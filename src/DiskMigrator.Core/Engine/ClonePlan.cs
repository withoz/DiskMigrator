using DiskMigrator.Core.Abstractions;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// 한 원본 구간을 대상의 한 오프셋으로 복사하는 단위 작업.
/// </summary>
/// <remarks>
/// 이 추상화 덕분에 전혀 다른 두 시나리오가 같은 엔진을 씁니다.
/// - 오프라인 전체 클론: 원시 디스크에서 [0, 크기) 구간 하나.
/// - VSS 핫 클론: 파티션 테이블은 원시 디스크에서, 각 파티션 데이터는
///   그 볼륨의 스냅샷 장치에서 — 서로 다른 Source를 가진 여러 구간.
/// </remarks>
public sealed class CopyRegion
{
    /// <summary>읽어올 장치. 구간마다 다를 수 있습니다.</summary>
    public required IBlockDevice Source { get; init; }

    /// <summary>원본 장치 내 시작 오프셋(바이트). 섹터 정렬되어야 합니다.</summary>
    public required long SourceOffset { get; init; }

    /// <summary>대상 디스크 내 시작 오프셋(바이트). 섹터 정렬되어야 합니다.</summary>
    public required long TargetOffset { get; init; }

    /// <summary>복사할 길이(바이트). 섹터 정렬되어야 합니다.</summary>
    public required long Length { get; init; }

    /// <summary>진행 표시와 로그에 쓰는 설명 (예: "파티션 2 (C:)").</summary>
    public required string Description { get; init; }

    /// <summary>
    /// 이 구간의 소스가 복제 내내 고정되어 있어, 복제 후 소스를 다시 읽어 대상과 비교하는
    /// 검증이 의미가 있는지.
    /// </summary>
    /// <remarks>
    /// VSS 스냅샷에서 읽는 구간은 고정이므로 true입니다.
    ///
    /// 반면 라이브 디스크에서 원시로 읽는 구간(FAT32라 스냅샷 못 뜨는 EFI, MSR, 파티션 테이블,
    /// GPT 백업 헤더)은 false입니다. 이 영역은 복제 시점과 검증 시점 사이에 실행 중인
    /// Windows가 값을 바꿀 수 있어, 검증 때 라이브 디스크를 다시 읽으면 "움직이는 과녁"과
    /// 비교하게 됩니다. 실기에서 EFI 파티션의 FAT 타임스탬프 한 바이트가 복제~검증 사이에
    /// 바뀌어 가짜 불일치가 났습니다. 이런 구간은 검증에서 제외합니다.
    /// </remarks>
    public bool IsStableForVerification { get; init; } = true;

    public override string ToString() =>
        $"{Description}: {Source.Id}[{SourceOffset:N0}] → [{TargetOffset:N0}] ({Length:N0} bytes)";
}

/// <summary>실행할 클론 작업 전체. 엔진에 넘기기 전에 <see cref="Validate"/>로 검증합니다.</summary>
public sealed class ClonePlan
{
    /// <summary>쓸 대상 장치. 이 장치의 데이터는 파괴됩니다.</summary>
    public required IBlockDevice Target { get; init; }

    public required IReadOnlyList<CopyRegion> Regions { get; init; }

    /// <summary>사람이 읽을 작업 이름 (로그 헤더에 사용).</summary>
    public string Name { get; init; } = "디스크 클론";

    public long TotalBytes => Regions.Sum(r => r.Length);

    /// <summary>
    /// 계획이 물리적으로 실행 가능한지 검사합니다. 엔진이 실행 직전에 호출합니다.
    /// </summary>
    /// <exception cref="InvalidOperationException">계획이 잘못된 경우.</exception>
    public void Validate()
    {
        if (!Target.CanWrite)
        {
            throw new InvalidOperationException($"대상 장치 {Target.Id} 을(를) 쓰기용으로 열지 못했습니다.");
        }

        if (Regions.Count == 0)
        {
            throw new InvalidOperationException("복사할 구간이 없습니다.");
        }

        int sector = Target.SectorSize;

        foreach (var region in Regions)
        {
            if (region.Length <= 0)
            {
                throw new InvalidOperationException($"{region.Description}: 길이가 0 이하입니다.");
            }

            if (region.SourceOffset % region.Source.SectorSize != 0 ||
                region.Length % region.Source.SectorSize != 0)
            {
                throw new InvalidOperationException(
                    $"{region.Description}: 원본 오프셋/길이가 섹터({region.Source.SectorSize}바이트) 정렬이 아닙니다.");
            }

            if (region.TargetOffset % sector != 0 || region.Length % sector != 0)
            {
                throw new InvalidOperationException(
                    $"{region.Description}: 대상 오프셋/길이가 섹터({sector}바이트) 정렬이 아닙니다.");
            }

            if (region.SourceOffset + region.Length > region.Source.Length)
            {
                throw new InvalidOperationException(
                    $"{region.Description}: 원본 장치 끝을 넘어서 읽으려 합니다.");
            }

            if (region.TargetOffset + region.Length > Target.Length)
            {
                throw new InvalidOperationException(
                    $"{region.Description}: 대상 장치 끝을 넘어서 쓰려 합니다. " +
                    $"대상 크기 {Target.Length:N0}바이트, 필요 {region.TargetOffset + region.Length:N0}바이트.");
            }
        }

        // 구간끼리 대상 영역이 겹치면 나중 구간이 앞 구간을 덮어써 조용히 깨집니다.
        var ordered = Regions.OrderBy(r => r.TargetOffset).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            if (prev.TargetOffset + prev.Length > curr.TargetOffset)
            {
                throw new InvalidOperationException(
                    $"구간이 대상에서 겹칩니다: '{prev.Description}' 과 '{curr.Description}'.");
            }
        }
    }
}
