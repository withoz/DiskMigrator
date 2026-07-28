namespace DiskMigrator.Core.Registry;

/// <summary>
/// 부팅 초기에 로드되는 <b>비표준(서드파티) 드라이버</b>를 찾아냅니다 — 진단 전용입니다.
/// </summary>
/// <remarks>
/// 부팅 구성(BCD·저장소 드라이버·재개 이미지)이 모두 정상인데도 사본이 제조사 로고에서 멈추는
/// 일이 있습니다. 원인은 대개 <b>원본에 설치돼 있던 보안·DRM·디스크 보호 솔루션의 드라이버</b>가
/// 부팅 시작(Start 0·1)으로 올라와, 바뀐 하드웨어에서 초기화에 실패하는 것입니다(실기에서 규명).
///
/// <para>이런 드라이버는 <b>정상 제품일 수도</b> 있으므로 자동으로 끄지 않습니다. 검사에서
/// "이런 것들이 부팅 초기에 로드된다"는 사실만 보여 주어, 사용자가 원인을 눈으로 확인하고
/// 판단할 수 있게 합니다. 판별은 Windows 표준 드라이버 목록에 없고 <b>ImagePath가 비표준</b>인
/// 것(절대 경로 <c>\??\C:\…</c> 또는 <c>System32</c> 밖)을 기준으로 합니다.</para>
/// </remarks>
public static class ThirdPartyBootDriverScan
{
    /// <summary>
    /// 오탐을 줄이기 위한 Windows 기본 드라이버·서비스 이름(부팅/시스템 시작으로 흔한 것들).
    /// </summary>
    private static readonly HashSet<string> WindowsBuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // 저장소·볼륨
        "storahci", "stornvme", "storufs", "msahci", "pciide", "intelide", "atapi",
        "iaStorV", "iaStorAV", "iaStorAC", "iaStorE", "amdsata", "amdxata",
        "LSI_SAS", "LSI_SAS2", "LSI_SAS3", "vmbus", "storvsc", "vhdmp", "vdrvroot",
        "disk", "partmgr", "volmgr", "volmgrx", "volsnap", "volume", "mountmgr",
        "spaceport", "EhStorClass", "cdrom", "fvevol", "iorate", "rdyboost",
        // 파일시스템·필터
        "Ntfs", "FileInfo", "Wof", "wcifs", "bindflt", "CldFlt", "FileCrypt",
        "luafv", "npsvctrig", "Null", "wcnfs",
        // 커널·플랫폼
        "acpi", "acpiex", "acpipagr", "acpitime", "pci", "pcw", "msisadrv",
        "intelpep", "pdc", "CNG", "ksecdd", "ksecpkg", "tpm", "CompositeBus",
        "Wdf01000", "WindowsTrustedRT", "WindowsTrustedRTProxy", "ahcache",
        "BasicDisplay", "BasicRender", "UEFI", "hwpolicy", "clfs", "tcpip",
        // Defender
        "WdFilter", "WdNisDrv", "WdBoot", "WdDevFlt",
    };

    /// <summary>찾은 서드파티 부팅 드라이버.</summary>
    /// <param name="ServiceName">서비스 키 이름.</param>
    /// <param name="Start">Start 값(0=부팅 시작, 1=시스템 시작).</param>
    /// <param name="ImagePath">드라이버 파일 경로(레지스트리 값 그대로).</param>
    public sealed record Finding(string ServiceName, uint Start, string ImagePath);

    /// <summary>
    /// 지정한 컨트롤 세트에서 부팅 초기(Start 0·1)에 로드되는 비표준 드라이버를 찾습니다.
    /// </summary>
    public static IReadOnlyList<Finding> Scan(RegistryHive hive, string controlSet)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSet);

        string services = $"{controlSet}\\Services";
        if (!hive.KeyExists(services)) return [];

        var found = new List<Finding>();
        foreach (string svc in hive.EnumerateSubKeyNames(services))
        {
            if (WindowsBuiltIn.Contains(svc)) continue;

            uint? start = hive.GetDword($"{services}\\{svc}", "Start");
            if (start is not (0 or 1)) continue;

            string? image = hive.GetString($"{services}\\{svc}", "ImagePath");
            if (image is null) continue;

            // 표준 드라이버는 대개 "System32\drivers\x.sys"(상대). 서드파티는 설치 경로가 절대
            // 경로(\??\C:\...)로 박히거나 System32 밖에 있는 경우가 많습니다.
            bool nonStandardPath =
                image.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
                !image.Contains("System32", StringComparison.OrdinalIgnoreCase);

            if (nonStandardPath) found.Add(new(svc, start.Value, image));
        }

        return found;
    }
}
