namespace DiskMigrator.Core.Registry;

/// <summary>호환성 판정.</summary>
public enum CompatibilityVerdict
{
    /// <summary>부팅할 수 있습니다.</summary>
    Supported,

    /// <summary>될 수도 있고 안 될 수도 있습니다 — 사용자가 확인해야 합니다.</summary>
    Uncertain,

    /// <summary>이 조합으로는 부팅할 수 없습니다.</summary>
    Unsupported,

    /// <summary>판정할 근거가 부족합니다.</summary>
    Unknown,
}

/// <summary>판정에 대한 확신 정도.</summary>
public enum VerdictConfidence
{
    /// <summary>규칙상 확정적입니다(예: 레거시 펌웨어 + NVMe).</summary>
    Certain,

    /// <summary>강한 근거가 있습니다.</summary>
    High,

    /// <summary>정황뿐입니다 — 확인이 필요합니다.</summary>
    Low,
}

/// <summary>호환성 판정 결과.</summary>
/// <param name="Verdict">판정.</param>
/// <param name="Confidence">확신도. <b>근거 없이 단정하지 않기 위해 항상 함께 냅니다.</b></param>
/// <param name="Reason">그렇게 판정한 이유.</param>
/// <param name="Advice">사용자가 할 수 있는 일 — "안 됩니다"로 끝내지 않습니다.</param>
/// <param name="UserChecks">우리가 볼 수 없어 사용자에게 부탁해야 하는 확인 항목.</param>
public sealed record CompatibilityResult(
    CompatibilityVerdict Verdict,
    VerdictConfidence Confidence,
    string Reason,
    string Advice,
    IReadOnlyList<string> UserChecks);

/// <summary>
/// "이 PC가 그 디스크로 부팅할 수 있는가"를 판정합니다.
/// </summary>
/// <remarks>
/// <b>사양표만으로는 부족합니다.</b> 2026-08-04 조사에서 ASRock B360M Pro4는 부팅 메뉴에 항목이
/// 정상적으로 떴는데도 콜드 부팅에서 실패했습니다. 그래서 이 판정은 "된다"를 쉽게 말하지 않고,
/// 애매하면 <see cref="CompatibilityVerdict.Uncertain"/>과 함께 <b>사용자가 확인할 항목</b>을 냅니다.
///
/// <para>확실히 말할 수 있는 것은 확실히 말합니다 — 레거시 전용 펌웨어에 NVMe는 옵션롬이 없어
/// 부팅이 불가능하고, 이건 확정입니다.</para>
/// </remarks>
public static class BootCompatibility
{
    /// <summary>NVMe 부팅이 펌웨어에 널리 들어온 시점. 이보다 오래되면 지원을 기대하기 어렵습니다.</summary>
    private static readonly DateTime NvmeBootEra = new(2014, 1, 1);

    /// <summary>이 시점 이후 보드는 NVMe 부팅이 사실상 표준입니다.</summary>
    private static readonly DateTime NvmeMatureEra = new(2019, 1, 1);

    /// <param name="isUefi">이 PC가 UEFI로 부팅했는지.</param>
    /// <param name="biosReleaseDate">BIOS 릴리스 날짜(모르면 null).</param>
    /// <param name="targetBusType">붙이려는 디스크의 버스 종류(예: "Nvme", "Sata", "Usb").</param>
    /// <param name="targetIsMbr">대상(또는 원본)이 MBR 구성인지.</param>
    public static CompatibilityResult Evaluate(
        bool isUefi,
        DateTime? biosReleaseDate,
        string targetBusType,
        bool targetIsMbr = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBusType);

        bool nvme = targetBusType.Contains("Nvme", StringComparison.OrdinalIgnoreCase);
        bool usb = targetBusType.Contains("Usb", StringComparison.OrdinalIgnoreCase);

        // USB는 부팅 대상으로 부적합합니다 — Windows가 정식 지원하지 않습니다.
        if (usb)
        {
            return new(CompatibilityVerdict.Unsupported, VerdictConfidence.Certain,
                "The target is connected over USB. Windows does not officially support booting an " +
                "installed system from a USB-attached disk.",
                "Install the disk internally (SATA or M.2) and try again. USB is fine for backup images, " +
                "just not as a boot device.",
                []);
        }

