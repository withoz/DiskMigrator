using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Partitioning;

/// <summary>축소 클론 판정 결과 — 어느 파티션을 얼마로 줄이면 대상에 들어가는지.</summary>
/// <param name="PartitionNumber">줄일 파티션 번호(원본에서 가장 큰 NTFS).</param>
/// <param name="CurrentBytes">그 파티션의 현재 크기.</param>
/// <param name="NewBytes">대상에 들어가는 목표 크기(1MB 정렬 여유 포함).</param>
/// <param name="EstimatedUsedBytes">
/// 그 파티션의 실사용 추정(바이트). 볼륨이 마운트돼 있지 않아 알 수 없으면 -1.
/// 임시 백업 이미지의 대략적 크기 예측(스마트 백업은 사용분만 저장)에도 씁니다.
/// </param>
public sealed record ShrinkCloneDecision(
    int PartitionNumber, long CurrentBytes, long NewBytes, long EstimatedUsedBytes);

/// <summary>
/// 대상이 원본보다 작고 맞춤 클론(제자리)도 안 될 때, <b>자동 축소 클론</b>이 가능한지 판정합니다
/// (순수 계산 — 디스크에 손대지 않음).
/// </summary>
/// <remarks>
/// 축소 클론은 원본을 수정하지 않기 위해 내부적으로 백업→(차등 자식에서) 축소→복원으로
/// 실행됩니다(확정 설계). 이 판정기는 그 경로를 태워도 되는지를 시작 전에 결정합니다:
/// 원본에서 가장 큰 NTFS 파티션을 골라, 복원 쪽 자동 축소(<c>ResolveAutoShrink</c>)와 같은
/// 규칙으로 필요한 축소량을 계산하고, 실사용량이 알려져 있으면 "줄여도 데이터가 들어가는지"까지
/// 확인합니다. 실제 축소 한계는 실행 시 Windows 축소기가 최종 강제합니다.
/// </remarks>
public static class ShrinkClonePlanner
{
    /// <summary>안전 여유 — 축소 목표가 실사용보다 최소 이만큼은 커야 진행합니다.</summary>
    public const long UsedHeadroomBytes = 1L << 30; // 1 GiB

    /// <summary>
    /// 축소 클론이 가능하면 결정을, 불가능하면 null과 <paramref name="blockedReason"/>을 돌려줍니다.
    /// 축소가 필요 없는 조합(대상이 충분히 큼)에서도 null(사유 없음)입니다.
    /// </summary>
    public static ShrinkCloneDecision? Evaluate(
        IReadOnlyList<PartitionInfo> sourcePartitions, long targetSizeBytes, out string? blockedReason)
    {
        ArgumentNullException.ThrowIfNull(sourcePartitions);
        blockedReason = null;

        if (sourcePartitions.Count == 0 || targetSizeBytes <= 0) return null;

        long occupiedEnd = sourcePartitions.Max(p => p.EndOffset);
        long maxEnd = targetSizeBytes - targetSizeBytes % ResizePlanner.Alignment - ResizePlanner.EndReserve;
        long deltaNeeded = occupiedEnd - maxEnd;
        if (deltaNeeded <= 0) return null;   // 축소 없이 들어감(맞춤 클론 경로).

        // 원본은 살아있는 디스크라 파일시스템이 감지돼 있습니다 — NTFS만 후보로 둡니다.
        var candidate = sourcePartitions
            .Where(p => string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LengthBytes)
            .FirstOrDefault();
        if (candidate is null)
        {
            blockedReason = Strings.Get("ShrinkCloneNoNtfs");
            return null;
        }

        // 줄일 양을 1MB 올림으로 잡아 확실히 들어가게 합니다.
        long delta = deltaNeeded % ResizePlanner.Alignment == 0
            ? deltaNeeded
            : deltaNeeded + (ResizePlanner.Alignment - deltaNeeded % ResizePlanner.Alignment);
        long newBytes = candidate.LengthBytes - delta;

        if (newBytes < (1L << 30))
        {
            blockedReason = Strings.Format("ShrinkCloneTooSmallFmt", FormatSize(targetSizeBytes));
            return null;
        }

        // 실사용량을 알면(마운트된 볼륨) 줄인 크기에 데이터가 들어가는지 미리 봅니다. 여기서
        // 통과해도 이동 불가 파일 때문에 실제 축소가 덜 될 수 있지만, 그건 실행 시 명확한
        // 오류로 중단됩니다 — 확실히 불가능한 조합만 시작 전에 거릅니다.
        long used = candidate.FreeSpaceBytes is { } free and >= 0
            ? candidate.LengthBytes - free
            : -1;
        if (used >= 0 && newBytes < used + UsedHeadroomBytes)
        {
            blockedReason = Strings.Format("ShrinkCloneDataTooBigFmt",
                FormatSize(used), FormatSize(newBytes));
            return null;
        }

        return new ShrinkCloneDecision(candidate.Number, candidate.LengthBytes, newBytes, used);
    }

    private static string FormatSize(long bytes) => Util.SizeFormatter.Format(bytes);
}
