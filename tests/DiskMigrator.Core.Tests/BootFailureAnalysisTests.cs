using DiskMigrator.Core.Registry;
using Xunit;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 개별 진단을 엮어 원인을 추리는 규칙 — 이 프로젝트가 며칠에 걸쳐 손으로 한 추론입니다.
/// </summary>
/// <remarks>
/// 진단 하나하나는 사실만 말합니다. 그것들을 합쳐야 원인이 보이고, 그 연결이 틀리면
/// 사용자가 멀쩡한 부품을 교체하거나 엉뚱한 곳에 시간을 씁니다.
/// </remarks>
public class BootFailureAnalysisTests
{
    private static BootDriverInventoryResult Drivers(params string[] missing)
    {
        var all = new List<BootDriverEntry>();
        foreach (string m in missing)
            all.Add(new BootDriverEntry(m, "", "", $@"C:\Windows\System32\drivers\{m}.sys", false, null, false));

        // 정상 드라이버도 몇 개 섞어 실제 형태에 가깝게.
        for (int i = 0; i < 3; i++)
            all.Add(new BootDriverEntry($"ok{i}", "SCSI Miniport", "", "x", true, 1024, false));

        return new BootDriverInventoryResult("ControlSet001", all,
            all.Where(d => !d.FileExists).ToList(), []);
    }

    private static FastStartupStateResult Fast(bool resume) =>
        new(resume ? 1u : 0u, null, resume, resume ? 12_000_000_000 : null, resume);

    private static EspAuditResult Esp(bool mgr = true, bool bcd = true, string? authority = SigningAuthority.Pca2011) =>
        new(true, mgr, true, bcd,
            authority is null ? null : new BootManagerSignature("CN=...", authority, null, null),
            [], 100, 1024, []);

    private static BootTraceResult Trace(BootProgress p) =>
        new([], DateTime.UtcNow, p, [], []);

    private static BootReadinessReport Boot(bool wouldBoot, string? failedCode = null)
    {
        var items = new List<BootCheckItem>
        {
            new("x", wouldBoot, BootCheckSeverity.Fatal, "", null),
        };
        if (failedCode is not null)
            items.Add(new("ref", false, BootCheckSeverity.Fatal, "", failedCode));
        return new BootReadinessReport(items);
    }

    /// <summary>
    /// 오프라인 디스크에서 "검사 실패"를 부팅 결함으로 읽어서는 안 됩니다.
    /// </summary>
    /// <remarks>
    /// 2026-08-05 실기에서 드러난 자리입니다. 오프라인 디스크는 열리고 파티션 테이블도 읽히지만
    /// 볼륨이 마운트되지 않아 ESP도 Windows 폴더도 <b>없는 것처럼 보입니다.</b> 그것을 결함으로
    /// 읽으면 멀쩡한 디스크에 부팅 복구를 하겠다고 덤비게 됩니다 — 원인은 그냥 오프라인인데.
    /// </remarks>
    [Fact]
    public void 오프라인_디스크는_부팅_결함으로_읽지_않는다()
    {
        var r = BootFailureAnalysis.Analyze(
            boot: Boot(wouldBoot: false),               // 검사가 통과하지 못했지만
            drivers: null,                              // 볼륨을 못 읽어 진단들이 비어 있습니다
            fastStartup: null,
            trace: null,
            esp: null,
            diskIsOffline: true);

        var cause = Assert.Single(r.Causes);
        Assert.Equal(BootFailureAnalysis.CodeDiskOffline, cause.Code);
        Assert.Equal("Certain", cause.Confidence);

        // 무엇을 해야 하는지가 구체적이어야 합니다 — "확인하십시오"로는 부족합니다.
        Assert.Contains("online", cause.Action, StringComparison.OrdinalIgnoreCase);

        // 다른 원인을 함께 늘어놓으면 사용자가 그쪽을 먼저 건드립니다.
        Assert.DoesNotContain(r.Causes, c => c.Code == BootFailureAnalysis.CodeOutsideDisk);
        Assert.Contains("offline", r.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>온라인이면 지금까지의 판단이 그대로여야 합니다 — 기본값이 판단을 바꾸면 안 됩니다.</summary>
    [Fact]
    public void 온라인이면_기존_판단이_그대로다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(true), Drivers(), Fast(false), Trace(BootProgress.BootloaderOnly), Esp(),
            diskIsOffline: false);

        Assert.Contains(r.Causes, c => c.Code == BootFailureAnalysis.CodeOutsideDisk);
        Assert.DoesNotContain(r.Causes, c => c.Code == BootFailureAnalysis.CodeDiskOffline);
    }

