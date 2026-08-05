namespace DiskMigrator.Mcp.Dto;

/// <summary>부팅 준비 검사 결과.</summary>
/// <param name="WouldBoot">
/// 치명 항목이 <b>모두 명시적으로 통과</b>했는지. "확인 못 함"은 통과로 치지 않습니다 —
/// 볼륨이 마운트되지 않아 못 본 것을 낙관하면 안 되기 때문입니다.
/// </param>
/// <param name="HasWarnings">같은 하드웨어면 뜨지만 다른 PC에서 문제될 수 있는 항목이 있는지.</param>
/// <param name="Summary">Claude가 그대로 인용할 수 있는 한 줄 요약.</param>
/// <param name="Items">개별 검사 항목.</param>
public sealed record BootCheckDto(
    bool WouldBoot,
    bool HasWarnings,
    string Summary,
    IReadOnlyList<BootCheckItemDto> Items);

/// <summary>검사 항목 하나.</summary>
/// <param name="Name">항목 이름(앱 언어로 현지화된 표시용 문구).</param>
/// <param name="Passed">통과 여부. <b>확인 자체가 불가능하면 null</b>이며, 이는 실패와 다릅니다.</param>
/// <param name="Severity">Fatal·Warning·Info.</param>
/// <param name="Detail">무엇을 봤고 무엇이 문제인지.</param>
/// <param name="Code">
/// 언어와 무관한 식별자. <b>Claude는 문구가 아니라 이 코드로 판단해야 합니다</b> —
/// 앱 언어가 바뀌면 Name·Detail은 달라집니다.
/// </param>
public sealed record BootCheckItemDto(
    string Name,
    bool? Passed,
    string Severity,
    string Detail,
    string? Code);
