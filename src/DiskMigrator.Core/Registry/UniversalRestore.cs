using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Registry;

/// <summary>클론한 시스템이 다른 하드웨어에서도 부팅되도록 SYSTEM 하이브를 손봅니다.</summary>
/// <remarks>
/// 상용 도구의 "Universal Restore"(Acronis) / "ReDeploy"(Macrium)에 해당합니다.
/// 핵심은 표준 저장소 컨트롤러 드라이버를 <b>부팅 시작(Start=0)</b>으로 켜는 것입니다.
/// 그러면 클론을 어떤 PC에 꽂아도 그 PC의 SATA/AHCI/NVMe/RAID 컨트롤러를 부팅 초기에
/// 인식해, INACCESSIBLE_BOOT_DEVICE(0x7B) 파란 화면을 피합니다.
/// </remarks>
public static class UniversalRestore
{
    /// <summary>SERVICE_BOOT_START. 부팅 로더가 로드하는 드라이버.</summary>
    private const uint StartBoot = 0;

    /// <summary>
    /// 부팅 시작으로 켤 표준 인박스 저장소 드라이버들. 존재하는 것만 수정합니다.
    /// </summary>
    /// <remarks>
    /// 이들은 Windows에 기본 포함된 드라이버라 별도 설치가 필요 없습니다. 대상 PC의 컨트롤러가
    /// 무엇이든(대개 AHCI 또는 NVMe) 이 중 하나가 담당합니다.
    /// </remarks>
    private static readonly string[] StorageDrivers =
    [
        "storahci",   // AHCI (SATA) — 가장 흔함
        "stornvme",   // NVMe
        "iaStorV",    // Intel RST (구형)
        "iaStorAV",   // Intel RST (AHCI/RAID)
        "iaStorAC",   // Intel RST (신형)
        "iaStorE",
        "storufs",    // UFS
        "msahci",     // AHCI (Win7 이름)
        "pciide",     // 레거시 IDE
        "atapi",
        "intelide",
        "amdsata",    // AMD SATA
        "amdxata",
        "LSI_SAS",    // LSI/Broadcom SAS
        "LSI_SAS2",
        "LSI_SAS3",
        "vmbus",      // Hyper-V
        "storvsc",    // Hyper-V 저장소
    ];

    public sealed record Result(
        IReadOnlyList<string> Enabled,
        IReadOnlyList<string> NotFound,
        IReadOnlyList<string> ControlSets)
    {
        public bool AnyChanged => Enabled.Count > 0;
    }

    /// <summary>
    /// SYSTEM 하이브 파일에 하드웨어 독립화를 적용합니다.
    /// </summary>
    /// <param name="systemHivePath">대상의 Windows\System32\config\SYSTEM 경로.</param>
    /// <remarks>
    /// 활성 컨트롤 세트뿐 아니라 존재하는 모든 ControlSet00N에 적용합니다(비활성 세트에 적용해도
    /// 무해하고, 다음 부팅에서 어느 세트가 선택되든 안전하게 만듭니다).
    /// </remarks>
    public static Result Apply(string systemHivePath, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var hive = RegistryHive.Load(systemHivePath);

        var enabled = new List<string>();
        var notFound = new HashSet<string>(StorageDrivers, StringComparer.OrdinalIgnoreCase);
        var controlSets = new List<string>();

        for (int n = 1; n <= 9; n++)
        {
            string cs = $"ControlSet{n:D3}";
            if (!hive.KeyExists(cs)) continue;
            controlSets.Add(cs);

            foreach (string driver in StorageDrivers)
            {
                string keyPath = $"{cs}\\Services\\{driver}";
                if (!hive.KeyExists(keyPath)) continue;

                notFound.Remove(driver);

                uint? before = hive.GetDword(keyPath, "Start");
                if (hive.SetDword(keyPath, "Start", StartBoot))
                {
                    log.LogInformation(
                        "{Cs}\\{Driver}: Start {Before} → 0 (부팅 시작)",
                        cs, driver, before?.ToString() ?? "?");
                    enabled.Add($"{cs}\\{driver}");
                }
            }
        }

        if (controlSets.Count == 0)
        {
            throw new InvalidDataException(
                "SYSTEM 하이브에서 ControlSet를 찾지 못했습니다. 올바른 SYSTEM 하이브가 아닐 수 있습니다.");
        }

        // 반드시 마지막에: 하이브를 깨끗함으로 표시하고 체크섬 재계산.
        hive.MarkCleanAndUpdateChecksum();
        hive.Save(systemHivePath);

        log.LogInformation(
            "Universal Restore 적용 완료: 드라이버 {Count}개 부팅 시작으로 설정, 컨트롤 세트 {Sets}.",
            enabled.Count, string.Join(", ", controlSets));

        return new Result(enabled, notFound.ToList(), controlSets);
    }
}