    /// <summary>
    /// 2026-08-04 조사가 도달한 결론 — 디스크는 온전한데 커널이 시작조차 못 한 경우.
    /// </summary>
    [Fact]
    public void 디스크가_멀쩡한데_커널이_못_뜨면_원인은_디스크_밖()
    {
        var r = BootFailureAnalysis.Analyze(
            boot: Boot(wouldBoot: true),
            drivers: Drivers(),                       // 누락 없음
            fastStartup: Fast(resume: false),
            trace: Trace(BootProgress.BootloaderOnly),
            esp: Esp());

        Assert.Contains(r.Causes, c => c.Code == BootFailureAnalysis.CodeOutsideDisk);

        // 웜/콜드 차이를 반드시 확인하게 해야 합니다 — 그 한 줄이 이번 조사의 결정타였습니다.
        Assert.Contains("power-off",
            r.Causes.Single(c => c.Code == BootFailureAnalysis.CodeOutsideDisk).Action,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.UserChecks, c => c.Contains("power-off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 드라이버_파일_누락은_확정적_원인()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(true), Drivers("stornvme"), Fast(false), Trace(BootProgress.BootloaderOnly), Esp());

        var cause = r.Causes.First();
        Assert.Equal(BootFailureAnalysis.CodeMissingDriverFiles, cause.Code);
        Assert.Equal("Certain", cause.Confidence);
        Assert.Contains("stornvme", cause.Finding);

        // 확정적인 원인이 있으면 "디스크 밖" 결론을 내지 않아야 합니다.
        Assert.DoesNotContain(r.Causes, c => c.Code == BootFailureAnalysis.CodeOutsideDisk);
    }

    [Fact]
    public void 재개_이미지가_있으면_높은_확신으로_지목한다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(true), Drivers(), Fast(resume: true), Trace(BootProgress.BootloaderOnly), Esp());

        var cause = r.Causes.Single(c => c.Code == BootFailureAnalysis.CodeResumeImage);
        Assert.Equal("High", cause.Confidence);
        Assert.Contains("hangs at the logo", cause.Finding);

        // 재개 이미지가 원인 후보로 잡혔으면 디스크가 '멀쩡하다'고 볼 수 없습니다.
        Assert.DoesNotContain(r.Causes, c => c.Code == BootFailureAnalysis.CodeOutsideDisk);
    }

    [Fact]
    public void 부팅관리자가_없으면_확정적_원인()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(false), Drivers(), Fast(false), Trace(BootProgress.BootloaderOnly), Esp(mgr: false));

        Assert.Contains(r.Causes, c => c.Code == BootFailureAnalysis.CodeBootManagerMissing && c.Confidence == "Certain");
    }

    [Fact]
    public void BCD_장치참조_불일치를_잡아낸다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(false, BootReadinessCheck.CodeDeviceRef), Drivers(), Fast(false),
            Trace(BootProgress.BootloaderOnly), Esp());

        Assert.Contains(r.Causes, c => c.Code == BootFailureAnalysis.CodeDeviceReference);
    }

    /// <summary>
    /// 2023 서명은 정황일 뿐이라 낮은 확신으로 두고, 사용자에게 확인을 요청해야 합니다.
    /// </summary>
    [Fact]
    public void CA2023_서명은_낮은_확신으로_두고_확인을_요청한다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(true), Drivers(), Fast(false), Trace(BootProgress.BootloaderOnly),
            Esp(authority: SigningAuthority.Ca2023));

        var cause = r.Causes.Single(c => c.Code == BootFailureAnalysis.CodeSignature2023);
        Assert.Equal("Low", cause.Confidence);
        Assert.Contains(r.UserChecks, c => c.Contains("2023"));
    }

    [Fact]
    public void 아무_문제가_없으면_디스크_밖을_보라고_말한다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(true), Drivers(), Fast(false), Trace(BootProgress.BootCompleted), Esp());

        Assert.Empty(r.Causes);
        Assert.Contains("check_hardware_compatibility", r.Verdict);
    }

    [Fact]
    public void 진단이_없으면_단정하지_않는다()
    {
        var r = BootFailureAnalysis.Analyze(null, null, null, null, null);

        Assert.Empty(r.Causes);
        Assert.Contains("Nothing conclusive", r.Verdict);
    }

    [Fact]
    public void 모든_원인에는_근거와_조치가_붙는다()
    {
        var r = BootFailureAnalysis.Analyze(
            Boot(false, BootReadinessCheck.CodeDeviceRef), Drivers("stornvme"), Fast(true),
            Trace(BootProgress.BootloaderOnly), Esp(mgr: false, authority: SigningAuthority.Ca2023));

        Assert.NotEmpty(r.Causes);
        foreach (var c in r.Causes)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Finding));
            Assert.False(string.IsNullOrWhiteSpace(c.Action));
            Assert.Contains(c.Confidence, new[] { "Certain", "High", "Low" });
        }
    }
}
