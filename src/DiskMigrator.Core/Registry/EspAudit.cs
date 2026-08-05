using System.Security.Cryptography.X509Certificates;

namespace DiskMigrator.Core.Registry;

/// <summary>부팅 관리자의 서명 정보.</summary>
/// <param name="Issuer">서명서 발급자 전체 문자열.</param>
/// <param name="Authority">발급 기관 분류 — <see cref="SigningAuthority"/> 참고.</param>
/// <param name="NotBefore">서명서 유효 시작.</param>
/// <param name="NotAfter">서명서 유효 만료.</param>
public sealed record BootManagerSignature(
    string Issuer,
    string Authority,
    DateTime? NotBefore,
    DateTime? NotAfter);

/// <summary>서명 발급 기관 분류. 구형 보드 호환성 판정에 씁니다.</summary>
public static class SigningAuthority
{
    /// <summary>Microsoft Windows Production PCA 2011 — 사실상 모든 UEFI 보드가 신뢰합니다.</summary>
    public const string Pca2011 = "PCA2011";

    /// <summary>
    /// Windows UEFI CA 2023 — <b>2023년 이전 보드의 Secure Boot DB에는 없을 수 있습니다.</b>
    /// </summary>
    public const string Ca2023 = "CA2023";

    /// <summary>Microsoft 서명이지만 위 둘로 분류되지 않음.</summary>
    public const string OtherMicrosoft = "OTHER_MICROSOFT";

    /// <summary>Microsoft가 아니거나 알 수 없음.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>
    /// 서명서 발급자 문자열을 위 분류 중 하나로 판정합니다.
    /// </summary>
    /// <remarks>
    /// 분류를 따로 떼어 둔 이유는 테스트 때문입니다. 2023 CA로 서명된 부팅 관리자는
    /// 흔치 않아 실물을 구하기 어렵지만, 이 판정이 틀리면 구형 보드 호환성 조언이 통째로
    /// 어긋납니다 — 문자열만 있으면 검증할 수 있게 열어 둡니다.
    /// </remarks>
    public static string Classify(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return Unknown;

        if (issuer.Contains("Production PCA 2011", StringComparison.OrdinalIgnoreCase)) return Pca2011;
        if (issuer.Contains("UEFI CA 2023", StringComparison.OrdinalIgnoreCase)) return Ca2023;
        if (issuer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return OtherMicrosoft;

        return Unknown;
    }
}

/// <summary>ESP 안의 파일 하나.</summary>
public sealed record EspFile(string RelativePath, long SizeBytes, DateTime LastWriteUtc);

/// <summary>ESP 감사 결과.</summary>
/// <param name="Uefi">ESP를 찾았는지(UEFI 구성인지).</param>
/// <param name="BootManagerPresent"><c>EFI\Microsoft\Boot\bootmgfw.efi</c>가 있는지.</param>
/// <param name="FallbackPresent">
/// <c>EFI\Boot\bootx64.efi</c>(대체 경로)가 있는지. NVRAM 부팅 항목이 없을 때 펌웨어가 찾는 곳입니다.
/// </param>
/// <param name="BcdPresent">BCD 스토어가 있는지.</param>
/// <param name="Signature">부팅 관리자 서명. 읽지 못하면 null.</param>
/// <param name="KeyFiles">핵심 부팅 파일들의 크기·시각.</param>
/// <param name="TotalFileCount">ESP 전체 파일 수.</param>
/// <param name="TotalSizeBytes">ESP 전체 사용량.</param>
/// <param name="ForeignBootFolders">
/// Microsoft·Boot 외의 부팅 폴더. 다른 도구가 남긴 잔재일 수 있습니다.
/// </param>
public sealed record EspAuditResult(
    bool Uefi,
    bool BootManagerPresent,
    bool FallbackPresent,
    bool BcdPresent,
    BootManagerSignature? Signature,
    IReadOnlyList<EspFile> KeyFiles,
    int TotalFileCount,
    long TotalSizeBytes,
    IReadOnlyList<string> ForeignBootFolders);

/// <summary>
/// EFI 시스템 파티션(ESP)을 훑어 부팅에 필요한 것이 갖춰졌는지, <b>부팅 관리자가 어느 인증서로
/// 서명됐는지</b>를 확인합니다.
/// </summary>
/// <remarks>
/// 서명 발급자를 보는 이유가 실무적으로 중요합니다. Windows 부팅 관리자는 오랫동안
/// <b>Production PCA 2011</b>로 서명돼 왔고 거의 모든 UEFI 보드가 이를 신뢰합니다. 그런데 최근
/// 도구들이 <b>Windows UEFI CA 2023</b>으로 서명된 부팅 관리자를 넣기도 하는데, 2023년 이전에
/// 나온 보드의 Secure Boot DB에는 그 인증서가 없어 검증에 실패합니다 — 펌웨어에 따라 오류 화면
/// 없이 그냥 멈춥니다.
///
/// <para>2026-08-04 조사에서 실제로 겪었습니다. 오프라인 ESP에 <c>bcdboot</c>을 돌렸더니
/// <b>실행한 PC의 Secure Boot 상태를 기준으로</b> 2023 서명본을 넣어버렸고, 대상은 2018년 보드였습니다.
/// 백업에서 되돌려 해결했지만, 그런 상태를 미리 알아볼 수 있어야 합니다.</para>
/// </remarks>
public static class EspAudit
{
    private const string BootManagerRel = @"EFI\Microsoft\Boot\bootmgfw.efi";
    private const string FallbackRel = @"EFI\Boot\bootx64.efi";
    private const string BcdRel = @"EFI\Microsoft\Boot\BCD";

