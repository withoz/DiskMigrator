using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Partitioning;

/// <summary>파티션 하나를 확대하라는 요청.</summary>
/// <param name="PartitionNumber">확대할 파티션 번호(<see cref="PartitionInfo.Number"/>).</param>
/// <param name="NewLengthBytes">
/// 원하는 새 크기(바이트). <c>null</c>이면 대상에 남는 공간을 <b>전부</b> 이 파티션에 몰아줍니다.
/// </param>
public sealed record PartitionGrowRequest(int PartitionNumber, long? NewLengthBytes);

/// <summary>대상 디스크에서 파티션 하나의 최종 배치.</summary>
/// <param name="SourceNumber">원본 파티션 번호(데이터를 어디서 복사할지 대응).</param>
/// <param name="StartingOffset">대상에서의 시작 오프셋(바이트).</param>
/// <param name="LengthBytes">대상에서의 크기(바이트).</param>
/// <param name="Grown">원본보다 커졌는지(커졌으면 클론 후 파일시스템 확장이 필요).</param>
public sealed record TargetPartition(int SourceNumber, long StartingOffset, long LengthBytes, bool Grown)
{
    /// <summary>파티션 끝의 배타적 오프셋.</summary>
    public long EndOffset => StartingOffset + LengthBytes;
}

/// <summary>확대 계획의 결과 — 각 파티션이 대상 어디에 놓이는지.</summary>
public sealed class ResizeLayout
{
    public required IReadOnlyList<TargetPartition> Partitions { get; init; }

    /// <summary>확대된 파티션(없으면 null).</summary>
    public TargetPartition? GrownPartition => Partitions.FirstOrDefault(p => p.Grown);
}

/// <summary>
/// 대상 디스크에서 파티션을 확대하는 배치를 계산합니다(<b>확대 전용</b>).
/// </summary>
/// <remarks>
/// 고른 파티션을 넓히고, 그 <b>뒤</b>의 파티션들을 넓힌 만큼 오른쪽으로 밉니다. 앞 파티션은
/// 그대로 둡니다. 전형적인 Windows 디스크 <c>[ESP][MSR][C:][복구]</c>에서 C:는 마지막이
/// 아니므로(뒤에 복구 파티션), C:를 넓히려면 복구 파티션을 뒤로 밀어야 합니다 — 그것이 이
/// 계획기의 목적입니다.
///
/// 이 클래스는 순수 계산입니다(디스크에 손대지 않음). 실제 데이터 복사·GPT 재작성·NTFS
/// 확장은 상위 계층이 이 배치를 받아 수행합니다. 축소(대상 &lt; 원본)는 파일시스템 인식
/// 클러스터 재배치가 필요해 범위 밖입니다(다음 버전).
///
/// <para><b>정렬 규칙:</b> 확대량(delta)을 1MB 배수로 맞춥니다. 원본 파티션이 1MB 정렬돼
/// 있으면(Windows 표준) 뒤 파티션을 delta만큼 밀어도 정렬이 유지되고, 정렬이 아니었더라도
/// 원본의 정렬 잔차가 보존됩니다.</para>
/// </remarks>
public static class ResizePlanner
{
    /// <summary>파티션 정렬 단위(바이트). Windows 표준인 1MiB.</summary>
    public const long Alignment = 1L << 20;

    /// <summary>
    /// 디스크 끝에 백업 GPT를 놓을 예약 공간. 실제 백업 GPT(헤더 1 + 엔트리배열 32섹터 ≈ 17KB)보다
    /// 넉넉하며, 마지막 파티션 끝을 1MB 경계 안쪽으로 유지합니다.
    /// </summary>
    public const long EndReserve = Alignment;

    /// <summary>
    /// 이 파티션 배치를 담으려면 대상 디스크가 최소 몇 바이트여야 하는지(끝의 백업 GPT 예약 포함).
    /// </summary>
    /// <remarks>
    /// 원본 디스크의 <b>전체 크기</b>가 아니라 <b>파티션이 실제로 차지한 끝</b>을 기준으로 합니다.
    /// 뒤쪽이 비어 있는 디스크(예: 1TB에 파티션은 200GB만)는 그보다 작은 대상에도 그대로 들어갑니다.
    /// </remarks>
    public static long MinimumTargetSize(IReadOnlyList<PartitionInfo> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
            throw new ArgumentException("원본에 파티션이 없습니다.", nameof(source));

        return AlignUp(source.Max(p => p.EndOffset) + EndReserve, Alignment);
    }

