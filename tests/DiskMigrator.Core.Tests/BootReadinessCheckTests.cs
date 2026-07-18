using DiskMigrator.Core.Registry;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 부팅 구성 정적 검사를 실제 파일 레이아웃 + regf 하이브로 검증합니다.
/// </summary>
/// <remarks>
/// 관리자 권한이 필요한 reg.exe 하이브 저장 대신, <see cref="HiveBuilder"/>로 SYSTEM·BCD와
/// 같은 키 구조를 가진 진짜 regf 바이트를 만들어 임시 폴더에 클론 디스크와 같은 레이아웃
/// (ESP + Windows)을 구성하고 검사를 돌립니다. 파일 존재 검사 · BCD 파싱 · 하위키 열거 ·
/// 문자열/DWORD 읽기 · 드라이버 판정이 한 번에 함께 exercised 됩니다.
/// </remarks>
public class BootReadinessCheckTests
{
    [Fact]
    public void 정상_UEFI_클론은_부팅_준비_완료로_판정된다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();
        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.True(report.WouldBoot, Dump(report));

        var driverItem = report.Items.Single(i => i.Name.StartsWith("부팅 시작 저장소 드라이버"));
        Assert.True(driverItem.Passed);
        Assert.Contains("storahci", driverItem.Detail);

        var loaderItem = report.Items.Single(i => i.Name == "BCD OS 로더 항목");
        Assert.True(loaderItem.Passed);
        Assert.Contains("winload.efi", loaderItem.Detail);

