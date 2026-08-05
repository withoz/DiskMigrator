using System.Collections.Concurrent;

namespace DiskMigrator.Mcp;

/// <summary>Claude가 한 번 호출한 기록.</summary>
/// <param name="At">호출 시각(로컬).</param>
/// <param name="Tool">도구 이름. 도구 호출이 아니면 <c>initialize</c> 같은 프로토콜 메서드.</param>
/// <param name="Detail">무엇을 대상으로 했는지(디스크 번호·경로 등). 없으면 빈 문자열.</param>
/// <param name="Kind">이 호출이 무엇을 하는 종류인지 — 사용자가 한눈에 보게.</param>
/// <param name="Failed">거절·오류로 끝났는지.</param>
/// <param name="ElapsedMs">걸린 시간.</param>
public sealed record McpActivity(
    DateTime At, string Tool, string Detail, McpActivityKind Kind, bool Failed, long ElapsedMs);

/// <summary>호출의 성격. 화면에서 색·배지로 구분합니다.</summary>
public enum McpActivityKind
{
    /// <summary>연결·목록 조회 같은 프로토콜 수준 호출.</summary>
    Protocol,

    /// <summary>디스크·이미지를 <b>읽기만</b> 하는 진단·계획 도구.</summary>
    Read,

    /// <summary>화면에 확인 카드를 띄우는 제안. <b>실행하지는 않습니다.</b></summary>
    Proposal,

    /// <summary>진행 중 작업 취소 요청.</summary>
    Cancel,
}

/// <summary>
/// Claude가 이 앱에 무엇을 물었는지 최근 기록을 들고 있습니다.
/// </summary>
/// <remarks>
/// 통로를 열어 두는 동안 사용자는 Claude가 무엇을 읽었는지 <b>대화창으로만</b> 알 수 있었습니다.
/// 앱은 "읽기만 합니다"라고 말하면서 정작 무엇을 읽었는지는 보여주지 않았습니다 — 그 말을
/// 확인할 방법이 사용자에게 없었다는 뜻입니다.
///
/// <para>파일 로그에도 남지만, 로그를 열어 보라고 하는 것은 답이 아닙니다. 화면에서 바로
/// 보여야 합니다.</para>
///
/// <para><b>최근 것만 들고 있습니다.</b> 오래 켜 두면 호출이 수백 건이 되는데, 전부 쌓아 두면
/// 메모리도 화면도 의미가 없어집니다. 영구 기록은 파일 로그의 몫입니다.</para>
/// </remarks>
public sealed class McpActivityLog
{
    /// <summary>화면에 들고 있을 최대 건수.</summary>
    public const int Capacity = 100;

    private readonly ConcurrentQueue<McpActivity> _items = new();

    /// <summary>새 기록이 추가되면 발생합니다. <b>MCP 스레드에서 옵니다</b> — UI로 넘길 것.</summary>
    public event EventHandler<McpActivity>? Recorded;

    public void Record(McpActivity activity)
    {
        _items.Enqueue(activity);
        while (_items.Count > Capacity) _items.TryDequeue(out _);
        Recorded?.Invoke(this, activity);
    }

    /// <summary>지금까지의 기록(오래된 것부터).</summary>
    public IReadOnlyList<McpActivity> Snapshot() => [.. _items];

    /// <summary>도구 이름으로 성격을 판정합니다.</summary>
    /// <remarks>
    /// 이름으로 가르는 것이 못 미더워 보일 수 있지만, 실제 안전장치는 여기가 아니라 타입에
    /// 있습니다 — 진단 도구는 <see cref="IDiskReader"/>만 받아 쓰기 통로에 닿지 못합니다.
    /// 이 판정은 <b>표시용</b>이며, 모르는 이름은 가장 조심스러운 쪽으로 분류하지 않고
    /// 있는 그대로 프로토콜로 둡니다(없는 안전을 있는 것처럼 보이게 하지 않기 위해).
    /// </remarks>
    public static McpActivityKind Classify(string tool) => tool switch
    {
        "cancel_operation" => McpActivityKind.Cancel,
        var t when t.StartsWith("propose_", StringComparison.Ordinal) => McpActivityKind.Proposal,
        "initialize" or "tools/list" or "notifications/initialized" => McpActivityKind.Protocol,
        _ => McpActivityKind.Read,
    };
}