    /// <summary>
    /// 원본 파티션 배치가 <paramref name="targetSizeBytes"/> 크기의 대상에 <b>그대로</b> 들어가는지.
    /// </summary>
    public static bool LayoutFitsIn(IReadOnlyList<PartitionInfo> source, long targetSizeBytes) =>
        source is { Count: > 0 } && targetSizeBytes >= MinimumTargetSize(source);

    /// <summary>
    /// 파티션을 <b>옮기지 않고</b> 그대로 두는 배치를 만듭니다(맞춤 클론).
    /// </summary>
    /// <remarks>
    /// 대상이 원본 디스크보다 작아도, 원본의 파티션이 모두 대상 안에 들어가면 복제할 수 있습니다.
    /// 파티션은 제자리에 복사하고, 원본 끝에 있던 GPT 백업 헤더는 복사하지 않은 채
    /// <see cref="GptRewriter"/>가 줄어든 대상의 끝에 다시 씁니다. 파티션 위치가 하나도 바뀌지
    /// 않으므로 파티션 내부(부트 구성·BCD가 참조하는 GUID 포함)는 영향을 받지 않습니다.
    ///
    /// <para>버려지는 것은 마지막 파티션 <b>뒤의 빈 공간</b>뿐입니다. 파일시스템을 줄이지 않으므로
    /// 원본에는 아무것도 쓰지 않습니다. 파티션 자체가 대상보다 큰 경우(진짜 NTFS 축소)는
    /// 이 경로가 아니라 별도 기능입니다.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">파티션이 대상에 들어가지 않을 때.</exception>
    public static ResizeLayout PlanFit(IReadOnlyList<PartitionInfo> source, long targetSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
            throw new ArgumentException("원본에 파티션이 없습니다.", nameof(source));

        var ordered = source.OrderBy(p => p.StartingOffset).ToList();
        long maxEnd = AlignDown(targetSizeBytes, Alignment) - EndReserve;

        if (ordered[^1].EndOffset > maxEnd)
        {
            throw new InvalidOperationException(
                $"원본 파티션이 대상에 들어가지 않습니다. 파티션이 {SizeGb(ordered[^1].EndOffset)}까지 " +
                $"차지하므로 대상이 최소 {SizeGb(MinimumTargetSize(ordered))}는 되어야 합니다 " +
                $"(대상 {SizeGb(targetSizeBytes)}).");
        }

        var result = ordered
            .Select(p => new TargetPartition(p.Number, p.StartingOffset, p.LengthBytes, Grown: false))
            .ToList();

        Validate(result, targetSizeBytes, maxEnd);
        return new ResizeLayout { Partitions = result };
    }

