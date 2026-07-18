using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Registry;

/// <summary>검사 항목의 심각도. 부팅 가능 여부 판정에 쓰입니다.</summary>
public enum BootCheckSeverity
{
    /// <summary>실패하면 부팅 자체가 불가능한 필수 요소.</summary>
    Fatal,

    /// <summary>같은 하드웨어면 부팅되지만, 위험하거나 다른 하드웨어에서 문제될 수 있는 요소.</summary>
    Warning,

    /// <summary>참고용. 부팅 여부에는 영향 없음.</summary>
    Info,
}

/// <summary>부팅 구성 정적 검사의 개별 항목 결과.</summary>
/// <param name="Name">항목 이름.</param>
/// <param name="Passed">통과 여부. 확인 자체가 불가능(볼륨 미마운트 등)하면 null.</param>
/// <param name="Severity">심각도.</param>
/// <param name="Detail">사람이 읽을 상세 설명.</param>
public sealed record BootCheckItem(string Name, bool? Passed, BootCheckSeverity Severity, string Detail);

/// <summary>부팅 구성 정적 검사 전체 결과.</summary>
public sealed record BootReadinessReport(IReadOnlyList<BootCheckItem> Items)
{
    /// <summary>
    /// 모든 치명(Fatal) 항목이 <b>명시적으로 통과</b>했으면 true (부팅 가능으로 판정).
    /// </summary>
    /// <remarks>
    /// 치명 항목이 실패(false)했거나 확인 불가(null: 볼륨 미마운트 등)면 부팅을 보장할 수
    /// 없으므로 false입니다. "확인 못 함"을 낙관적으로 통과 처리하지 않습니다.
    /// </remarks>
    public bool WouldBoot =>
        Items.Where(i => i.Severity == BootCheckSeverity.Fatal).All(i => i.Passed == true);

    /// <summary>실패한 경고 항목이 있는지.</summary>
    public bool HasWarnings =>
        Items.Any(i => i.Severity == BootCheckSeverity.Warning && i.Passed == false);
}

/// <summary>이미 마운트된 볼륨 경로를 받아 부팅 구성 정적 검사를 수행합니다.</summary>
public sealed record BootCheckInput
{
    /// <summary>true면 UEFI(ESP) 기준, false면 BIOS(활성 파티션) 기준으로 경로를 해석합니다.</summary>
    public required bool Uefi { get; init; }

    /// <summary>부팅 파일이 있는 파티션의 루트. UEFI면 ESP, BIOS면 활성/시스템 파티션. 미마운트면 null.</summary>
    public string? SystemRoot { get; init; }

    /// <summary>Windows가 설치된 볼륨의 루트(예: "C:\" 또는 "\\?\Volume{...}\"). 미마운트면 null.</summary>
    public string? WindowsRoot { get; init; }
}

/// <summary>
/// 클론한 디스크가 부팅 가능한 구성인지 <b>실제 부팅 없이</b> 정적으로 점검합니다.
/// </summary>
/// <remarks>
/// 부트로더 파일 존재, BCD 스토어 유효성과 OS 로더 항목, winload 존재, SYSTEM 하이브의
/// 저장소 드라이버 부팅 시작 여부를 확인합니다. 실기/VM에서 실제로 부팅해 보는 검증을
/// 대신하지는 못하지만, 부팅을 막는 흔한 결함(부트로더 누락, BCD 손상, 0x7B 유발 드라이버
/// 설정)을 옮기기 전에 값싸게 잡아냅니다.
/// </remarks>
public static class BootReadinessCheck
{
    /// <summary>BCD 요소 코드: BcdLibraryString_ApplicationPath (OS 로더의 winload 경로).</summary>
    private const string ElementApplicationPath = "12000002";

    /// <summary>표준 Windows 부트 매니저 객체 GUID.</summary>
    private const string BootMgrObject = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";

    public static BootReadinessReport Inspect(BootCheckInput input)
    {
        var items = new List<BootCheckItem>();

        InspectSystemPartition(input, items);
        InspectWindowsPartition(input, items);

        return new BootReadinessReport(items);
    }

    /// <summary>디스크를 검사합니다 — 파티션에서 ESP/Windows 볼륨 경로를 해석해 검사합니다.</summary>
    public static BootReadinessReport InspectDisk(DiskInfo disk) => Inspect(ResolveInput(disk));

