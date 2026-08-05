namespace DiskMigrator.Mcp.Dto;

/// <summary>복사 계획 요약.</summary>
/// <param name="Summary">한 줄 요약 — 무엇이 얼마나 복사되는지.</param>
/// <param name="RegionCount">복사 구간 수.</param>
/// <param name="TotalBytes">복사할 총량.</param>
/// <param name="TotalText">사람이 읽는 총량.</param>
/// <param name="Regions">구간별 내역(원본 오프셋·대상 오프셋·길이·설명).</param>
/// <param name="TargetFitsSource">대상이 원본의 배치를 그대로 담을 수 있는지.</param>
/// <param name="SpareBytesOnTarget">
/// 대상에서 남는 공간. 음수면 <b>그만큼 모자라</b> 축소가 필요합니다.
/// </param>
/// <param name="EstimatedMinutes">
/// 대략적인 소요 시간(분). 연결 방식과 실제 속도에 따라 크게 달라지므로 어림값입니다.
/// </param>
/// <param name="Caveats">
/// 이 계획의 한계 — <b>반드시 사용자에게 함께 전하십시오.</b> 스냅샷 없이 계산했으므로
/// 스마트 클론으로 줄어들 양은 반영되지 않았습니다.
/// </param>
/// <param name="Source">원본 디스크.</param>
/// <param name="Target">대상 디스크.</param>
public sealed record ClonePlanDto(
    string Summary,
    int RegionCount,
    long TotalBytes,
    string TotalText,
    IReadOnlyList<CopyRegionDto> Regions,
    bool TargetFitsSource,
    long SpareBytesOnTarget,
    int EstimatedMinutes,
    IReadOnlyList<string> Caveats,
    DiskDto Source,
    DiskDto Target);

/// <summary>복사 구간 하나.</summary>
/// <param name="Description">무엇을 복사하는 구간인지(예: 파티션 이름·역할).</param>
public sealed record CopyRegionDto(
    string Description,
    long SourceOffset,
    long TargetOffset,
    long Length,
    string LengthText);
