namespace DiskMigrator.Mcp.Dto;

/// <summary>ESP(EFI 시스템 파티션) 감사 결과.</summary>
/// <param name="Uefi">UEFI 부팅 구성인지.</param>
/// <param name="BootManagerPresent">부팅 관리자(bootmgfw.efi)가 있는지.</param>
/// <param name="FallbackPresent">
/// 대체 경로(EFI\Boot\bootx64.efi)가 있는지. NVRAM 부팅 항목이 없을 때 펌웨어가 찾는 곳입니다.
/// </param>
/// <param name="BcdPresent">BCD 스토어가 있는지.</param>
/// <param name="Signature">부팅 관리자 서명 — 구형 보드 호환성 판정의 핵심입니다.</param>
/// <param name="KeyFiles">핵심 부팅 파일의 크기·시각.</param>
/// <param name="TotalFileCount">ESP 전체 파일 수.</param>
/// <param name="TotalSizeBytes">ESP 사용량.</param>
/// <param name="ForeignBootFolders">Microsoft·Boot 외의 부팅 폴더 — 다른 도구가 남긴 잔재일 수 있습니다.</param>
/// <param name="Summary">한 줄 요약.</param>
public sealed record EspAuditDto(
    bool Uefi,
    bool BootManagerPresent,
    bool FallbackPresent,
    bool BcdPresent,
    SignatureDto? Signature,
    IReadOnlyList<EspFileDto> KeyFiles,
    int TotalFileCount,
    long TotalSizeBytes,
    IReadOnlyList<string> ForeignBootFolders,
    string Summary);

/// <summary>부팅 관리자 서명.</summary>
/// <param name="Issuer">발급자 전체 문자열.</param>
/// <param name="Authority">
/// PCA2011 · CA2023 · OTHER_MICROSOFT · UNKNOWN.
/// <b>CA2023이면 2023년 이전 보드에서 Secure Boot 검증에 실패할 수 있습니다.</b>
/// </param>
/// <param name="Compatibility">그 발급자가 구형 보드에서 어떤 의미인지.</param>
public sealed record SignatureDto(
    string Issuer,
    string Authority,
    DateTime? NotBefore,
    DateTime? NotAfter,
    string Compatibility);

/// <summary>ESP 파일 하나.</summary>
public sealed record EspFileDto(string RelativePath, long SizeBytes, DateTime LastWriteUtc);
