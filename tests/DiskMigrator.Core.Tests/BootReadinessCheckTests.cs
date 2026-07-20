using DiskMigrator.Core.Models;
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

        // BCD 장치 참조가 이 디스크 GUID를 가리킴 → 통과.
        var devItem = report.Items.Single(i => i.Name == "BCD 장치 참조 ↔ 디스크");
        Assert.True(devItem.Passed);
    }

    [Fact]
    public void BCD_장치참조가_디스크와_불일치하면_부팅_불가로_판정된다()
    {
        // 디스크 서명 충돌로 디스크 GUID가 바뀐 상황: BCD는 옛 GUID를 가리킴 → 0xc000000e.
        using var layout = DiskLayout.CreateHealthyUefi(bcdDeviceMatchesDisk: false);
        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.False(report.WouldBoot, Dump(report));

        var devItem = report.Items.Single(i => i.Name == "BCD 장치 참조 ↔ 디스크");
        Assert.False(devItem.Passed);
        Assert.Equal(BootCheckSeverity.Fatal, devItem.Severity);
        Assert.Contains("0xc000000e", devItem.Detail);

        // 파일·구조 검사는 전부 통과하지만(정적 검사만으로는 놓쳤던 케이스), 장치 대조가 잡아냄.
        Assert.True(report.Items.Single(i => i.Name.Contains("bootmgfw")).Passed);
        Assert.True(report.Items.Single(i => i.Name == "BCD OS 로더 항목").Passed);
    }

    /// <summary>
    /// MBR 클론에서 BCD 참조 대조가 실제로 돌아야 합니다. 예전에는 GPT GUID만 보고 MBR은
    /// 통째로 건너뛰어, 참조가 어긋난 디스크를 "부팅 준비 완료"로 보고했습니다 —
    /// 실기 클론에서 정확히 그 일이 일어났습니다.
    /// </summary>
    [Fact]
    public void 정상_BIOS_MBR_클론은_부팅_준비_완료로_판정된다()
    {
        using var layout = DiskLayout.CreateHealthyBios();

        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.True(report.WouldBoot, Dump(report));
    }

    [Fact]
    public void MBR_서명이_BCD와_불일치하면_부팅_불가로_판정된다()
    {
        // 원본과 대상을 함께 연결해 두면 Windows가 대상을 재서명하고, BCD는 옛 서명을
        // 가리킨 채 남습니다. 부팅하면 0xc000000e가 납니다.
        using var layout = DiskLayout.CreateHealthyBios(bcdSignatureMatchesDisk: false);

        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.False(report.WouldBoot);
        var item = report.Items.Single(i => i.Name.Contains("장치 참조"));
        Assert.False(item.Passed);
        Assert.Equal(BootCheckSeverity.Fatal, item.Severity);
    }

    [Fact]
    public void MBR_서명도_GUID도_없으면_대조를_건너뛴다()
    {
        using var layout = DiskLayout.CreateHealthyBios();
        var input = layout.Input with { MbrSignature = null };

        var report = BootReadinessCheck.Inspect(input);

        var item = report.Items.Single(i => i.Name.Contains("장치 참조"));
        Assert.Null(item.Passed);
    }

    /// <summary>
    /// 실기에서 가장 진단하기 어려웠던 실패입니다. 부트로더·BCD·드라이버가 전부 정상인데
    /// 사본이 오류 문구 하나 없이 검은 화면에서 멈췄습니다 — 원본이 빠른 시작으로 종료돼
    /// 저장된 커널 상태를 다른 하드웨어에서 복원하려던 것이었습니다.
    /// </summary>
    [Fact]
    public void 최대_절전_이미지가_남아_있으면_부팅_불가로_판정된다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();
        File.WriteAllText(Path.Combine(layout.WindowsRoot, "hiberfil.sys"), "saved kernel state");

        var report = BootReadinessCheck.Inspect(layout.Input);

        Assert.False(report.WouldBoot);
        var item = report.Items.Single(i => i.Name.StartsWith("최대 절전"));
        Assert.False(item.Passed);
        Assert.Equal(BootCheckSeverity.Fatal, item.Severity);
        Assert.Contains("검은 화면", item.Detail);
    }

    [Fact]
    public void 최대_절전_이미지가_없으면_통과한다()
    {
        using var layout = DiskLayout.CreateHealthyUefi();

        var report = BootReadinessCheck.Inspect(layout.Input);

        var item = report.Items.Single(i => i.Name.StartsWith("최대 절전"));
        Assert.True(item.Passed);
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

    [Fact]
    public void ResolveInput은_EFI파티션에서_UEFI와_ESP경로를_해석한다()
    {
        var disk = new DiskInfo
        {
            DeviceNumber = 9,
            Model = "TEST",
            SizeBytes = 1000,
            LogicalSectorSize = 512,
            Partitions =
            [
                new PartitionInfo
                {
                    Number = 1, StartingOffset = 0, LengthBytes = 100,
                    IsEfiSystemPartition = true,
                    VolumeGuidPath = @"\\?\Volume{aaaaaaaa-0000-0000-0000-000000000001}\",
                },
                new PartitionInfo
                {
                    Number = 2, StartingOffset = 100, LengthBytes = 900,
                    VolumeGuidPath = @"\\?\Volume{bbbbbbbb-0000-0000-0000-000000000002}\",
                },
            ],
        };

        var input = BootReadinessCheck.ResolveInput(disk);

        Assert.True(input.Uefi);
        Assert.Equal(@"\\?\Volume{aaaaaaaa-0000-0000-0000-000000000001}\", input.SystemRoot);
        // 어떤 볼륨에도 \Windows\System32 가 없으므로 Windows 루트는 미해석.
        Assert.Null(input.WindowsRoot);
    }

    [Fact]
    public void ResolveInput은_EFI가_없으면_BIOS와_활성파티션을_시스템루트로_쓴다()
    {
        var disk = new DiskInfo
        {
            DeviceNumber = 9,
            Model = "TEST",
            SizeBytes = 1000,
            LogicalSectorSize = 512,
            Partitions =
            [
                new PartitionInfo
                {
                    Number = 1, StartingOffset = 0, LengthBytes = 1000,
                    IsActive = true, DriveLetter = "E",
                },
            ],
        };

        var input = BootReadinessCheck.ResolveInput(disk);

        Assert.False(input.Uefi);
        Assert.Equal(@"E:\", input.SystemRoot);
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

        public static DiskLayout CreateHealthyUefi(bool bcdDeviceMatchesDisk = true)
        {
            Guid diskGuid = Guid.NewGuid();
            Guid bcdDeviceGuid = bcdDeviceMatchesDisk ? diskGuid : Guid.NewGuid();

            string root = Path.Combine(Path.GetTempPath(), "dm-boot-" + Guid.NewGuid().ToString("N"));
            string esp = Path.Combine(root, "esp");
            string win = Path.Combine(root, "win");

            string efiBoot = Path.Combine(esp, "EFI", "Microsoft", "Boot");
            Directory.CreateDirectory(efiBoot);
            Directory.CreateDirectory(Path.Combine(esp, "EFI", "Boot"));
            File.WriteAllText(Path.Combine(efiBoot, "bootmgfw.efi"), "stub");
            File.WriteAllText(Path.Combine(esp, "EFI", "Boot", "bootx64.efi"), "stub");
            File.WriteAllBytes(Path.Combine(efiBoot, "BCD"), BuildBcd(bcdDeviceGuid));

            string sys32 = Path.Combine(win, "Windows", "System32");
            Directory.CreateDirectory(Path.Combine(sys32, "config"));
            File.WriteAllText(Path.Combine(sys32, "winload.efi"), "stub");
            File.WriteAllBytes(Path.Combine(sys32, "config", "SYSTEM"), BuildSystem());

            return new DiskLayout(root, esp, win, new BootCheckInput
            {
                Uefi = true,
                SystemRoot = esp + "\\",
                WindowsRoot = win + "\\",
                DiskGuid = diskGuid,
            });
        }

        /// <summary>
        /// BIOS/MBR 클론 — 부팅 파일이 활성 파티션 루트에 있고, BCD 장치 참조에는 GPT GUID 대신
        /// 4바이트 디스크 서명이 들어갑니다.
        /// </summary>
        public static DiskLayout CreateHealthyBios(bool bcdSignatureMatchesDisk = true)
        {
            const uint diskSignature = 812018231;   // 실기 N: 디스크의 서명
            uint bcdSignature = bcdSignatureMatchesDisk ? diskSignature : 285794371;

            string root = Path.Combine(Path.GetTempPath(), "dm-boot-" + Guid.NewGuid().ToString("N"));
            string sys = Path.Combine(root, "sys");
            string win = sys;   // BIOS 단일 파티션 배치 — 부팅 파일과 Windows가 한 파티션에.

            Directory.CreateDirectory(Path.Combine(sys, "Boot"));
            File.WriteAllText(Path.Combine(sys, "bootmgr"), "stub");
            File.WriteAllBytes(Path.Combine(sys, "Boot", "BCD"), BuildBcd(SignatureBlob(bcdSignature), "winload.exe"));

            string sys32 = Path.Combine(win, "Windows", "System32");
            Directory.CreateDirectory(Path.Combine(sys32, "config"));
            File.WriteAllText(Path.Combine(sys32, "winload.exe"), "stub");
            File.WriteAllBytes(Path.Combine(sys32, "config", "SYSTEM"), BuildSystem());

            return new DiskLayout(root, sys, win, new BootCheckInput
            {
                Uefi = false,
                SystemRoot = sys + "\\",
                WindowsRoot = win + "\\",
                MbrSignature = diskSignature,
            });
        }

        /// <summary>디스크 서명 4바이트를 중간에 심은 장치 요소 blob.</summary>
        private static byte[] SignatureBlob(uint signature)
        {
            var blob = new byte[48];
            BitConverter.GetBytes(signature).CopyTo(blob, 20);
            return blob;
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

        /// <summary>
        /// 부트 매니저 + winload OS 로더 객체를 가진 BCD 하이브. 장치 요소(osdevice/device)에는
        /// <paramref name="deviceGuid"/>를 내장해 실제 BCD 장치 참조를 흉내 냅니다.
        /// </summary>
        private static byte[] BuildBcd(Guid deviceGuid) => BuildBcd(DeviceBlob(deviceGuid), "winload.efi");

        private static byte[] BuildBcd(byte[] devBlob, string loaderName)
        {
            const string bootMgr = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
            const string loaderGuid = "{11111111-2222-3333-4444-555555555555}";

            var b = new HiveBuilder();
            // OS 로더: 12000002=winload 경로, 21000001=osdevice, 11000001=device
            int e12 = b.AddKey("12000002", [b.Sz("Element", $@"\Windows\system32\{loaderName}")], []);
            int e21 = b.AddKey("21000001", [b.Binary("Element", devBlob)], []);
            int e11 = b.AddKey("11000001", [b.Binary("Element", devBlob)], []);
            int loaderElems = b.AddKey("Elements", [], [e12, e21, e11]);
            int loaderObj = b.AddKey(loaderGuid, [], [loaderElems]);

            // 부트 매니저: 23000003=기본 로더 GUID, 11000001=device
            int d23 = b.AddKey("23000003", [b.Sz("Element", loaderGuid)], []);
            int d11 = b.AddKey("11000001", [b.Binary("Element", devBlob)], []);
            int mgrElems = b.AddKey("Elements", [], [d23, d11]);
            int mgrObj = b.AddKey(bootMgr, [], [mgrElems]);

            int objects = b.AddKey("Objects", [], [loaderObj, mgrObj]);
            int rootKey = b.AddKey("", [], [objects]);
            return b.Finish(rootKey);
        }

        /// <summary>GUID를 중간에 심은 장치 요소 blob (실제 BCD 장치 요소를 흉내).</summary>
        private static byte[] DeviceBlob(Guid guid)
        {
            var blob = new byte[48];
            guid.ToByteArray().CopyTo(blob, 16);
            return blob;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 정리 실패는 무시 */ }
        }
    }
}
