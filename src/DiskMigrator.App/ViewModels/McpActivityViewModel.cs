using DiskMigrator.App.Localization;
using DiskMigrator.Mcp;

namespace DiskMigrator.App.ViewModels;

/// <summary>
/// Claude의 호출 한 건을 화면에 보여주는 형태로 감쌉니다.
/// </summary>
/// <remarks>
/// 이 목록의 목적은 감사입니다 — "읽기만 한다"는 앱의 말을 사용자가 눈으로 확인하는 것.
/// 그래서 <b>종류 배지</b>가 도구 이름만큼 중요합니다. 무엇을 불렀는지보다
/// "그게 읽는 것이었나 쓰는 것이었나"가 사용자의 관심사입니다.
/// </remarks>
public sealed class McpActivityViewModel(McpActivity activity)
{
    public string Time { get; } = activity.At.ToString("HH:mm:ss");

    public string Tool { get; } = activity.Tool;

    public string Detail { get; } = activity.Detail;

    public bool HasDetail { get; } = activity.Detail.Length > 0;

    /// <summary>걸린 시간. 1초 미만은 밀리초로.</summary>
    public string Elapsed { get; } = activity.ElapsedMs >= 1000
        ? $"{activity.ElapsedMs / 1000.0:0.0}s"
        : $"{activity.ElapsedMs}ms";

    public bool Failed { get; } = activity.Failed;

    /// <summary>종류 배지 문구.</summary>
    public string KindLabel { get; } = activity.Kind switch
    {
        McpActivityKind.Read => Strings.Get("McpActRead"),
        McpActivityKind.Proposal => Strings.Get("McpActProposal"),
        McpActivityKind.Cancel => Strings.Get("McpActCancel"),
        _ => Strings.Get("McpActProtocol"),
    };

    /// <summary>제안은 화면에 카드를 띄우므로 다른 색으로 구분합니다.</summary>
    public bool IsProposal { get; } = activity.Kind == McpActivityKind.Proposal;

    public bool IsRead { get; } = activity.Kind == McpActivityKind.Read;
}
