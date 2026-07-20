namespace DiskMigrator.Core.Models;

/// <summary>배치 막대 한 조각의 성격.</summary>
public enum DiskSpanKind
{
    /// <summary>파티션이 차지한 구간.</summary>
    Partition,

    /// <summary>어떤 파티션에도 속하지 않는 빈 구간.</summary>
    Unallocated,
}

/// <summary>디스크를 앞에서 뒤로 훑으며 나눈 한 구간.</summary>
/// <param name="Kind">파티션인지 미할당인지.</param>
/// <param name="StartOffset">디스크 내 시작 오프셋(바이트).</param>
/// <param name="LengthBytes">구간 길이(바이트).</param>
/// <param name="TrueFraction">디스크 전체에서 차지하는 진짜 비율(0~1).</param>
/// <param name="DisplayFraction">
/// 화면에 그릴 때 쓸 비율(0~1). 너무 작아 보이지 않는 구간에 최소 폭을 준 뒤 전체를 다시
/// 정규화한 값이라 <paramref name="TrueFraction"/>과 다를 수 있습니다. 모든 구간의 합은 1입니다.
/// </param>
/// <param name="Partition">파티션 구간이면 그 정보, 미할당이면 null.</param>
public sealed record DiskLayoutSpan(
    DiskSpanKind Kind,
    long StartOffset,
    long LengthBytes,
    double TrueFraction,
    double DisplayFraction,
    PartitionInfo? Partition)
{
    public long EndOffset => StartOffset + LengthBytes;
}

/// <summary>
/// 디스크의 파티션 배치를 "앞에서 뒤로 이어지는 구간 목록"으로 펼칩니다(빈 공간 포함).
/// </summary>
/// <remarks>
/// 화면에 배치 막대를 그리기 위한 순수 계산입니다. UI에 의존하지 않으므로 단위 테스트로
/// 검증할 수 있고, 색·글자 같은 표현은 상위(App)가 입힙니다.
///
/// <para>이 표현이 중요한 이유: "더 작은 디스크로 옮길 수 있는가"는 사용한 데이터 양이 아니라
/// <b>파티션이 차지한 끝</b>의 문제입니다(<see cref="Partitioning.ResizePlanner.MinimumTargetSize"/>).
/// 뒤쪽 빈 공간을 눈으로 보여주면 사용자가 그 판단을 직접 할 수 있습니다.</para>
/// </remarks>
public static class DiskLayoutMap
{
    /// <summary>
    /// 화면에서 사라지지 않도록 각 구간에 보장하는 최소 표시 비율.
    /// </summary>
    /// <remarks>
    /// 1TB 디스크의 100MB ESP는 0.01%라 그대로 그리면 한 픽셀도 안 됩니다. 디스크 관리 도구들이
    /// 하듯 최소 폭을 주고 전체를 다시 정규화합니다. 비율이 왜곡되므로 실제 크기는 항상 글자로
    /// 함께 보여줘야 합니다.
    /// </remarks>
    public const double MinDisplayFraction = 0.02;

    /// <summary>이보다 작은 틈은 미할당으로 취급하지 않습니다(정렬 여백·GPT 헤더 영역 노이즈).</summary>
    public const long GapNoiseThreshold = 32L * 1024 * 1024;

    /// <summary>파티션이 실제로 차지한 끝(마지막 파티션의 끝). 파티션이 없으면 0.</summary>
    public static long OccupiedEnd(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.Partitions.Count == 0 ? 0 : disk.Partitions.Max(p => p.EndOffset);
    }

    /// <summary>마지막 파티션 뒤에 남은 빈 공간(바이트). 없으면 0.</summary>
    public static long TrailingFreeBytes(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return Math.Max(0, disk.SizeBytes - OccupiedEnd(disk));
    }

    /// <summary>
    /// 디스크를 구간 목록으로 펼칩니다. 파티션이 없으면 디스크 전체가 미할당 한 조각이 됩니다.
    /// </summary>
    public static IReadOnlyList<DiskLayoutSpan> Build(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (disk.SizeBytes <= 0) return [];

        var ordered = disk.Partitions.OrderBy(p => p.StartingOffset).ToList();

        // (구간, 길이) 초안 — 비율은 정규화 후에 채웁니다.
        var draft = new List<(DiskSpanKind Kind, long Start, long Length, PartitionInfo? Part)>();
        long cursor = 0;

        foreach (var p in ordered)
        {
            long gap = p.StartingOffset - cursor;
            if (gap >= GapNoiseThreshold)
            {
                draft.Add((DiskSpanKind.Unallocated, cursor, gap, null));
            }

            // 겹치거나 디스크를 넘는 파티션이 들어와도 막대가 깨지지 않게 잘라 맞춥니다.
            long start = Math.Max(cursor, p.StartingOffset);
            long end = Math.Min(disk.SizeBytes, p.EndOffset);
            if (end > start)
            {
                draft.Add((DiskSpanKind.Partition, start, end - start, p));
                cursor = end;
            }
        }

        long trailing = disk.SizeBytes - cursor;
        if (trailing >= GapNoiseThreshold || draft.Count == 0)
        {
            draft.Add((DiskSpanKind.Unallocated, cursor, Math.Max(0, trailing), null));
        }

        // 최소 폭 보정 후 정규화 — 합이 정확히 1이어야 막대가 컨테이너에 딱 맞습니다.
        var floored = draft
            .Select(d => Math.Max((double)d.Length / disk.SizeBytes, MinDisplayFraction))
            .ToList();
        double total = floored.Sum();

        return draft
            .Select((d, i) => new DiskLayoutSpan(
                d.Kind,
                d.Start,
                d.Length,
                (double)d.Length / disk.SizeBytes,
                total > 0 ? floored[i] / total : 0,
                d.Part))
            .ToList();
    }
}
