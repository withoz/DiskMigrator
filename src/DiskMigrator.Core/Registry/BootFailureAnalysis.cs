namespace DiskMigrator.Core.Registry;

/// <summary>원인 후보 하나.</summary>
/// <param name="Code">언어 무관 식별자.</param>
/// <param name="Confidence">확신도 — Certain · High · Low.</param>
/// <param name="Finding">무엇을 근거로 그렇게 보는지.</param>
/// <param name="Action">사용자가 할 수 있는 일.</param>
public sealed record BootFailureCause(string Code, string Confidence, string Finding, string Action);

/// <summary>부팅 실패 원인 분석 결과.</summary>
/// <param name="Causes">가능성이 높은 순서로 정렬된 원인 후보.</param>
/// <param name="Verdict">종합 판단 한 문장.</param>
/// <param name="UserChecks">우리가 볼 수 없어 사용자에게 물어야 하는 것.</param>
public sealed record BootFailureAnalysisResult(
    IReadOnlyList<BootFailureCause> Causes,
    string Verdict,
    IReadOnlyList<string> UserChecks);

/// <summary>
/// 개별 진단 결과를 <b>엮어서</b> "왜 안 뜨는가"를 추립니다.
/// </summary>
/// <remarks>
/// 진단 하나하나는 사실만 말합니다 — 드라이버 파일이 있다, 재개 이미지가 있다, 커널이 어디까지 갔다.
/// 그것들을 합쳐야 원인이 보입니다. 2026-08-04 조사에서 며칠이 걸린 이유가 이 연결을 손으로 했기
/// 때문입니다: 부트로더만 실행됐다 + 드라이버는 멀쩡하다 + ESP도 정상이다 → 그러면 원인은
/// 디스크 밖이다, 라는 추론에 도달하는 데 오래 걸렸습니다.
///
/// <para><b>확신도를 반드시 함께 냅니다.</b> 근거가 약한 추측을 단정처럼 말하면, 사용자가 멀쩡한
/// 부품을 교체하거나 엉뚱한 곳에 시간을 씁니다.</para>
/// </remarks>
public static class BootFailureAnalysis
{
    public const string CodeMissingDriverFiles = "MISSING_BOOT_DRIVER_FILES";
    public const string CodeResumeImage = "RESUME_IMAGE_PRESENT";
    public const string CodeBootManagerMissing = "BOOT_MANAGER_MISSING";
    public const string CodeBcdMissing = "BCD_MISSING";
    public const string CodeSignature2023 = "BOOT_MANAGER_SIGNED_2023";
    public const string CodeDeviceReference = "BCD_DEVICE_REFERENCE";
    public const string CodeThirdPartyBootDriver = "THIRD_PARTY_BOOT_DRIVER";
    public const string CodeOutsideDisk = "CAUSE_OUTSIDE_DISK";

