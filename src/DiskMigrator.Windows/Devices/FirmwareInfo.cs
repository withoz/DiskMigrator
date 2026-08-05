using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DiskMigrator.Windows.Devices;

/// <summary>이 PC의 펌웨어·메인보드 정보. 호환성 판정의 입력입니다.</summary>
/// <param name="BoardManufacturer">메인보드 제조사(예: ASRock, ASUSTeK).</param>
/// <param name="BoardProduct">메인보드 모델명.</param>
/// <param name="BiosVendor">BIOS 제조사.</param>
/// <param name="BiosVersion">BIOS 버전 문자열.</param>
/// <param name="BiosReleaseDate">
/// BIOS 릴리스 날짜. <b>세대 판정의 1차 근거</b>입니다 — NVMe 부팅 지원은 펌웨어 세대에 달렸습니다.
/// </param>
/// <param name="SmbiosVersion">SMBIOS 버전(예: "3.8"). 세대 보조 근거.</param>
/// <param name="IsUefi">UEFI로 부팅했는지. false면 Legacy(CSM) 부팅입니다.</param>
/// <param name="SecureBootEnabled">Secure Boot 상태. 확인 불가면 null.</param>
public sealed record FirmwareInfoResult(
    string? BoardManufacturer,
    string? BoardProduct,
    string? BiosVendor,
    string? BiosVersion,
    DateTime? BiosReleaseDate,
    string? SmbiosVersion,
    bool IsUefi,
    bool? SecureBootEnabled);

/// <summary>
/// 메인보드·펌웨어 정보를 읽습니다 — "이 PC가 그 디스크로 부팅할 수 있는가"를 가늠하는 근거입니다.
/// </summary>
/// <remarks>
/// 2026-08-04 조사에서 이 정보가 있었다면 며칠을 아꼈을 것입니다. 대상은 2018년 보드였고
/// 최신 BIOS에도 NVMe 개선이 없었는데, 그걸 확인하는 데만 한참 걸렸습니다.
///
/// <para><b>주의</b>: 여기서 읽는 것은 <b>이 앱이 돌고 있는 PC</b>의 정보입니다. 디스크를 옮겨 갈
/// 대상 PC의 정보가 아닙니다. 대상 PC에서 진단 리포트를 만들어 오는 것이 정확한 방법입니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FirmwareInfo
{
    public static FirmwareInfoResult Read()
    {
        var (boardMfr, boardProd) = ReadBaseBoard();
        var (biosVendor, biosVer, biosDate, smbios) = ReadBios();

        return new FirmwareInfoResult(
            BoardManufacturer: boardMfr,
            BoardProduct: boardProd,
            BiosVendor: biosVendor,
            BiosVersion: biosVer,
            BiosReleaseDate: biosDate,
            SmbiosVersion: smbios,
            IsUefi: IsUefiBoot(),
            SecureBootEnabled: ReadSecureBoot());
    }

    private static (string? Manufacturer, string? Product) ReadBaseBoard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                using (o)
                {
                    return (Str(o["Manufacturer"]), Str(o["Product"]));
                }
            }
        }
        catch { /* WMI가 막힌 환경(WinPE 등)에서는 그냥 모르는 채로 둡니다. */ }
        return (null, null);
    }

    private static (string? Vendor, string? Version, DateTime? Date, string? Smbios) ReadBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                using (o)
                {
                    DateTime? date = null;
                    if (o["ReleaseDate"] is string raw && raw.Length >= 8)
                    {
                        // WMI DATETIME은 yyyyMMddHHmmss.ffffff±UUU 형식입니다.
                        try { date = ManagementDateTimeConverter.ToDateTime(raw); }
                        catch { /* 형식이 어긋나면 날짜만 포기합니다. */ }
                    }

                    string? smbios = null;
                    if (o["SMBIOSMajorVersion"] is not null && o["SMBIOSMinorVersion"] is not null)
                        smbios = $"{o["SMBIOSMajorVersion"]}.{o["SMBIOSMinorVersion"]}";

                    return (Str(o["Manufacturer"]), Str(o["SMBIOSBIOSVersion"]), date, smbios);
                }
            }
        }
        catch { }
        return (null, null, null, null);
    }

    /// <summary>UEFI로 부팅했는지. 레지스트리의 PEFirmwareType이 2면 UEFI입니다.</summary>
    private static bool IsUefiBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            if (key?.GetValue("PEFirmwareType") is int t) return t == 2;
        }
        catch { }

        // 폴백: 환경 변수(설정돼 있으면 신뢰할 만합니다).
        string? env = Environment.GetEnvironmentVariable("firmware_type");
        if (!string.IsNullOrEmpty(env)) return env.Contains("UEFI", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    /// <summary>Secure Boot 상태. 확인할 수 없으면 null — 꺼짐으로 단정하지 않습니다.</summary>
    private static bool? ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int v) return v == 1;
        }
        catch { }
        return null;
    }

    private static string? Str(object? o)
    {
        string? s = o?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
