using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Partitioning;

/// <summary>축소 복원에서 한 구간을 이미지의 어디에서 대상의 어디로 옮길지(순수 좌표, 장치 무관).</summary>
/// <param name="SourceOffset">이미지(축소된 자식)에서 읽을 시작 오프셋(바이트).</param>
/// <param name="TargetOffset">대상 디스크에 쓸 시작 오프셋(바이트).</param>
/// <param name="Length">복사 길이(바이트).</param>
/// <param name="Description">로그·진행 표시용 설명.</param>
public sealed record CopyRegionSpec(long SourceOffset, long TargetOffset, long Length, string Description);

/// <summary>축소 복원의 복사 계획 — 복사 구간 목록 + 복사 후 GPT 재작성에 쓸 remap 목록.</summary>
public sealed record ShrinkRestorePlan(
    IReadOnlyList<CopyRegionSpec> Regions,
    IReadOnlyList<PartitionRemap> Remaps);

/// <summary>
/// 축소된 이미지를 <b>더 작은 대상</b>에 압축 복원하기 위한 복사 좌표를 계산합니다(순수 로직).
/// </summary>
/// <remarks>
/// 축소 흐름은 이렇습니다: <see cref="VhdxShrinker"/>가 이미지의 차등 자식에서 파티션을 줄이면,
/// 그 자식은 <c>[ESP][MSR][C: 축소됨][빈틈][복구@원래위치]</c> 모양이 됩니다(diskpart는 파티션을
/// 줄이기만 하고 뒤 파티션을 옮기지 않으므로 빈틈이 생김). 이 계획기는 <see
/// cref="ResizePlanner.PlanShrink"/>가 계산한 <b>압축 배치</b>(뒤 파티션을 왼쪽으로 당긴 최종 모습)를
/// 받아, 각 파티션을 <b>자식의 현재 위치</b>에서 <b>대상의 압축 위치</b>로 옮기는 복사 구간과, 복사
/// 후 대상 GPT를 그 배치로 고칠 remap을 만듭니다.
///
/// <para>중요한 전제: diskpart 축소는 파티션 <b>시작</b>을 바꾸지 않으므로, 자식에서 각 파티션의
/// 읽기 시작 = 원본 이미지에서의 시작과 같습니다. 그래서 <paramref name="sourcePartitions"/>(원본
/// 이미지 파티션 배치, 축소 전에 읽어 둠)의 시작 오프셋을 읽기 위치로 씁니다. 축소된 파티션의 복사
/// 길이는 배치가 정한 줄어든 길이입니다(줄인 파일시스템이 그 안에 들어감).</para>
///
/// <para>맨 앞의 <b>GPT 영역</b>(보호 MBR + 주 GPT 헤더·엔트리)은 첫 파티션 앞에서 그대로 복사한 뒤,
/// 상위 계층이 <see cref="GptRewriter"/>로 엔트리 위치를 이 remap대로 고치고 백업 GPT를 줄어든 대상
/// 끝에 다시 씁니다. 파티션 GUID는 보존되므로 부트 구성(BCD)이 참조하는 식별자는 그대로입니다.</para>
/// </remarks>
public static class ShrinkRestorePlanner
{
    /// <param name="sourcePartitions">원본 이미지의 파티션 배치(축소 <b>전</b>에 읽어 둔 것).</param>
    /// <param name="layout"><see cref="ResizePlanner.PlanShrink"/>가 만든 대상 압축 배치.</param>
    /// <param name="sectorSize">논리 섹터 크기(remap의 LBA 계산에 씀).</param>
    public static ShrinkRestorePlan Build(
        IReadOnlyList<PartitionInfo> sourcePartitions,
        ResizeLayout layout,
        int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(sourcePartitions);
        ArgumentNullException.ThrowIfNull(layout);
        if (sourcePartitions.Count == 0)
            throw new ArgumentException("원본에 파티션이 없습니다.", nameof(sourcePartitions));
        if (sectorSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "섹터 크기는 0보다 커야 합니다.");

        var orderedSource = sourcePartitions.OrderBy(p => p.StartingOffset).ToList();
        long firstStart = orderedSource[0].StartingOffset;
        if (firstStart <= 0)
        {
            throw new InvalidOperationException(
                "첫 파티션이 디스크 맨 앞에서 시작해 주 GPT 영역을 복사할 공간이 없습니다.");
        }

        // 보호 MBR + 주 GPT(헤더 + 엔트리 배열)는 첫 파티션 앞에 있습니다. 그대로 복사하면
        // 파티션 GUID·타입이 온전하고, 이후 GptRewriter가 위치만 remap대로 고칩니다.
        var regions = new List<CopyRegionSpec>
        {
            new(0, 0, firstStart, "GPT 영역(주)"),
        };
        var remaps = new List<PartitionRemap>();

        foreach (var tp in layout.Partitions.OrderBy(p => p.StartingOffset))
        {
            var src = orderedSource.FirstOrDefault(p => p.Number == tp.SourceNumber)
                ?? throw new InvalidOperationException(
                    $"배치의 파티션 {tp.SourceNumber}에 대응하는 원본 파티션을 찾지 못했습니다.");

            long len = tp.LengthBytes;
            if (len <= 0)
                throw new InvalidOperationException($"파티션 {tp.SourceNumber}의 복사 길이가 0 이하입니다.");

            string tag = tp.Shrunk ? "(축소)"
                : tp.StartingOffset != src.StartingOffset ? "(이동)"
                : "";
            regions.Add(new CopyRegionSpec(
                src.StartingOffset, tp.StartingOffset, len, $"파티션 {tp.SourceNumber}{tag}"));

            remaps.Add(new PartitionRemap(
                OldStartLba: src.StartingOffset / sectorSize,
                NewStartLba: tp.StartingOffset / sectorSize,
                NewEndLba: (tp.StartingOffset + len) / sectorSize - 1));
        }

        return new ShrinkRestorePlan(regions, remaps);
    }
}
