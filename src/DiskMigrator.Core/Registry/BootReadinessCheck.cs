using DiskMigrator.Core.Localization;
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
/// <param name="Name">항목 이름(현지화됨 — 표시용).</param>
/// <param name="Passed">통과 여부. 확인 자체가 불가능(볼륨 미마운트 등)하면 null.</param>
/// <param name="Severity">심각도.</param>
/// <param name="Detail">사람이 읽을 상세 설명(현지화됨).</param>
/// <param name="Code">언어와 무관한 안정 식별자(선택). UI 로직은 이름 대신 이것으로 항목을 찾습니다.</param>
public sealed record BootCheckItem(string Name, bool? Passed, BootCheckSeverity Severity, string Detail, string? Code = null);

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

    /// <summary>
    /// 이 디스크의 GPT 디스크 GUID. 주어지면 BCD의 장치 참조가 이 디스크를 가리키는지 대조합니다.
    /// </summary>
    public Guid? DiskGuid { get; init; }

    /// <summary>
    /// 이 디스크의 MBR 디스크 서명. MBR에서 <see cref="DiskGuid"/>와 같은 역할을 합니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 GPT GUID만 대조하고 MBR은 통째로 건너뛰어, MBR 클론에서 BCD 참조가 어긋나
    /// 있어도 "부팅 준비 완료"라고 답했습니다. 실기 클론에서 정확히 그 일이 일어났습니다 —
    /// 원본과 대상이 함께 연결돼 Windows가 대상을 재서명했는데, 검사는 그것을 보지 못했습니다.
    /// </remarks>
    public uint? MbrSignature { get; init; }
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
    /// <summary>
    /// BCD 장치 참조↔디스크 항목의 언어 무관 식별자. UI가 이 코드로 "복구 버튼 제안 여부"를
    /// 판단합니다 — 현지화된 <see cref="BootCheckItem.Name"/>에 의존하면 언어를 바꿀 때 깨집니다.
    /// </summary>
    public const string CodeDeviceRef = "DEVICE_REF";

    /// <summary>BCD 요소 코드: BcdLibraryString_ApplicationPath (OS 로더의 winload 경로).</summary>
    private const string ElementApplicationPath = "12000002";

    /// <summary>BCD 요소 코드: BcdLibraryDevice_ApplicationDevice (부팅 파일이 있는 장치, "device").</summary>
    private const string ElementApplicationDevice = "11000001";

    /// <summary>BCD 요소 코드: BcdOSLoaderDevice_OSDevice (OS가 있는 장치, "osdevice").</summary>
    private const string ElementOsDevice = "21000001";

    /// <summary>BCD 요소 코드: BcdBootMgrObjectList_DefaultObject ({bootmgr}의 기본 로더 GUID).</summary>
    private const string ElementDefault = "23000003";

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
            DiskGuid = disk.DiskGuid,
            MbrSignature = disk.MbrSignature,
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
        string label = input.Uefi ? Strings.Get("BcLabelEsp") : Strings.Get("BcLabelSystemActive");

        if (input.SystemRoot is null)
        {
            items.Add(new(label, null, BootCheckSeverity.Fatal,
                Strings.Get("BcDetailNotMounted")));
            return;
        }

        if (input.Uefi)
        {
            string efiBoot = Path.Combine(input.SystemRoot, "EFI", "Microsoft", "Boot");

            string mgr = Path.Combine(efiBoot, "bootmgfw.efi");
            bool hasMgr = SafeFileExists(mgr);
            items.Add(new(Strings.Get("BcNameUefiLoader"), hasMgr, BootCheckSeverity.Fatal,
                hasMgr ? mgr : Strings.Get("BcDetailNoneUefi")));

            string fallback = Path.Combine(input.SystemRoot, "EFI", "Boot", "bootx64.efi");
            bool hasFallback = SafeFileExists(fallback);
            items.Add(new(Strings.Get("BcNameFallbackLoader"), hasFallback, BootCheckSeverity.Warning,
                hasFallback ? fallback
                    : Strings.Get("BcDetailFallbackMissing")));

            string bcd = Path.Combine(efiBoot, "BCD");
            AnalyzeBcd(bcd, input.DiskGuid, input.MbrSignature, items);
        }
        else
        {
            string mgr = Path.Combine(input.SystemRoot, "bootmgr");
            bool hasMgr = SafeFileExists(mgr);
            items.Add(new(Strings.Get("BcNameBiosMgr"), hasMgr, BootCheckSeverity.Fatal,
                hasMgr ? mgr : Strings.Get("BcDetailNoneBios")));

            string bcd = Path.Combine(input.SystemRoot, "Boot", "BCD");
            AnalyzeBcd(bcd, input.DiskGuid, input.MbrSignature, items);
        }
    }

    private static void InspectWindowsPartition(BootCheckInput input, List<BootCheckItem> items)
    {
        if (input.WindowsRoot is null)
        {
            items.Add(new(Strings.Get("BcNameWinVolume"), null, BootCheckSeverity.Fatal,
                Strings.Get("BcDetailNoWinVolume")));
            return;
        }

        string sys32 = Path.Combine(input.WindowsRoot, "Windows", "System32");
        string loaderName = input.Uefi ? "winload.efi" : "winload.exe";
        string loader = Path.Combine(sys32, loaderName);
        bool hasLoader = SafeFileExists(loader);
        items.Add(new(Strings.Format("BcNameOsLoaderFmt", loaderName), hasLoader, BootCheckSeverity.Fatal,
            hasLoader ? loader : Strings.Get("BcDetailNoOsLoader")));

        CheckHibernationImage(input.WindowsRoot, items);

        string systemHive = Path.Combine(sys32, "config", "SYSTEM");
        if (!SafeFileExists(systemHive))
        {
            items.Add(new(Strings.Get("BcNameSystemHive"), false, BootCheckSeverity.Fatal,
                Strings.Get("BcDetailNoSystemHive")));
            return;
        }
        AnalyzeSystemHive(systemHive, input.Uefi, items);
    }

    /// <summary>
    /// 최대 절전 이미지(<c>hiberfil.sys</c>)가 남아 있는지 — 사본에서는 부팅을 막습니다.
    /// </summary>
    /// <remarks>
    /// Windows는 기본값인 <b>빠른 시작</b>으로 종료할 때 커널 상태를 <c>hiberfil.sys</c>에
    /// 저장하고, 다음 부팅에서 정상 부팅 대신 <c>winresume.exe</c>로 그 상태를 복원합니다.
    ///
    /// <para>저장된 커널 상태는 <b>원래 하드웨어를 전제</b>합니다. 사본을 다른 메인보드에서
    /// 켜면 복원 도중 그대로 멈춥니다 — 오류 문구도 없이 검은 화면이라, 부트로더·BCD·드라이버가
    /// 모두 정상인데도 원인을 찾을 수 없습니다(실기에서 규명).</para>
    ///
    /// <para>같은 하드웨어라도 위험합니다. 원본은 복제 후에도 계속 돌아가므로, 사본의 이미지가
    /// 기억하는 파일시스템 상태와 실제 디스크 내용이 어긋나 손상될 수 있습니다.
    /// <b>사본에서는 언제나 꺼야 합니다.</b></para>
    /// </remarks>
    private static void CheckHibernationImage(string windowsRoot, List<BootCheckItem> items)
    {
        string hiberfil = Path.Combine(windowsRoot, "hiberfil.sys");
        bool exists = SafeFileExists(hiberfil);

        items.Add(new(Strings.Get("BcNameHibernation"), !exists, BootCheckSeverity.Fatal,
            exists
                ? Strings.Get("BcDetailHibernationPresent")
                : Strings.Get("BcDetailHibernationNone")));
    }

    private static void AnalyzeBcd(string bcdPath, Guid? diskGuid, uint? mbrSignature, List<BootCheckItem> items)
    {
        if (!SafeFileExists(bcdPath))
        {
            items.Add(new(Strings.Get("BcNameBcdStore"), false, BootCheckSeverity.Fatal,
                Strings.Format("BcDetailBcdStoreMissingFmt", bcdPath)));
            return;
        }

        switch (TryLoadHive(bcdPath, out RegistryHive? loaded, out string bcdError))
        {
            case HiveLoadStatus.InUse:
                items.Add(new(Strings.Get("BcNameBcdStoreValid"), null, BootCheckSeverity.Fatal, bcdError));
                return;
            case HiveLoadStatus.Bad:
                items.Add(new(Strings.Get("BcNameBcdStoreValid"), false, BootCheckSeverity.Fatal, bcdError));
                return;
        }
        RegistryHive bcd = loaded!;

        if (!bcd.KeyExists("Objects"))
        {
            items.Add(new("BCD Objects", false, BootCheckSeverity.Fatal,
                Strings.Get("BcDetailBcdNotStructure")));
            return;
        }

        bool hasMgr = bcd.KeyExists($"Objects\\{BootMgrObject}\\Elements");
        items.Add(new(Strings.Get("BcNameBcdBootMgr"), hasMgr, BootCheckSeverity.Warning,
            hasMgr ? Strings.Get("BcDetailBootMgrPresent")
                : Strings.Get("BcDetailBootMgrMissing")));

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
        items.Add(new(Strings.Get("BcNameBcdOsLoader"), anyLoader, BootCheckSeverity.Fatal,
            anyLoader ? Strings.Format("BcDetailOsLoaderCountFmt", loaders, firstPath ?? "")
                : Strings.Get("BcDetailNoOsLoaderEntry")));

        CheckDeviceReferences(bcd, diskGuid, mbrSignature, items);
    }

    /// <summary>
    /// BCD의 장치 참조(device/osdevice)가 <b>이 디스크</b>를 가리키는지 대조합니다.
    /// </summary>
    /// <remarks>
    /// 이 검사가 없으면 부트로더·winload·BCD 구조가 전부 정상인데도 실부팅 시 0xc000000e가
    /// 나는 경우를 놓칩니다(실기에서 규명). 원인은 디스크 서명 충돌로 Windows가 GPT 디스크
    /// GUID를 재서명하면, BCD의 장치 요소에 내장된 옛 디스크 GUID가 어긋나기 때문입니다.
    /// 장치 요소는 REG_BINARY이고 대상 디스크 GUID(16바이트)를 그대로 담으므로, 현재 디스크
    /// GUID가 그 바이트 안에 있는지 확인하면 참조 유효성을 알 수 있습니다.
    /// </remarks>
    private static void CheckDeviceReferences(
        RegistryHive bcd, Guid? diskGuid, uint? mbrSignature, List<BootCheckItem> items)
    {
        // GPT는 디스크 GUID 16바이트, MBR은 디스크 서명 4바이트가 장치 요소에 그대로 들어갑니다.
        // 둘 중 아는 것으로 대조합니다.
        byte[]? target =
            diskGuid is { } dg ? dg.ToByteArray() :
            mbrSignature is { } sig ? BitConverter.GetBytes(sig) :
            null;

        if (target is null)
        {
            items.Add(new(Strings.Get("BcNameBcdDeviceRef"), null, BootCheckSeverity.Warning,
                Strings.Get("BcDetailNoDeviceKnown")));
            return;
        }

        // 기본 OS 로더의 osdevice가 우선. 없으면 winload를 가리키는 아무 로더나.
        string? defaultId = bcd.GetString($"Objects\\{BootMgrObject}\\Elements\\{ElementDefault}", "Element");

        bool checkedAny = false;
        bool matched = false;

        void Probe(string objectGuid)
        {
            foreach (string elem in new[] { ElementOsDevice, ElementApplicationDevice })
            {
                byte[]? blob = bcd.GetBinary($"Objects\\{objectGuid}\\Elements\\{elem}", "Element");
                if (blob is null) continue;
                checkedAny = true;
                if (ContainsBytes(blob, target)) matched = true;
            }
        }

        if (!string.IsNullOrEmpty(defaultId)) Probe(defaultId);

        // 기본 로더에서 확인이 안 됐으면, winload OS 로더들을 훑습니다.
        if (!matched)
        {
            foreach (string guid in bcd.EnumerateSubKeyNames("Objects"))
            {
                string? appPath = bcd.GetString($"Objects\\{guid}\\Elements\\{ElementApplicationPath}", "Element");
                if (appPath is null) continue;
                if (appPath.EndsWith("winload.efi", StringComparison.OrdinalIgnoreCase) ||
                    appPath.EndsWith("winload.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Probe(guid);
                    if (matched) break;
                }
            }
        }

        // 부트 매니저의 device도 같은 디스크(ESP)를 가리켜야 하므로 함께 봅니다.
        Probe(BootMgrObject);

        if (!checkedAny)
        {
            items.Add(new(Strings.Get("BcNameBcdDeviceRef"), null, BootCheckSeverity.Warning,
                Strings.Get("BcDetailNoDeviceRefFound")));
            return;
        }

        string identity = diskGuid is { } g
            ? Strings.Format("BcIdentityGuidFmt", g)
            : Strings.Format("BcIdentityMbrFmt", mbrSignature.GetValueOrDefault().ToString("X8"));

        items.Add(new(Strings.Get("BcNameBcdDeviceRef"), matched, BootCheckSeverity.Fatal,
            matched
                ? Strings.Format("BcDetailDeviceMatchFmt", identity)
                : Strings.Format("BcDetailDeviceMismatchFmt", identity),
            CodeDeviceRef));
    }

    /// <summary>haystack 안에 needle(연속 바이트열)이 있으면 true.</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    private static void AnalyzeSystemHive(string systemHivePath, bool uefi, List<BootCheckItem> items)
    {
        switch (TryLoadHive(systemHivePath, out RegistryHive? loaded, out string sysError))
        {
            case HiveLoadStatus.InUse:
                items.Add(new(Strings.Get("BcNameSystemHiveValid"), null, BootCheckSeverity.Fatal, sysError));
                return;
            case HiveLoadStatus.Bad:
                items.Add(new(Strings.Get("BcNameSystemHiveValid"), false, BootCheckSeverity.Fatal, sysError));
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
                Strings.Get("BcDetailNoControlSet")));
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
        items.Add(new(Strings.Format("BcNameBootStartDriversFmt", active), anyBootStart, BootCheckSeverity.Warning,
            anyBootStart
                ? string.Join(", ", bootStart)
                : Strings.Get("BcDetailNoBootStart")));

        // 하드웨어 독립성: 표준 AHCI+NVMe가 모두 부팅 시작이면 대부분의 최신 PC를 커버.
        bool broad = bootStart.Contains("storahci") && bootStart.Contains("stornvme");
        items.Add(new(Strings.Get("BcNameHwIndependence"), broad, BootCheckSeverity.Info,
            broad
                ? Strings.Get("BcDetailHwBroad")
                : Strings.Get("BcDetailHwNarrow")));

        if (!uefi)
        {
            // BIOS 부팅은 부트 섹터/코드가 필요하지만 여기선 파일 레벨만 검사한다는 참고.
            items.Add(new(Strings.Get("BcNameBiosBootCode"), null, BootCheckSeverity.Info,
                Strings.Get("BcDetailBiosBootCode")));
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
            detail = Strings.Get("BcDetailHiveInUse");
            return HiveLoadStatus.InUse;
        }
        catch (Exception ex)
        {
            detail = Strings.Format("BcDetailHiveParseFmt", ex.Message);
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