        // 레거시 전용 펌웨어 + NVMe = 확정적으로 불가. NVMe에는 레거시 부팅 옵션롬이 없습니다.
        if (!isUefi && nvme)
        {
            return new(CompatibilityVerdict.Unsupported, VerdictConfidence.Certain,
                "This PC booted in legacy (CSM/BIOS) mode, and NVMe drives have no legacy boot option ROM. " +
                "There is no path for the firmware to start an operating system from an NVMe disk this way.",
                "Either switch the firmware to UEFI mode (and convert the disk to GPT/UEFI), or use a " +
                "SATA SSD instead. If the board has no UEFI mode at all, the board must be replaced.",
                ["In the firmware setup, check whether a UEFI (non-CSM) boot mode exists."]);
        }

        if (nvme && biosReleaseDate is { } date)
        {
            if (date < NvmeBootEra)
            {
                return new(CompatibilityVerdict.Unsupported, VerdictConfidence.High,
                    $"The firmware dates from {date:yyyy-MM}, before NVMe boot support became common. " +
                    "Boards from that era usually cannot start an OS from an NVMe drive even with UEFI.",
                    "Use a SATA SSD on this PC, or move the disk to a newer machine. " +
                    "Check the board maker's site for a BIOS update mentioning NVMe — if there is none, " +
                    "this combination will not work.",
                    ["Check whether the firmware boot menu lists the NVMe disk at all."]);
            }

            if (date < NvmeMatureEra)
            {
                return new(CompatibilityVerdict.Uncertain, VerdictConfidence.Low,
                    $"The firmware dates from {date:yyyy-MM}. Boards from this period sometimes boot NVMe " +
                    "and sometimes do not, and the spec sheet is not a reliable guide — a board can list the " +
                    "disk in its boot menu and still fail to start it from a cold power-on.",
                    "Test it properly before relying on it: after the first successful boot, shut the PC " +
                    "down completely (not restart), wait, and power it on again. If a restart works but a " +
                    "cold boot does not, the firmware cannot initialise that drive and no software fix will help.",
                    [
                        "Does the firmware boot menu list the NVMe disk?",
                        "After a successful boot, does a FULL power-off and power-on also work?",
                        "Is Fast Boot disabled in the firmware setup?",
                    ]);
            }

            return new(CompatibilityVerdict.Supported, VerdictConfidence.High,
                $"The firmware dates from {date:yyyy-MM} and this PC boots in UEFI mode. " +
                "NVMe boot is standard on boards of this generation.",
                targetIsMbr
                    ? "The source layout is MBR, so convert the copy to GPT/UEFI after cloning — " +
                      "the app can do this automatically."
                    : "No compatibility obstacle found for this combination.",
                []);
        }

        if (nvme)
        {
            return new(CompatibilityVerdict.Uncertain, VerdictConfidence.Low,
                "The firmware release date could not be read, so its NVMe boot support cannot be judged.",
                "Check the firmware boot menu for the NVMe disk, and test a full power-off/power-on " +
                "cycle before relying on it.",
                [
                    "Does the firmware boot menu list the NVMe disk?",
                    "After a successful boot, does a FULL power-off and power-on also work?",
                ]);
        }

        // SATA 등 그 밖의 내장 연결.
        if (!isUefi && !targetIsMbr)
        {
            return new(CompatibilityVerdict.Uncertain, VerdictConfidence.Low,
                "This PC booted in legacy (CSM/BIOS) mode while the disk layout is GPT/UEFI. " +
                "A GPT disk generally will not boot in legacy mode.",
                "Switch the firmware to UEFI mode, or keep the source's MBR layout.",
                ["In the firmware setup, check whether CSM is enabled and whether UEFI mode is available."]);
        }

        return new(CompatibilityVerdict.Supported, VerdictConfidence.High,
            isUefi
                ? "UEFI boot with a directly attached (non-NVMe) disk — a well-supported combination."
                : "Legacy boot with an MBR layout — a consistent combination.",
            targetIsMbr && isUefi
                ? "The layout is MBR while this PC boots in UEFI mode; convert the copy to GPT/UEFI after cloning."
                : "No compatibility obstacle found for this combination.",
            []);
    }
}
