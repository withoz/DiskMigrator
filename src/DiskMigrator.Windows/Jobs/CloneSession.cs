using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// 실행 준비가 끝난 클론 작업 하나. 열려 있는 장치·스냅샷·볼륨 잠금을 모두 소유합니다.
/// </summary>
/// <remarks>
/// 이 객체를 Dispose하기 전까지 스냅샷과 볼륨 잠금이 유지됩니다. 순서가 중요해서
/// 한 곳에 모아 두었습니다 — 예를 들어 GPT 보정은 대상 장치가 아직 열려 있는 동안
/// 해야 하므로, 엔진이 끝났다고 바로 Dispose하면 안 됩니다.
/// </remarks>
public sealed class CloneSession : IDisposable
{
    private readonly List<IDisposable> _resources;
    private bool _disposed;

    public required ClonePlan Plan { get; init; }

    /// <summary>쓰기용으로 열린 대상 장치. GPT 보정에 필요합니다.</summary>
    public required IBlockDevice TargetDevice { get; init; }

    /// <summary>VSS 스냅샷을 썼다면 그 시점(UTC). 오프라인 클론이면 null.</summary>
    public DateTime? SnapshotTimeUtc { get; init; }

    /// <summary>VSS로 스냅샷을 뜨지 못해 원시로 복사한 파티션 설명 (예: EFI 파티션).</summary>
    public IReadOnlyList<string> UnsnapshottedPartitions { get; init; } = [];

    internal CloneSession(List<IDisposable> resources)
    {
        _resources = resources;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 역순으로 정리합니다: 대상 장치(및 볼륨 잠금)를 먼저 닫고, 그다음 스냅샷,
        // 마지막으로 원본 장치. 반대로 하면 잠금이 먼저 풀려 Windows가 볼륨을
        // 다시 마운트한 상태에서 쓰기가 마무리될 수 있습니다.
        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            try
            {
                _resources[i].Dispose();
            }
            catch
            {
                // 정리 중 오류는 삼킵니다 — 나머지 자원은 반드시 해제해야 합니다.
            }
        }

        _resources.Clear();
    }
}