    /// <summary>
    /// 디스크의 파티션 목록에서 부팅 검사 입력을 해석합니다(부팅 방식, ESP·Windows 볼륨 루트).
    /// </summary>
    /// <remarks>
    /// 볼륨은 드라이브 문자가 없어도 볼륨 GUID 경로로 접근합니다(ESP는 대개 문자가 없음).
    /// Windows 볼륨은 실제로 <c>\Windows\System32</c>가 있는 파티션으로 고릅니다.
    /// </remarks>
    public static BootCheckInput ResolveInput(DiskInfo disk)
    {
        bool uefi = disk.Partitions.Any(p => p.IsEfiSystemPartition);

        PartitionInfo? systemPartition = uefi
            ? disk.Partitions.FirstOrDefault(p => p.IsEfiSystemPartition)
            : disk.Partitions.FirstOrDefault(p => p.IsActive) ?? disk.Partitions.FirstOrDefault();

        PartitionInfo? windowsPartition = null;
        foreach (var p in disk.Partitions)
        {
            string? root = RootOf(p);
            if (root is null) continue;
            try
            {
                if (Directory.Exists(Path.Combine(root, "Windows", "System32")))
                {
                    windowsPartition = p;
                    break;
                }
            }
            catch
            {
                // 접근 불가 볼륨은 건너뜁니다.
            }
        }

        return new BootCheckInput
        {
            Uefi = uefi,
            SystemRoot = RootOf(systemPartition),
            WindowsRoot = RootOf(windowsPartition),
        };
    }

    /// <summary>파일 접근에 쓸 볼륨 루트. 드라이브 문자가 없어도 볼륨 GUID 경로로 접근합니다.</summary>
    private static string? RootOf(PartitionInfo? p)
    {
        if (p is null) return null;
        if (p.VolumeGuidPath is { } guid)
            return guid.EndsWith('\\') ? guid : guid + "\\";
        if (p.DriveLetter is { } letter)
            return $"{letter}:\\";
        return null;
    }

    private static void InspectSystemPartition(BootCheckInput input, List<BootCheckItem> items)
    {
        string label = input.Uefi ? "EFI 시스템 파티션(ESP)" : "시스템/활성 파티션";

        if (input.SystemRoot is null)
        {
            items.Add(new(label, null, BootCheckSeverity.Fatal,
                "볼륨이 마운트되지 않아 부트로더/BCD를 검사할 수 없습니다."));
            return;
        }

        if (input.Uefi)
        {
            string efiBoot = Path.Combine(input.SystemRoot, "EFI", "Microsoft", "Boot");

            string mgr = Path.Combine(efiBoot, "bootmgfw.efi");
            bool hasMgr = SafeFileExists(mgr);
            items.Add(new("UEFI 부트로더 (bootmgfw.efi)", hasMgr, BootCheckSeverity.Fatal,
                hasMgr ? mgr : "없음 — UEFI 부팅이 불가능합니다."));

            string fallback = Path.Combine(input.SystemRoot, "EFI", "Boot", "bootx64.efi");
            bool hasFallback = SafeFileExists(fallback);
            items.Add(new("폴백 부트로더 (EFI\\Boot\\bootx64.efi)", hasFallback, BootCheckSeverity.Warning,
                hasFallback ? fallback
                    : "없음 — 일부 펌웨어/이동식 부팅에서 필요할 수 있습니다."));

            string bcd = Path.Combine(efiBoot, "BCD");
            AnalyzeBcd(bcd, items);
        }
        else
        {
            string mgr = Path.Combine(input.SystemRoot, "bootmgr");
            bool hasMgr = SafeFileExists(mgr);
            items.Add(new("BIOS 부트 매니저 (bootmgr)", hasMgr, BootCheckSeverity.Fatal,
                hasMgr ? mgr : "없음 — BIOS/MBR 부팅이 불가능합니다."));

            string bcd = Path.Combine(input.SystemRoot, "Boot", "BCD");
            AnalyzeBcd(bcd, items);
        }
    }

