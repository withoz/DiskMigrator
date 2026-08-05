namespace DiskMigrator.Mcp.Dto;

/// <summary>부팅 실패 원인 분석.</summary>
/// <param name="Verdict">종합 판단 한 문장.</param>
/// <param name="Causes">가능성이 높은 순서의 원인 후보.</param>
/// <param name="UserChecks">
/// 우리가 볼 수 없어 <b>사용자에게 물어야 하는</b> 것. 추측으로 채우지 말고 그대로 물어보십시오.
/// </param>
/// <param name="Diagnostics">이 판단의 근거가 된 개별 진단 — 사용자가 검증할 수 있게 함께 실습니다.</param>
public sealed record BootFailureDto(
    string Verdict,
    IReadOnlyList<BootFailureCauseDto> Causes,
    IReadOnlyList<string> UserChecks,
    BootFailureEvidenceDto Diagnostics);

/// <summary>원인 후보 하나.</summary>
/// <param name="Code">언어 무관 식별자.</param>
/// <param name="Confidence">
/// Certain · High · Low. <b>Low를 단정처럼 말하지 마십시오</b> — 사용자가 멀쩡한 부품을 교체하게 됩니다.
/// </param>
/// <param name="Finding">무엇을 근거로 그렇게 보는지.</param>
/// <param name="Action">사용자가 할 수 있는 일.</param>
public sealed record BootFailureCauseDto(string Code, string Confidence, string Finding, string Action);

/// <summary>판단의 근거가 된 진단들. 실행하지 못한 항목은 null입니다.</summary>
public sealed record BootFailureEvidenceDto(
    BootCheckDto? BootCheck,
    BootDriverInventoryDto? BootDrivers,
    FastStartupDto? FastStartup,
    BootTraceDto? BootTrace,
    EspAuditDto? Esp);
