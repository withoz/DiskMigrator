using System.Runtime.Versioning;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Jobs;

namespace DiskMigrator.Mcp;

/// <summary>
/// 복사 계획을 <b>세워보기만</b> 하는 통로. 계획 도구는 이것만 받습니다.
/// </summary>
/// <remarks>
/// <see cref="CloneSessionFactory"/>를 그대로 주입하면 <c>CreateAsync</c> — 실제 클론 세션을
/// 만드는 메서드 — 에 손이 닿습니다. 지금은 아무도 부르지 않더라도, 나중에 도구를 추가하는
/// 사람이 무심코 쓸 수 있습니다. <see cref="IDiskReader"/>와 같은 이유로 표면을 좁힙니다.
/// </remarks>
public interface IClonePlanner
{
    /// <summary>
    /// 복사 계획을 계산합니다. <b>어떤 디스크에도 쓰지 않습니다.</b>
    /// </summary>
    /// <param name="source">읽을 디스크.</param>
    /// <param name="useSnapshot">
    /// VSS 스냅샷을 만들지. <b>계획만 볼 때는 false</b>여야 합니다 — 스냅샷은 시스템 리소스를
    /// 잡았다 놓는 부작용이 있고, 구간 배치를 계산하는 데는 필요하지 않습니다.
    /// </param>
    Task<ClonePreview> PreviewAsync(DiskInfo source, bool useSnapshot, CancellationToken ct = default);
}

/// <summary>
/// <see cref="CloneSessionFactory"/>에서 계획 기능만 꺼내 보여주는 어댑터.
/// </summary>
/// <remarks>
/// 감싸는 팩토리는 private이며 밖으로 노출되지 않습니다. 또한 <c>skipUnusedBlocks</c>를 항상
/// false로 고정합니다 — 스마트 클론은 스냅샷 볼륨의 할당 정보를 읽어야 하므로, 스냅샷 없이
/// 계획할 때는 쓸 수 없습니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CloneSessionPlanner(CloneSessionFactory inner) : IClonePlanner
{
    public Task<ClonePreview> PreviewAsync(DiskInfo source, bool useSnapshot, CancellationToken ct = default) =>
        inner.PreviewAsync(source, useSnapshot, skipUnusedBlocks: false, resizeLayout: null, ct);
}
