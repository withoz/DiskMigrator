using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;

namespace DiskMigrator.Mcp;

/// <summary>
/// 디스크를 <b>읽기만</b> 하는 통로. 진단 도구는 이것만 받습니다.
/// </summary>
/// <remarks>
/// 계획서 §4의 첫 원칙("읽기와 쓰기를 계층에서 분리한다")을 <b>타입으로</b> 강제하기 위한 인터페이스입니다.
///
/// <para><see cref="IDiskService"/>를 그대로 주입하면 <c>SafeRemoveAsync</c> 같은 부작용 있는 메서드에
/// 손이 닿습니다. 지금은 아무도 부르지 않더라도, 나중에 도구를 추가하는 사람이 무심코 쓸 수 있습니다.
/// 애초에 보이지 않게 하는 편이 확실합니다 — 규율이 아니라 컴파일러가 지키게.</para>
/// </remarks>
public interface IDiskReader
{
    /// <summary>연결된 물리 디스크를 열거합니다.</summary>
    Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default);

    /// <summary>현재 프로세스가 관리자 권한인지.</summary>
    bool IsElevated { get; }
}

/// <summary>
/// <see cref="IDiskService"/>에서 읽기 기능만 꺼내 보여주는 어댑터.
/// </summary>
/// <remarks>
/// 감싸는 서비스는 private이며 밖으로 노출되지 않습니다 — 여기서 새는 것을 막는 것이 이 클래스의 전부입니다.
/// </remarks>
public sealed class DiskServiceReader(IDiskService inner) : IDiskReader
{
    public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
        inner.EnumerateDisksAsync(ct);

    public bool IsElevated => inner.IsElevated;
}
