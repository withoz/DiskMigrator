using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// 대상에 아무것도 쓰지 않고 만들어 본 복사 계획. Dispose하면 스냅샷이 삭제됩니다.
/// </summary>
/// <remarks>
/// 몇 시간짜리 되돌릴 수 없는 작업을 시작하기 전에, 위험한 부분만 몇 초 만에 확인하기
/// 위한 것입니다: VSS 스냅샷이 실제로 떠지는가, 실제 파티션 레이아웃에서 구간 조립이
/// 맞는가, 스냅샷 장치가 읽히는가.
/// </remarks>
public sealed class ClonePreview : IDisposable
{
    private readonly List<IDisposable> _resources;
    private bool _disposed;

    public required DiskInfo Source { get; init; }

    public required IReadOnlyList<CopyRegion> Regions { get; init; }

    /// <summary>VSS 스냅샷을 썼다면 그 시점(UTC).</summary>
    public DateTime? SnapshotTimeUtc { get; init; }

    /// <summary>VSS로 스냅샷을 뜨지 못해 원시로 복사될 파티션 (예: FAT32인 EFI 파티션).</summary>
    public IReadOnlyList<string> UnsnapshottedPartitions { get; init; } = [];

    public long TotalBytes => Regions.Sum(r => r.Length);

    /// <summary>스냅샷에서 읽어올 바이트 수 — 이 값이 0이면 스냅샷이 실질적으로 안 쓰이는 것입니다.</summary>
    public long BytesFromSnapshot =>
        Regions.Where(r => !ReferenceEquals(r.Source, null) && r.Description.Contains("스냅샷"))
               .Sum(r => r.Length);

    internal ClonePreview(List<IDisposable> resources) => _resources = resources;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            try { _resources[i].Dispose(); } catch { /* 정리 중 오류는 무시 */ }
        }

        _resources.Clear();
    }
}
