namespace DiskMigrator.Mcp.Dto;

/// <summary>원본→대상 조합의 안전성 판정.</summary>
/// <param name="CanProceed">차단 사유 없이 진행할 수 있는지.</param>
/// <param name="NeedsTypedConfirmation">
/// 대상에 데이터가 있어 <b>모델명을 직접 입력해야</b> 실행되는지.
/// Claude는 이 확인을 대신할 수 없습니다 — 사람이 앱 화면에서 해야 합니다.
/// </param>
/// <param name="Summary">한 줄 요약. 차단 사유가 있으면 그것부터 말합니다.</param>
/// <param name="Blockers">진행을 막는 사유. 하나라도 있으면 실행되지 않습니다.</param>
/// <param name="Confirmations">타이핑 확인이 필요한 사유(대개 "대상에 데이터가 있음").</param>
/// <param name="Warnings">막지는 않지만 알아야 할 것.</param>
/// <param name="Notes">
/// 위험은 아니지만 <b>화면이 사용자에게 말해 주는</b> 사실 — 남는 공간, USB 연결 같은 것.
/// </param>
/// <param name="Source">원본 디스크 요약.</param>
/// <param name="Target">대상 디스크 요약 — <b>이 디스크의 데이터가 지워집니다.</b></param>
public sealed record SafetyDto(
    bool CanProceed,
    bool NeedsTypedConfirmation,
    string Summary,
    IReadOnlyList<SafetyIssueDto> Blockers,
    IReadOnlyList<SafetyIssueDto> Confirmations,
    IReadOnlyList<SafetyIssueDto> Warnings,
    IReadOnlyList<SafetyIssueDto> Notes,
    DiskDto Source,
    DiskDto Target);

/// <summary>안전성 항목 하나.</summary>
/// <param name="Code">
/// 언어와 무관한 식별자. <b>Claude는 문구가 아니라 이 코드로 판단해야 합니다</b> —
/// 예: SAME_DISK, TARGET_IS_SYSTEM_DISK, TARGET_TOO_SMALL, SOURCE_HIBERNATED.
/// </param>
/// <param name="Severity">Blocker · RequiresConfirmation · Warning · Info.</param>
/// <param name="Message">사용자에게 보여줄 설명(앱 언어).</param>
public sealed record SafetyIssueDto(string Code, string Severity, string Message);