    /// <summary>
    /// 확대 배치를 계산합니다.
    /// </summary>
    /// <param name="source">원본 파티션 목록(순서 무관 — 내부에서 오프셋순 정렬).</param>
    /// <param name="targetSizeBytes">대상 디스크 전체 크기(바이트).</param>
    /// <param name="request">어느 파티션을 얼마나 확대할지.</param>
    /// <exception cref="ArgumentException">파티션 번호가 없거나 원본이 비었을 때.</exception>
    /// <exception cref="InvalidOperationException">확대가 불가능할 때(여유 없음·요청이 대상 초과 등).</exception>
    public static ResizeLayout Plan(
        IReadOnlyList<PartitionInfo> source,
        long targetSizeBytes,
        PartitionGrowRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        if (source.Count == 0)
            throw new ArgumentException("원본에 파티션이 없습니다.", nameof(source));

        var ordered = source.OrderBy(p => p.StartingOffset).ToList();
        int growIndex = ordered.FindIndex(p => p.Number == request.PartitionNumber);
        if (growIndex < 0)
            throw new ArgumentException(
                $"파티션 {request.PartitionNumber}을(를) 원본에서 찾지 못했습니다.", nameof(request));

        // 대상에서 파티션이 끝까지 쓸 수 있는 마지막 경계(백업 GPT 예약 + 1MB 정렬).
        long maxEnd = AlignDown(targetSizeBytes, Alignment) - EndReserve;

        long sourceLastEnd = ordered[^1].EndOffset;
        if (sourceLastEnd > maxEnd)
            throw new InvalidOperationException(
                "대상이 원본 레이아웃을 담기에 부족합니다. 확대는 대상이 원본보다 클 때만 가능합니다.");

        // 뒤로 밀 수 있는 최대 여유 = 마지막 파티션 끝을 maxEnd까지 밀 수 있는 양(1MB 정렬).
        long maxDelta = AlignDown(maxEnd - sourceLastEnd, Alignment);
        if (maxDelta <= 0)
            throw new InvalidOperationException(
                "대상에 확대할 여유 공간이 없습니다(원본과 크기가 거의 같습니다).");

        var grown = ordered[growIndex];
        long oldLen = grown.LengthBytes;

        long delta;
        if (request.NewLengthBytes is { } wanted)
        {
            long newLen = AlignUp(wanted, Alignment);
            if (newLen < oldLen)
                throw new InvalidOperationException(
                    $"새 크기({SizeGb(newLen)})가 현재 크기({SizeGb(oldLen)})보다 작습니다. " +
                    "이 버전은 확대만 지원합니다.");
            delta = newLen - oldLen;
            if (delta > maxDelta)
                throw new InvalidOperationException(
                    $"요청한 크기가 대상 용량을 넘습니다. 이 파티션은 최대 {SizeGb(oldLen + maxDelta)}까지 " +
                    $"확대할 수 있습니다(요청 {SizeGb(newLen)}).");
        }
        else
        {
            // 남는 공간 전부를 이 파티션에.
            delta = maxDelta;
        }

        var result = new List<TargetPartition>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (i < growIndex)
            {
                // 앞 파티션: 그대로.
                result.Add(new TargetPartition(p.Number, p.StartingOffset, p.LengthBytes, Grown: false));
            }
            else if (i == growIndex)
            {
                result.Add(new TargetPartition(p.Number, p.StartingOffset, oldLen + delta, Grown: delta > 0));
            }
            else
            {
                // 뒤 파티션: delta만큼 오른쪽으로.
                result.Add(new TargetPartition(p.Number, p.StartingOffset + delta, p.LengthBytes, Grown: false));
            }
        }

        Validate(result, targetSizeBytes, maxEnd);
        return new ResizeLayout { Partitions = result };
    }

    /// <summary>배치가 물리적으로 성립하는지 최종 확인(겹침 없음·대상 안에 맞음).</summary>
    private static void Validate(List<TargetPartition> layout, long targetSizeBytes, long maxEnd)
    {
        for (int i = 0; i < layout.Count; i++)
        {
            var p = layout[i];
            if (p.LengthBytes <= 0)
                throw new InvalidOperationException($"파티션 {p.SourceNumber}의 크기가 0 이하입니다.");
            if (p.StartingOffset < 0)
                throw new InvalidOperationException($"파티션 {p.SourceNumber}의 시작 오프셋이 음수입니다.");

            if (i > 0 && layout[i - 1].EndOffset > p.StartingOffset)
                throw new InvalidOperationException(
                    $"파티션 {layout[i - 1].SourceNumber}과(와) {p.SourceNumber}이(가) 겹칩니다.");
        }

        long lastEnd = layout[^1].EndOffset;
        if (lastEnd > maxEnd)
            throw new InvalidOperationException(
                $"파티션들이 대상 끝을 넘습니다(끝 {lastEnd:N0} > 한계 {maxEnd:N0}, 대상 {targetSizeBytes:N0}).");
    }

    private static long AlignDown(long value, long alignment) => value - (value % alignment);

    private static long AlignUp(long value, long alignment) =>
        value % alignment == 0 ? value : value + (alignment - value % alignment);

    private static string SizeGb(long bytes) => $"{bytes / 1_000_000_000.0:F1} GB";
}
