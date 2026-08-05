using DiskMigrator.Core.Models;

namespace DiskMigrator.Mcp.Proposals;

/// <summary>제안이 어떻게 됐는지.</summary>
public enum ProposalStatus
{
    /// <summary>카드가 떠 있고 사용자 반응을 기다리는 중.</summary>
    Pending,

    /// <summary>사용자가 적용했습니다. <b>아직 실행 전</b>이며 모델명 입력이 남아 있습니다.</summary>
    Applied,

    /// <summary>사용자가 무시했습니다.</summary>
    Dismissed,

    /// <summary>새 제안이 덮어썼습니다.</summary>
    Superseded,

    /// <summary>디스크 구성이 바뀌었거나 시간이 지나 무효가 됐습니다.</summary>
    Expired,
}

/// <summary>
/// 디스크를 식별하는 지문. 장치 번호만으로는 부족합니다 — USB를 다시 꽂으면 번호가 바뀝니다.
/// </summary>
/// <remarks>
/// 제안을 만든 뒤 사용자가 디스크를 바꿔 꽂았을 수 있습니다. 적용 시점에 이 지문을 다시 대조해,
/// 다른 디스크에 제안이 적용되는 일을 막습니다 — 기존 <c>AssertTargetUnchanged</c>와 같은 발상입니다.
/// </remarks>
/// <param name="DeviceNumber">제안 당시의 장치 번호.</param>
/// <param name="Model">모델명.</param>
/// <param name="SerialNumber">시리얼(없을 수 있음).</param>
/// <param name="SizeBytes">크기.</param>
public sealed record DiskFingerprint(int DeviceNumber, string Model, string? SerialNumber, long SizeBytes)
{
    public static DiskFingerprint Of(DiskInfo d) =>
        new(d.DeviceNumber, d.Model, d.SerialNumber, d.SizeBytes);

    /// <summary>같은 디스크로 볼 수 있는지 — 번호가 아니라 <b>정체</b>를 봅니다.</summary>
    public bool Matches(DiskInfo d) =>
        string.Equals(Model, d.Model, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SerialNumber, d.SerialNumber, StringComparison.OrdinalIgnoreCase) &&
        SizeBytes == d.SizeBytes;
}

/// <summary>
/// Claude가 올린 복제 제안. <b>이것만으로는 아무 일도 일어나지 않습니다.</b>
/// </summary>
/// <param name="Id">제안 식별자 — Claude가 상태를 조회할 때 씁니다.</param>
/// <param name="Source">원본 디스크 지문.</param>
/// <param name="Target">대상 디스크 지문 — <b>이 디스크가 지워집니다.</b></param>
/// <param name="Reason">Claude가 왜 이 제안을 했는지. 사용자가 카드에서 읽습니다.</param>
/// <param name="UseSnapshot">스냅샷 사용 여부 제안.</param>
/// <param name="VerifyAfterCopy">복제 후 검증 여부 제안.</param>
/// <param name="NeedsTypedConfirmation">
/// 적용 후에도 모델명 입력이 필요한지. <b>Claude는 이것을 대신할 수 없습니다.</b>
/// </param>
/// <param name="CreatedUtc">만든 시각.</param>
/// <param name="Status">현재 상태.</param>
public sealed record CloneProposal(
    string Id,
    DiskFingerprint Source,
    DiskFingerprint Target,
    string Reason,
    bool UseSnapshot,
    bool VerifyAfterCopy,
    bool NeedsTypedConfirmation,
    DateTime CreatedUtc,
    ProposalStatus Status)
{
    /// <summary>제안이 유효한 시간. 지나면 만료합니다.</summary>
    /// <remarks>
    /// 오래된 제안을 무심코 적용하는 것을 막습니다. 사용자가 자리를 비운 사이 상황이 달라졌을 수
    /// 있고, 그때의 판단이 지금도 맞다는 보장이 없습니다.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public bool IsExpiredAt(DateTime utcNow) => utcNow - CreatedUtc > Lifetime;

    /// <summary>사용자가 아직 반응하지 않았고 시간도 남았는지.</summary>
    public bool IsLiveAt(DateTime utcNow) => Status == ProposalStatus.Pending && !IsExpiredAt(utcNow);
}
