using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Partitioning;

/// <summary>
/// 복제가 끝나면 대상이 어떤 배치가 될지 미리 계산합니다(화면 미리보기용).
/// </summary>
/// <remarks>
/// 대상 막대는 <b>"지금 지워질 것"</b>을 보여줍니다. 남는 공간을 어떻게 할지 고르는 자리에서는
/// <b>"복제 후 이렇게 됩니다"</b>가 필요한데, 둘은 다른 정보입니다. 이걸 한 막대로 섞으면
/// 꽉 찬 대상 막대 아래에서 "남는 공간을 어떻게 할까요"를 묻는 모순이 생깁니다.
///
/// <para>디스크에 아무것도 쓰지 않는 순수 계산이라 단위 테스트로 검증합니다.</para>
/// </remarks>
public static class ProjectedLayout
{
    /// <summary>
    /// 복제 후 대상의 파티션 배치를 계산합니다. 계산할 수 없으면 <c>null</c>(화면에서 숨김).
    /// </summary>
    /// <param name="source">복제할 원본.</param>
    /// <param name="targetSizeBytes">대상 디스크 크기.</param>
    /// <param name="mode">남는 공간 처리 방식.</param>
    /// <param name="grow">
    /// <paramref name="mode"/>가 <see cref="FreeSpaceMode.GrowPartition"/>일 때 넓힐 파티션.
    /// </param>
    public static IReadOnlyList<PartitionInfo>? After(
        DiskInfo? source,
        long targetSizeBytes,
        FreeSpaceMode mode,
        PartitionGrowRequest? grow = null)
    {
        if (source is null || source.Partitions.Count == 0 || targetSizeBytes <= 0) return null;

        var ordered = source.Partitions.OrderBy(p => p.StartingOffset).ToList();

        // 파티션이 대상에 아예 안 들어가면 클론 자체가 불가능하므로 미리보기도 의미가 없습니다.
        if (!ResizePlanner.LayoutFitsIn(ordered, targetSizeBytes)) return null;

        return mode switch
        {
            FreeSpaceMode.GrowPartition when grow is not null => ProjectGrow(ordered, targetSizeBytes, grow),
            FreeSpaceMode.ExpandLast => ProjectExpandLast(ordered, targetSizeBytes),
            _ => ordered,   // Leave — 원본 배치 그대로, 뒤는 미할당
        };
    }

    /// <summary>마지막 파티션을 디스크 끝(백업 GPT 예약 앞)까지 늘립니다.</summary>
    private static IReadOnlyList<PartitionInfo> ProjectExpandLast(
        List<PartitionInfo> ordered, long targetSizeBytes)
    {
        long maxEnd = MaxUsableEnd(targetSizeBytes);
        var last = ordered[^1];

        // 이미 끝까지 차 있으면 바뀌는 것이 없습니다.
        if (last.EndOffset >= maxEnd) return ordered;

        var result = new List<PartitionInfo>(ordered.Count);
        result.AddRange(ordered.Take(ordered.Count - 1));
        result.Add(Resized(last, last.StartingOffset, maxEnd - last.StartingOffset));
        return result;
    }

    /// <summary>고른 파티션을 넓히고 그 뒤를 오른쪽으로 밉니다(<see cref="ResizePlanner"/> 규칙 그대로).</summary>
    private static IReadOnlyList<PartitionInfo>? ProjectGrow(
        List<PartitionInfo> ordered, long targetSizeBytes, PartitionGrowRequest grow)
    {
        ResizeLayout layout;
        try
        {
            layout = ResizePlanner.Plan(ordered, targetSizeBytes, grow);
        }
        catch (Exception)
        {
            // 넓힐 수 없는 요청(여유 없음·원본보다 작은 크기 등)은 미리보기를 숨깁니다.
            // 실제 시작 시에는 같은 계산이 사용자에게 이유를 알리며 막습니다.
            return null;
        }

        return layout.Partitions
            .Select(tp =>
            {
                var src = ordered.First(p => p.Number == tp.SourceNumber);
                return Resized(src, tp.StartingOffset, tp.LengthBytes);
            })
            .ToList();
    }

    /// <summary>
    /// 파티션을 새 위치·크기로 옮긴 사본. 볼륨 정보는 유지하되 <b>여유 공간은 다시 계산</b>합니다.
    /// </summary>
    /// <remarks>
    /// 파티션이 커져도 담긴 데이터 양은 그대로이므로, 늘어난 만큼이 곧 여유 공간이 됩니다.
    /// 이걸 안 고치면 미리보기 막대의 사용량 띠가 원래 비율로 남아 "넓혔는데 꽉 차 보이는"
    /// 이상한 그림이 됩니다.
    /// </remarks>
    private static PartitionInfo Resized(PartitionInfo src, long newStart, long newLength)
    {
        long? newFree = null;
        if (src.FreeSpaceBytes is { } free)
        {
            long used = Math.Max(0, src.LengthBytes - free);
            newFree = Math.Max(0, newLength - used);
        }

        return new PartitionInfo
        {
            Number = src.Number,
            StartingOffset = newStart,
            LengthBytes = newLength,
            DriveLetter = src.DriveLetter,
            VolumeGuidPath = src.VolumeGuidPath,
            FileSystem = src.FileSystem,
            VolumeLabel = src.VolumeLabel,
            FreeSpaceBytes = newFree,
            GptPartitionType = src.GptPartitionType,
            MbrPartitionType = src.MbrPartitionType,
            IsEfiSystemPartition = src.IsEfiSystemPartition,
            IsActive = src.IsActive,
        };
    }

    /// <summary>파티션이 쓸 수 있는 마지막 경계(끝의 백업 GPT 예약 제외).</summary>
    private static long MaxUsableEnd(long targetSizeBytes) =>
        targetSizeBytes - (targetSizeBytes % ResizePlanner.Alignment) - ResizePlanner.EndReserve;
}