    /// <summary>
    /// 1단계 진단 결과들을 받아 원인을 추립니다 — 순수 함수라 테스트로 고정할 수 있습니다.
    /// </summary>
    /// <param name="boot">부팅 준비 검사(없으면 null).</param>
    /// <param name="drivers">부팅 드라이버 조사(없으면 null).</param>
    /// <param name="fastStartup">빠른 시작 상태(없으면 null).</param>
    /// <param name="trace">부팅 흔적(없으면 null).</param>
    /// <param name="esp">ESP 감사(없으면 null).</param>
    public static BootFailureAnalysisResult Analyze(
        BootReadinessReport? boot,
        BootDriverInventoryResult? drivers,
        FastStartupStateResult? fastStartup,
        BootTraceResult? trace,
        EspAuditResult? esp)
    {
        var causes = new List<BootFailureCause>();
        var checks = new List<string>();

        // --- 확정적인 것부터 -------------------------------------------------

        if (drivers is { MissingFiles.Count: > 0 })
        {
            causes.Add(new(CodeMissingDriverFiles, "Certain",
                $"{drivers.MissingFiles.Count} boot-start driver(s) are registered but their files are missing: " +
                string.Join(", ", drivers.MissingFiles.Select(d => d.ServiceName)) + ". " +
                "The loader stalls when it cannot find one of these.",
                "Restore the missing driver files, or restore the disk from a known-good backup."));
        }

        if (esp is { Uefi: true, BootManagerPresent: false })
        {
            causes.Add(new(CodeBootManagerMissing, "Certain",
                "The ESP has no boot manager (bootmgfw.efi) — the firmware has nothing to start.",
                "Run boot repair, which rewrites the boot files."));
        }

        if (esp is { Uefi: true, BootManagerPresent: true, BcdPresent: false })
        {
            causes.Add(new(CodeBcdMissing, "Certain",
                "The boot manager is present but the BCD store is missing, so it does not know what to boot.",
                "Run boot repair to rebuild the BCD."));
        }

        if (fastStartup is { ResumeWouldBeAttempted: true })
        {
            causes.Add(new(CodeResumeImage, "High",
                $"A hibernation image is present ({fastStartup.HiberfilSizeBytes / 1024 / 1024} MB). The boot " +
                "manager will try to RESUME the saved session instead of booting, and that saved state assumes " +
                "the original hardware — on a different PC it hangs at the logo with no error.",
                "Run boot repair, which turns off Fast Startup and clears the image."));
        }

        if (boot is not null)
        {
            var deviceRef = boot.Items.FirstOrDefault(i => i.Code == BootReadinessCheck.CodeDeviceRef);
            if (deviceRef is { Passed: false })
            {
                causes.Add(new(CodeDeviceReference, "High",
                    "The BCD points at a different disk than this one. This happens when the source and the copy " +
                    "were connected together and Windows re-signed one of them.",
                    "Run boot repair, which rewrites the device references to this disk."));
            }
        }

        if (esp?.Signature?.Authority == SigningAuthority.Ca2023)
        {
            causes.Add(new(CodeSignature2023, "Low",
                "The boot manager is signed by the 2023 UEFI CA. Boards made before 2023 may not carry that " +
                "certificate, and some firmware then hangs with no error instead of reporting a Secure Boot failure.",
                "If the target PC is older, disable Secure Boot and retry. If that fixes it, the signature was the cause."));
            checks.Add("Is the target PC's board from before 2023, and is Secure Boot enabled there?");
        }

        // --- 아무것도 안 잡혔는데 부팅이 안 되는 경우 -------------------------

        bool diskLooksHealthy =
            drivers is { MissingFiles.Count: 0 } &&
            esp is { BootManagerPresent: true, BcdPresent: true } &&
            fastStartup is { ResumeWouldBeAttempted: false } &&
            boot is { WouldBoot: true };

        if (diskLooksHealthy && trace is { Progress: BootProgress.BootloaderOnly })
        {
            // 2026-08-04 조사가 도달한 결론입니다 — 디스크는 멀쩡한데 커널이 시작조차 못 함.
            causes.Add(new(CodeOutsideDisk, "High",
                "Everything on the disk checks out — boot files, BCD, all boot-start driver files, no resume image — " +
                "yet the loader ran and the kernel left no trace at all. When the disk is sound and the kernel " +
                "still cannot start, the cause is usually outside the disk: firmware that cannot initialise the " +
                "drive, or the drive/slot itself.",
                "Test whether a RESTART works while a full power-off/power-on does not. If restarts succeed and " +
                "cold boots fail, the firmware cannot wake that drive and no software fix will help — use a " +
                "different drive type or another PC."));

            checks.Add("Does the firmware boot menu list this disk?");
            checks.Add("After a successful boot, does a FULL power-off and power-on also work?");
            checks.Add("Is Fast Boot disabled in the firmware setup?");
        }

        string verdict = causes.Count == 0
            ? diskLooksHealthy
                ? "No fault was found on this disk. If it still will not boot, look at the PC it is connected to " +
                  "rather than the disk — start with check_hardware_compatibility."
                : "Nothing conclusive was found. Some diagnostics could not run — check that the disk is online " +
                  "and its volumes are reachable, then run them again."
            : causes[0].Confidence == "Certain"
                ? $"Most likely cause: {causes[0].Code}. This one is conclusive — fix it first."
                : $"Most likely cause: {causes[0].Code} ({causes[0].Confidence.ToLowerInvariant()} confidence), " +
                  $"with {causes.Count - 1} other candidate(s).";

        return new BootFailureAnalysisResult(causes, verdict, checks);
    }
}
