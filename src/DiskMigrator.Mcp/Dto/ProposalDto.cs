namespace DiskMigrator.Mcp.Dto;

/// <summary>제안 상태 응답.</summary>
/// <param name="Id">제안 식별자 — get_proposal_status에 씁니다.</param>
/// <param name="Status">Pending · Applied · Dismissed · Superseded · Expired.</param>
/// <param name="Note">
/// 이 상태가 <b>무엇을 뜻하는지</b>. 특히 Applied는 "실행됐다"가 아니라 "양식이 채워졌다"입니다.
/// </param>
/// <param name="NeedsTypedConfirmation">
/// 적용 후에도 모델명 입력이 필요한지. <b>Claude는 대신할 수 없습니다.</b>
/// </param>
/// <param name="ExpiresUtc">이 시각이 지나면 만료합니다.</param>
public sealed record ProposalDto(
    string Id,
    string Status,
    string Note,
    int SourceDeviceNumber,
    string SourceModel,
    int TargetDeviceNumber,
    string TargetModel,
    string Reason,
    bool NeedsTypedConfirmation,
    DateTime CreatedUtc,
    DateTime ExpiresUtc);