        var mgrItem = report.Items.Single(i => i.Name == "BCD 부트 매니저 항목");
        Assert.True(mgrItem.Passed);
    }

    [Fact]
    public void 부트로더가_없으면_부팅_불가로_판정된다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();
        File.Delete(Path.Combine(layout.EspRoot, "EFI", "Microsoft", "Boot", "bootmgfw.efi"));

        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.False(report.WouldBoot, Dump(report));
        var mgr = report.Items.Single(i => i.Name.Contains("bootmgfw"));
        Assert.False(mgr.Passed);
        Assert.Equal(BootCheckSeverity.Fatal, mgr.Severity);
    }

    [Fact]
    public void winload가_없으면_부팅_불가로_판정된다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();
        File.Delete(Path.Combine(layout.WindowsRoot, "Windows", "System32", "winload.efi"));

        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.False(report.WouldBoot, Dump(report));
        var loader = report.Items.Single(i => i.Name.Contains("winload"));
        Assert.False(loader.Passed);
    }

    [Fact]
    public void 잠긴_하이브는_손상이_아니라_사용중으로_보고된다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();
        string systemHive = Path.Combine(layout.WindowsRoot, "Windows", "System32", "config", "SYSTEM");

        // SYSTEM 하이브를 배타적으로 열어 잠금(라이브 OS를 흉내).
        using (File.Open(systemHive, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = BootReadinessCheck.Inspect(layout.Input);

            var item = report.Items.Single(i => i.Name == "SYSTEM 하이브 유효성");
            Assert.Null(item.Passed);              // 확인 불가(손상 아님)
            Assert.Contains("사용 중", item.Detail);
            Assert.DoesNotContain("손상", item.Detail);

            // 확인 못 한 치명 항목이 있으므로 부팅 가능으로 낙관하지 않음.
            Assert.False(report.WouldBoot, Dump(report));
        }
    }

    [Fact]
    public void 볼륨_미마운트는_부팅_보장하지_못한다()
    {
        var report = BootReadinessCheck.Inspect(new BootCheckInput
        {
            Uefi = true,
            SystemRoot = null,
            WindowsRoot = null,
        });

        // 치명 요소를 확인조차 못 했으므로 부팅 가능이라 단언할 수 없음.
        Assert.False(report.WouldBoot);
        Assert.All(report.Items, i => Assert.NotEqual(true, i.Passed));
    }

    private static string Dump(BootReadinessReport report) =>
        "\n" + string.Join("\n", report.Items.Select(i => $"  {i.Passed} ({i.Severity}) {i.Name}: {i.Detail}"));

    /// <summary>임시 폴더에 클론 디스크와 같은 파일 레이아웃 + 진짜 regf 하이브를 만드는 픽스처.</summary>
    private sealed class DiskLayout : IDisposable
    {
        public string Root { get; }
        public string EspRoot { get; }
        public string WindowsRoot { get; }
        public BootCheckInput Input { get; }

        private DiskLayout(string root, string esp, string win, BootCheckInput input)
        {
            Root = root;
            EspRoot = esp;
            WindowsRoot = win;
            Input = input;
        }

        public static DiskLayout CreateHealthyUefi()
        {
            string root = Path.Combine(Path.GetTempPath(), "dm-boot-" + Guid.NewGuid().ToString("N"));
            string esp = Path.Combine(root, "esp");
            string win = Path.Combine(root, "win");

            string efiBoot = Path.Combine(esp, "EFI", "Microsoft", "Boot");
            Directory.CreateDirectory(efiBoot);
            Directory.CreateDirectory(Path.Combine(esp, "EFI", "Boot"));
            File.WriteAllText(Path.Combine(efiBoot, "bootmgfw.efi"), "stub");
            File.WriteAllText(Path.Combine(esp, "EFI", "Boot", "bootx64.efi"), "stub");
            File.WriteAllBytes(Path.Combine(efiBoot, "BCD"), BuildBcd());

            string sys32 = Path.Combine(win, "Windows", "System32");
            Directory.CreateDirectory(Path.Combine(sys32, "config"));
            File.WriteAllText(Path.Combine(sys32, "winload.efi"), "stub");
            File.WriteAllBytes(Path.Combine(sys32, "config", "SYSTEM"), BuildSystem());

            return new DiskLayout(root, esp, win, new BootCheckInput
            {
                Uefi = true,
                SystemRoot = esp + "\\",
                WindowsRoot = win + "\\",
            });
        }

        /// <summary>Select\Current 와 ControlSet001\Services\{storahci,stornvme,pciide}\Start 를 가진 SYSTEM 하이브.</summary>
        private static byte[] BuildSystem()
        {
            var b = new HiveBuilder();
            int ahci = b.AddKey("storahci", [b.Dword("Start", 0)], []);
            int nvme = b.AddKey("stornvme", [b.Dword("Start", 0)], []);
            int ide = b.AddKey("pciide", [b.Dword("Start", 3)], []);
            int services = b.AddKey("Services", [], [ahci, nvme, ide]);
            int cs1 = b.AddKey("ControlSet001", [], [services]);
            int select = b.AddKey("Select", [b.Dword("Current", 1)], []);
            int rootKey = b.AddKey("", [], [cs1, select]);
            return b.Finish(rootKey);
        }

        /// <summary>부트 매니저 객체 + winload를 가리키는 OS 로더 객체를 가진 BCD 하이브.</summary>
        private static byte[] BuildBcd()
        {
            const string bootMgr = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
            const string loaderGuid = "{11111111-2222-3333-4444-555555555555}";

            var b = new HiveBuilder();
            // OS 로더 객체: Elements\12000002\Element = winload 경로
            int e12 = b.AddKey("12000002", [b.Sz("Element", @"\Windows\system32\winload.efi")], []);
            int loaderElems = b.AddKey("Elements", [], [e12]);
            int loaderObj = b.AddKey(loaderGuid, [], [loaderElems]);
            // 부트 매니저 객체: Elements 존재만 확인
            int e24 = b.AddKey("24000001", [b.Sz("Element", loaderGuid)], []);
            int mgrElems = b.AddKey("Elements", [], [e24]);
            int mgrObj = b.AddKey(bootMgr, [], [mgrElems]);

            int objects = b.AddKey("Objects", [], [loaderObj, mgrObj]);
            int rootKey = b.AddKey("", [], [objects]);
            return b.Finish(rootKey);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 정리 실패는 무시 */ }
        }
    }
}
