namespace DiskMigrator.Mcp.Dto;

/// <summary>진단 리포트를 저장한 결과.</summary>
/// <param name="Path">저장된 파일 경로.</param>
/// <param name="SizeBytes">파일 크기 — 옮기기 좋은 크기인지 판단할 수 있게.</param>
/// <param name="CollectedUtc">수집 시각.</param>
/// <param name="Summary">파일을 열지 않고도 상황을 알 수 있는 요약.</param>
public sealed record DiagnosticSavedDto(string Path, long SizeBytes, DateTime CollectedUtc, string Summary);