    private static void InspectWindowsPartition(BootCheckInput input, List<BootCheckItem> items)
    {
        if (input.WindowsRoot is null)
        {
            items.Add(new("Windows 볼륨", null, BootCheckSeverity.Fatal,
                "\\Windows 가 있는 볼륨을 찾지 못했거나 마운트되지 않았습니다."));
            return;
        }

        string sys32 = Path.Combine(input.WindowsRoot, "Windows", "System32");
        string loaderName = input.Uefi ? "winload.efi" : "winload.exe";
        string loader = Path.Combine(sys32, loaderName);
        bool hasLoader = SafeFileExists(loader);
        items.Add(new($"OS 로더 ({loaderName})", hasLoader, BootCheckSeverity.Fatal,
            hasLoader ? loader : $"없음 — BCD가 가리키는 OS 로더가 실제로 없습니다."));

        string systemHive = Path.Combine(sys32, "config", "SYSTEM");
        if (!SafeFileExists(systemHive))
        {
            items.Add(new("SYSTEM 레지스트리 하이브", false, BootCheckSeverity.Fatal,
                "없음 — 커널이 시스템 구성을 읽을 수 없습니다."));
            return;
        }
        AnalyzeSystemHive(systemHive, input.Uefi, items);
    }

    private static void AnalyzeBcd(string bcdPath, List<BootCheckItem> items)
    {
        if (!SafeFileExists(bcdPath))
        {
            items.Add(new("BCD 스토어", false, BootCheckSeverity.Fatal,
                $"없음 ({bcdPath}) — 부팅 구성이 없습니다."));
            return;
        }

        switch (TryLoadHive(bcdPath, out RegistryHive? loaded, out string bcdError))
        {
            case HiveLoadStatus.InUse:
                items.Add(new("BCD 스토어 유효성", null, BootCheckSeverity.Fatal, bcdError));
                return;
            case HiveLoadStatus.Bad:
                items.Add(new("BCD 스토어 유효성", false, BootCheckSeverity.Fatal, bcdError));
                return;
        }
        RegistryHive bcd = loaded!;

        if (!bcd.KeyExists("Objects"))
        {
            items.Add(new("BCD Objects", false, BootCheckSeverity.Fatal,
                "Objects 키가 없습니다 — BCD 구조가 아닙니다."));
            return;
        }

        bool hasMgr = bcd.KeyExists($"Objects\\{BootMgrObject}\\Elements");
        items.Add(new("BCD 부트 매니저 항목", hasMgr, BootCheckSeverity.Warning,
            hasMgr ? "표준 부트 매니저 객체 존재"
                : "표준 부트 매니저 GUID 항목이 없습니다 (비표준 구성일 수 있음)."));

        // OS 로더 항목: 각 객체의 ApplicationPath(REG_SZ)가 winload로 끝나는지.
        int loaders = 0;
        string? firstPath = null;
        foreach (string guid in bcd.EnumerateSubKeyNames("Objects"))
        {
            string key = $"Objects\\{guid}\\Elements\\{ElementApplicationPath}";
            string? appPath = bcd.GetString(key, "Element");
            if (appPath is null) continue;

            if (appPath.EndsWith("winload.efi", StringComparison.OrdinalIgnoreCase) ||
                appPath.EndsWith("winload.exe", StringComparison.OrdinalIgnoreCase))
            {
                loaders++;
                firstPath ??= appPath;
            }
        }

        bool anyLoader = loaders > 0;
        items.Add(new("BCD OS 로더 항목", anyLoader, BootCheckSeverity.Fatal,
            anyLoader ? $"{loaders}개 — 예: {firstPath}"
                : "winload를 가리키는 OS 로더 항목이 없습니다 — 부팅 메뉴가 비어 있습니다."));
    }

