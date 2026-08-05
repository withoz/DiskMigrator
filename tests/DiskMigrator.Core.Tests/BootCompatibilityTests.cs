using DiskMigrator.Core.Registry;
using Xunit;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// "이 조합으로 부팅할 수 있는가" 판정 — 잘못 말하면 사용자가 메인보드를 헛되이 사거나,
/// 안 되는 조합에 며칠을 씁니다.
/// </summary>
/// <remarks>
/// 특히 2026-08-04 조사의 교훈을 규칙으로 담았습니다. 그 보드(2018년)는 부팅 메뉴에 디스크가
/// 정상적으로 떴는데도 콜드 부팅에서 실패했습니다 — 사양표로 "된다"고 단정하면 틀립니다.
/// </remarks>
public class BootCompatibilityTests
{
    private static readonly DateTime Bios2012 = new(2012, 6, 1);
    private static readonly DateTime Bios2018 = new(2018, 5, 1);   // B360M Pro4 세대
    private static readonly DateTime Bios2025 = new(2025, 7, 18);  // 이 PC(B860)

    [Fact]
    public void 레거시_부팅에_NVMe는_확정적으로_불가()
    {
        var r = BootCompatibility.Evaluate(isUefi: false, Bios2018, "Nvme");

        Assert.Equal(CompatibilityVerdict.Unsupported, r.Verdict);
        Assert.Equal(VerdictConfidence.Certain, r.Confidence);
        // "안 됩니다"로 끝내지 않고 대안을 줍니다.
        Assert.Contains("SATA", r.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 아주_오래된_펌웨어에_NVMe는_지원_안_됨()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2012, "Nvme");

        Assert.Equal(CompatibilityVerdict.Unsupported, r.Verdict);
        Assert.Equal(VerdictConfidence.High, r.Confidence);
    }

    /// <summary>
    /// 이번 조사의 그 세대. 사양만 보고 "된다"고 하면 안 됩니다.
    /// </summary>
    [Fact]
    public void 중간세대_2018년_NVMe는_불확실로_두고_콜드부팅_확인을_요구한다()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2018, "Nvme");

        Assert.Equal(CompatibilityVerdict.Uncertain, r.Verdict);
        Assert.NotEmpty(r.UserChecks);

        // 웜/콜드 차이를 반드시 확인하게 해야 합니다 — 이게 이번 조사의 핵심 교훈입니다.
        Assert.Contains(r.UserChecks, c => c.Contains("power-off", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cold", r.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 최신_보드_NVMe는_지원으로_판정()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2025, "Nvme");

        Assert.Equal(CompatibilityVerdict.Supported, r.Verdict);
        Assert.Empty(r.UserChecks);
    }

    [Fact]
    public void USB_대상은_부팅용으로_거절()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2025, "Usb");

        Assert.Equal(CompatibilityVerdict.Unsupported, r.Verdict);
        Assert.Equal(VerdictConfidence.Certain, r.Confidence);
    }

    [Fact]
    public void 날짜를_모르면_단정하지_않는다()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, biosReleaseDate: null, "Nvme");

        Assert.Equal(CompatibilityVerdict.Uncertain, r.Verdict);
        Assert.Equal(VerdictConfidence.Low, r.Confidence);
        Assert.NotEmpty(r.UserChecks);
    }

    [Fact]
    public void 최신_보드에_SATA는_문제없음()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2025, "Sata");
        Assert.Equal(CompatibilityVerdict.Supported, r.Verdict);
    }

    [Fact]
    public void UEFI_PC에_MBR_원본이면_변환을_안내한다()
    {
        var r = BootCompatibility.Evaluate(isUefi: true, Bios2025, "Sata", targetIsMbr: true);

        Assert.Equal(CompatibilityVerdict.Supported, r.Verdict);
        Assert.Contains("GPT/UEFI", r.Advice);
    }

    [Fact]
    public void 모든_판정에는_이유와_조언이_붙는다()
    {
        foreach (var r in new[]
        {
            BootCompatibility.Evaluate(false, Bios2018, "Nvme"),
            BootCompatibility.Evaluate(true, Bios2012, "Nvme"),
            BootCompatibility.Evaluate(true, Bios2018, "Nvme"),
            BootCompatibility.Evaluate(true, Bios2025, "Nvme"),
            BootCompatibility.Evaluate(true, null, "Nvme"),
            BootCompatibility.Evaluate(true, Bios2025, "Usb"),
            BootCompatibility.Evaluate(true, Bios2025, "Sata"),
        })
        {
            // 근거 없이 판정만 던지면 사용자가 납득할 수 없습니다.
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.False(string.IsNullOrWhiteSpace(r.Advice));
        }
    }
}