    /// <param name="espRoot">ESP 루트. 예: <c>"S:\"</c> 또는 <c>"\\?\Volume{...}\"</c>.</param>
    public static EspAuditResult Inspect(string espRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(espRoot);
        string root = espRoot.EndsWith('\\') ? espRoot : espRoot + "\\";

        string bootMgr = Path.Combine(root, BootManagerRel);
        bool hasMgr = SafeExists(bootMgr);
        bool hasFallback = SafeExists(Path.Combine(root, FallbackRel));
        bool hasBcd = SafeExists(Path.Combine(root, BcdRel));

        var keyFiles = new List<EspFile>();
        foreach (string rel in new[] { BootManagerRel, FallbackRel, BcdRel, @"EFI\Microsoft\Boot\bootmgr.efi" })
        {
            try
            {
                var fi = new FileInfo(Path.Combine(root, rel));
                if (fi.Exists) keyFiles.Add(new EspFile(rel, fi.Length, fi.LastWriteTimeUtc));
            }
            catch { /* 한 파일 때문에 감사를 멈추지 않습니다. */ }
        }

        int count = 0;
        long total = 0;
        var foreign = new List<string>();
        try
        {
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                count++;
                total += fi.Length;
            }

            // EFI 아래에서 Microsoft·Boot가 아닌 폴더 = 다른 부팅 도구의 흔적일 수 있습니다.
            var efi = new DirectoryInfo(Path.Combine(root, "EFI"));
            if (efi.Exists)
            {
                foreach (var d in efi.EnumerateDirectories())
                {
                    if (d.Name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Equals("Boot", StringComparison.OrdinalIgnoreCase)) continue;
                    foreign.Add(d.Name);
                }
            }

            // ESP 루트에 바로 놓인 폴더도 확인합니다(EFI 밖에 남기는 도구가 있습니다).
            foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (d.Name.Equals("EFI", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                foreign.Add(d.Name);
            }
        }
        catch { /* 열거 실패는 개수만 못 채울 뿐, 나머지 판정은 유효합니다. */ }

        return new EspAuditResult(
            Uefi: hasMgr || hasFallback || hasBcd,
            BootManagerPresent: hasMgr,
            FallbackPresent: hasFallback,
            BcdPresent: hasBcd,
            Signature: hasMgr ? ReadSignature(bootMgr) : null,
            KeyFiles: keyFiles,
            TotalFileCount: count,
            TotalSizeBytes: total,
            ForeignBootFolders: foreign);
    }

    /// <summary>서명된 실행 파일에서 발급자를 읽습니다. 서명이 없거나 못 읽으면 null.</summary>
    /// <remarks>
    /// 서명 <b>유효성</b>을 검증하지는 않습니다 — 여기서 알고 싶은 것은 "어느 기관이 발급했나"이고,
    /// 그것만으로 구형 보드에서의 검증 가능성을 가늠할 수 있습니다.
    /// </remarks>
    private static BootManagerSignature? ReadSignature(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile은 서명 추출에 여전히 유효한 경로입니다.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return new BootManagerSignature(
                cert.Issuer, SigningAuthority.Classify(cert.Issuer), cert.NotBefore, cert.NotAfter);
        }
        catch
        {
            // 서명이 없거나 형식을 못 읽는 경우 — 판정하지 않습니다.
            return null;
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}
