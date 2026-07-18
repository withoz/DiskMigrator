using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Safety;

/// <summary>안전 점검에서 발견된 사항 한 건.</summary>
/// <param name="Severity">심각도. Blocker면 어떤 확인으로도 진행 불가.</param>
/// <param name="Code">기계 판독용 코드. 로그·테스트에서 문자열 비교 대신 사용합니다.</param>
/// <param name="Message">사용자에게 보여줄 설명.</param>
public sealed record SafetyIssue(SafetySeverity Severity, string Code, string Message)
{
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}

/// <summary>안전 점검 전체 결과.</summary>
public sealed class SafetyReport
{
    public required IReadOnlyList<SafetyIssue> Issues { get; init; }

    /// <summary>진행 자체가 불가능한 사유들.</summary>
    public IEnumerable<SafetyIssue> Blockers =>
        Issues.Where(i => i.Severity == SafetySeverity.Blocker);

    /// <summary>사용자 확인이 필요한 사유들.</summary>
    public IEnumerable<SafetyIssue> Confirmations =>
        Issues.Where(i => i.Severity == SafetySeverity.RequiresConfirmation);

    public IEnumerable<SafetyIssue> Warnings =>
        Issues.Where(i => i.Severity == SafetySeverity.Warning);

    /// <summary>Blocker가 하나도 없어야 사용자 확인 단계로 넘어갈 수 있습니다.</summary>
    public bool CanProceed => !Blockers.Any();

    /// <summary>사용자가 대상 디스크 모델명을 직접 입력해야 하는지.</summary>
    public bool NeedsTypedConfirmation => Confirmations.Any();
}
