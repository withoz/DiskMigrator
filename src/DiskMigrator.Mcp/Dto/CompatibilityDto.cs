namespace DiskMigrator.Mcp.Dto;

/// <summary>이 PC가 그 디스크로 부팅할 수 있는가 — 판정과 근거.</summary>
/// <param name="Verdict">Supported · Uncertain · Unsupported · Unknown.</param>
/// <param name="Confidence">
/// Certain · High · Low. <b>확신도 없이 "메인보드를 바꾸세요"라고 말해서는 안 됩니다.</b>
/// </param>
/// <param name="Reason">그렇게 판정한 근거.</param>
/// <param name="Advice">할 수 있는 일 — "안 됩니다"로 끝내지 않습니다.</param>
/// <param name="UserChecks">
/// 우리가 볼 수 없어 <b>사용자에게 물어야 하는</b> 항목(부팅 메뉴에 뜨는지, 콜드 부팅이 되는지 등).
/// </param>
/// <param name="Firmware">판정에 쓴 이 PC의 펌웨어 정보.</param>
/// <param name="TargetBusType">대상 디스크의 연결 방식.</param>
public sealed record CompatibilityDto(
    string Verdict,
    string Confidence,
    string Reason,
    string Advice,
    IReadOnlyList<string> UserChecks,
    FirmwareDto Firmware,
    string TargetBusType);

/// <summary>이 PC의 펌웨어·메인보드 정보.</summary>
/// <param name="BiosReleaseDate">세대 판정의 1차 근거.</param>
/// <param name="IsUefi">UEFI로 부팅했는지. false면 레거시(CSM)입니다.</param>
/// <param name="SecureBootEnabled">확인 불가면 null — 꺼짐으로 단정하지 않습니다.</param>
public sealed record FirmwareDto(
    string? BoardManufacturer,
    string? BoardProduct,
    string? BiosVendor,
    string? BiosVersion,
    DateTime? BiosReleaseDate,
    string? SmbiosVersion,
    bool IsUefi,
    bool? SecureBootEnabled);
