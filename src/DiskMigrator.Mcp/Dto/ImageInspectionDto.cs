namespace DiskMigrator.Mcp.Dto;

/// <summary>백업 이미지(.vhdx) 무결성 검사 결과.</summary>
/// <param name="Ok">복원해도 되는 상태인지.</param>
/// <param name="Summary">한 줄 요약.</param>
/// <param name="Items">개별 검사 항목.</param>
public sealed record ImageInspectionDto(
    bool Ok,
    string Summary,
    IReadOnlyList<ImageCheckItemDto> Items);

/// <summary>검사 항목 하나.</summary>
public sealed record ImageCheckItemDto(string Name, bool Passed, string Detail);
