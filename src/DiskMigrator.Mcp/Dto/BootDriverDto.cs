namespace DiskMigrator.Mcp.Dto;

/// <summary>부팅 시작 드라이버 조사 결과.</summary>
/// <param name="ControlSet">읽은 컨트롤 세트.</param>
/// <param name="TotalCount">Start=0 드라이버 총 개수.</param>
/// <param name="MissingCount">
/// <b>파일이 없는 드라이버 수. 0이 아니면 부팅이 그 지점에서 멈출 수 있습니다.</b>
/// </param>
/// <param name="OutsideSystem32Count">표준 위치 밖의 드라이버 수(서드파티 의심).</param>
/// <param name="Summary">Claude가 그대로 인용할 수 있는 한 줄 요약.</param>
/// <param name="Missing">파일이 없는 드라이버 — 문제의 직접 후보입니다.</param>
/// <param name="OutsideSystem32">표준 위치 밖의 드라이버. 정상 제품일 수도 있어 자동 판단하지 않습니다.</param>
public sealed record BootDriverInventoryDto(
    string ControlSet,
    int TotalCount,
    int MissingCount,
    int OutsideSystem32Count,
    string Summary,
    IReadOnlyList<BootDriverDto> Missing,
    IReadOnlyList<BootDriverDto> OutsideSystem32);

/// <summary>드라이버 하나.</summary>
public sealed record BootDriverDto(
    string ServiceName,
    string Group,
    string ImagePath,
    string ResolvedPath,
    bool FileExists,
    long? FileSizeBytes);

/// <summary>빠른 시작(재개) 상태.</summary>
/// <param name="HiberbootEnabled">1이면 종료할 때마다 재개 이미지가 다시 만들어집니다. 값 없으면 null.</param>
/// <param name="HibernateEnabled">최대 절전 설정. 값 없으면 null.</param>
/// <param name="HiberfilExists">지금 재개 이미지가 있는지.</param>
/// <param name="HiberfilSizeBytes">이미지 크기.</param>
/// <param name="ResumeWouldBeAttempted">
/// 다음 부팅에서 winresume 경로를 탈 가능성이 있는지 — <b>참이면 다른 하드웨어에서 멈출 수 있습니다.</b>
/// </param>
/// <param name="Summary">한 줄 요약.</param>
public sealed record FastStartupDto(
    uint? HiberbootEnabled,
    uint? HibernateEnabled,
    bool HiberfilExists,
    long? HiberfilSizeBytes,
    bool ResumeWouldBeAttempted,
    string Summary);