    private static void AnalyzeSystemHive(string systemHivePath, bool uefi, List<BootCheckItem> items)
    {
        switch (TryLoadHive(systemHivePath, out RegistryHive? loaded, out string sysError))
        {
            case HiveLoadStatus.InUse:
                items.Add(new("SYSTEM 하이브 유효성", null, BootCheckSeverity.Fatal, sysError));
                return;
            case HiveLoadStatus.Bad:
                items.Add(new("SYSTEM 하이브 유효성", false, BootCheckSeverity.Fatal, sysError));
                return;
        }
        RegistryHive hive = loaded!;

        var sets = new List<string>();
        for (int n = 1; n <= 9; n++)
        {
            string cs = $"ControlSet{n:D3}";
            if (hive.KeyExists(cs)) sets.Add(cs);
        }

        if (sets.Count == 0)
        {
            items.Add(new("SYSTEM ControlSet", false, BootCheckSeverity.Fatal,
                "ControlSet를 찾지 못했습니다 — 올바른 SYSTEM 하이브가 아닙니다."));
            return;
        }

        // 활성 컨트롤 세트(Select\Current) 기준으로 판정. 없으면 첫 세트.
        uint? current = hive.GetDword("Select", "Current");
        string active = current is >= 1 and <= 9 ? $"ControlSet{current:D3}" : sets[0];
        if (!sets.Contains(active)) active = sets[0];

        var bootStart = new List<string>();
        foreach (string driver in UniversalRestore.StorageDrivers)
        {
            if (hive.GetDword($"{active}\\Services\\{driver}", "Start") == 0)
                bootStart.Add(driver);
        }

        bool anyBootStart = bootStart.Count > 0;
        items.Add(new($"부팅 시작 저장소 드라이버 ({active})", anyBootStart, BootCheckSeverity.Warning,
            anyBootStart
                ? string.Join(", ", bootStart)
                : "부팅 시작(Start=0) 저장소 드라이버가 없습니다 — 부팅 중 0x7B 위험이 큽니다."));

        // 하드웨어 독립성: 표준 AHCI+NVMe가 모두 부팅 시작이면 대부분의 최신 PC를 커버.
        bool broad = bootStart.Contains("storahci") && bootStart.Contains("stornvme");
        items.Add(new("하드웨어 독립성 (Universal Restore)", broad, BootCheckSeverity.Info,
            broad
                ? "storahci+stornvme 모두 부팅 시작 — 대부분의 AHCI/NVMe 하드웨어를 커버합니다."
                : "표준 AHCI/NVMe가 모두 부팅 시작은 아닙니다 — 다른 하드웨어로 옮긴다면 --universal-restore 권장."));

        if (!uefi)
        {
            // BIOS 부팅은 부트 섹터/코드가 필요하지만 여기선 파일 레벨만 검사한다는 참고.
            items.Add(new("BIOS 부트 코드", null, BootCheckSeverity.Info,
                "MBR/VBR 부트 코드는 이 정적 파일 검사 범위를 벗어납니다 (실부팅 확인 권장)."));
        }
    }

    private enum HiveLoadStatus
    {
        /// <summary>정상 로드.</summary>
        Ok,

        /// <summary>다른 프로세스가 파일을 잠가 읽을 수 없음(라이브/마운트된 OS). 손상과 구분합니다.</summary>
        InUse,

        /// <summary>파싱 실패 등 실제 결함.</summary>
        Bad,
    }

    /// <summary>
    /// 하이브 파일을 로드하되, "사용 중(잠김)"을 "손상"과 구분해 분류합니다.
    /// </summary>
    /// <remarks>
    /// 라이브/마운트된 OS의 SYSTEM·BCD 하이브는 커널이 배타적으로 열고 있어 읽을 수 없습니다.
    /// 이는 클론이 손상됐다는 뜻이 아니라, 이 검사가 <b>오프라인 클론 대상</b>을 위한 것이라는
    /// 신호입니다. 두 경우의 메시지를 다르게 해 오해를 막습니다.
    /// </remarks>
    private static HiveLoadStatus TryLoadHive(string path, out RegistryHive? hive, out string detail)
    {
        hive = null;
        try
        {
            hive = RegistryHive.Load(path);
            detail = "";
            return HiveLoadStatus.Ok;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // 공유/잠금 위반
        {
            detail = "사용 중이라 읽을 수 없습니다 — 라이브/마운트된 OS일 수 있습니다. " +
                     "이 검사는 오프라인 클론 대상에 사용하세요.";
            return HiveLoadStatus.InUse;
        }
        catch (Exception ex)
        {
            detail = $"하이브 파싱 실패 — 손상 가능: {ex.Message}";
            return HiveLoadStatus.Bad;
        }
    }

    /// <summary>권한/경로 문제로 예외가 나도 "없음"으로 처리해 검사가 중단되지 않게 합니다.</summary>
    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}
