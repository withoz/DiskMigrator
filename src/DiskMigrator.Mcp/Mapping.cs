using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Mcp.Dto;

namespace DiskMigrator.Mcp;

/// <summary>
/// 엔진 모델 → DTO 변환. <b>민감 정보 마스킹도 여기서 함께 처리합니다</b>(계획서 §5.2·§7).
/// </summary>
/// <remarks>
/// 변환을 한곳에 모으는 이유: 진단 결과는 대화 로그에 남으므로, 시리얼·볼륨 레이블이
/// 어디선가 빠져나가면 사용자가 의도치 않게 공유하게 됩니다. 통로가 하나여야 빠뜨리지 않습니다.
/// </remarks>
public sealed class Mapping(bool includeSensitive = false)
{
    /// <summary>시리얼·볼륨 레이블을 그대로 내보낼지. 사용자가 앱에서 켤 때만 참입니다.</summary>
    public bool IncludeSensitive { get; } = includeSensitive;

    public DiskDto ToDto(DiskInfo d) => new(
        DeviceNumber: d.DeviceNumber,
        Model: d.Model,
        SerialNumber: Mask(d.SerialNumber),
        SizeBytes: d.SizeBytes,
        SizeText: SizeFormatter.Format(d.SizeBytes),
        BusType: d.BusType.ToString(),
        PartitionStyle: d.PartitionStyle.ToString(),
        IsSystemDisk: d.IsSystemDisk,
        IsBootDisk: d.IsBootDisk,
        HasPageFile: d.HasPageFile,
        IsRemovable: d.IsRemovable,
        IsReadOnly: d.IsReadOnly,
        IsOffline: d.IsOffline,
        DiskGuid: d.DiskGuid?.ToString("B"),
        MbrSignature: d.MbrSignature is { } s ? $"0x{s:X8}" : null,
        LogicalSectorSize: d.LogicalSectorSize,
        PartitionCount: d.Partitions.Count);

    public DiskDetailDto ToDetailDto(DiskInfo d) =>
        new(ToDto(d), d.Partitions.Select(ToDto).ToList());

    public PartitionDto ToDto(PartitionInfo p) => new(
        Number: p.Number,
        OffsetBytes: p.StartingOffset,
        SizeBytes: p.LengthBytes,
        SizeText: SizeFormatter.Format(p.LengthBytes),
        DriveLetter: p.DriveLetter,
        FileSystem: p.FileSystem,
        Label: Mask(p.VolumeLabel),
        Kind: DescribeKind(p),
        IsActive: p.IsActive,
        // 여유 공간만 알 수 있으므로 사용량은 역산합니다. 마운트 안 된 볼륨은 알 수 없어 null입니다.
        UsedBytes: p.FreeSpaceBytes is { } free ? p.LengthBytes - free : null);

    /// <summary>부팅 준비 검사 결과를 DTO로. 요약 한 줄은 여기서 만듭니다.</summary>
    public BootCheckDto ToDto(Core.Registry.BootReadinessReport r)
    {
        int fatalFailed = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Fatal && i.Passed == false);
        int unknown = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Fatal && i.Passed is null);
        int warned = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Warning && i.Passed == false);

        // "확인 못 함"과 "실패"를 구분해 말합니다 — 볼륨이 안 잡혀 못 본 것을 결함으로 단정하면
        // 사용자가 멀쩡한 디스크를 의심하게 됩니다.
        string summary = r.WouldBoot
            ? warned > 0
                ? $"Boot configuration looks complete, but {warned} warning(s) may matter on different hardware."
                : "Boot configuration looks complete."
            : fatalFailed > 0
                ? $"{fatalFailed} critical check(s) failed — this disk is not expected to boot."
                : $"{unknown} critical check(s) could not be verified (volume not mounted?) — boot cannot be confirmed.";

        return new BootCheckDto(r.WouldBoot, r.HasWarnings, summary, r.Items.Select(ToDto).ToList());
    }

    public BootCheckItemDto ToDto(Core.Registry.BootCheckItem i) =>
        new(i.Name, i.Passed, i.Severity.ToString(), i.Detail, i.Code);

    /// <summary>부팅 드라이버 조사 결과를 DTO로.</summary>
    /// <remarks>
    /// 정상 드라이버 89개를 다 보내면 응답만 길어지고 판단에는 도움이 안 됩니다.
    /// <b>문제가 되는 것(파일 없음·표준 위치 밖)만</b> 목록으로 싣고, 나머지는 개수로 요약합니다.
    /// </remarks>
    public BootDriverInventoryDto ToDto(Core.Registry.BootDriverInventoryResult r)
    {
        string summary = r.MissingFiles.Count > 0
            ? $"{r.MissingFiles.Count} of {r.Drivers.Count} boot-start driver(s) are registered but their files are missing — " +
              "this alone can stop the kernel from starting."
            : r.OutsideSystem32.Count > 0
                ? $"All {r.Drivers.Count} boot-start driver files are present. " +
                  $"{r.OutsideSystem32.Count} load from outside System32 (third-party — may be fine, worth noting)."
                : $"All {r.Drivers.Count} boot-start driver files are present and load from standard locations.";

        return new BootDriverInventoryDto(
            ControlSet: r.ControlSet,
            TotalCount: r.Drivers.Count,
            MissingCount: r.MissingFiles.Count,
            OutsideSystem32Count: r.OutsideSystem32.Count,
            Summary: summary,
            Missing: r.MissingFiles.Select(ToDto).ToList(),
            OutsideSystem32: r.OutsideSystem32.Select(ToDto).ToList());
    }

    public BootDriverDto ToDto(Core.Registry.BootDriverEntry d) =>
        new(d.ServiceName, d.Group, d.ImagePath, d.ResolvedPath, d.FileExists, d.FileSizeBytes);

    /// <summary>호환성 판정과 그 근거가 된 펌웨어 정보를 함께 DTO로.</summary>
    /// <remarks>
    /// 판정만 보내면 Claude가 근거 없이 "메인보드를 바꾸세요"라고 말할 수 있습니다.
    /// 무엇을 보고 그렇게 판단했는지(BIOS 날짜·UEFI 여부·연결 방식)를 함께 실어,
    /// 사용자가 납득하거나 반박할 수 있게 합니다.
    /// </remarks>
    public CompatibilityDto ToDto(
        Core.Registry.CompatibilityResult r,
        Windows.Devices.FirmwareInfoResult fw,
        string targetBusType) =>
        new(
            Verdict: r.Verdict.ToString(),
            Confidence: r.Confidence.ToString(),
            Reason: r.Reason,
            Advice: r.Advice,
            UserChecks: r.UserChecks,
            Firmware: new FirmwareDto(
                fw.BoardManufacturer, fw.BoardProduct, fw.BiosVendor, fw.BiosVersion,
                fw.BiosReleaseDate, fw.SmbiosVersion, fw.IsUefi, fw.SecureBootEnabled),
            TargetBusType: targetBusType);

    /// <summary>안전성 판정을 DTO로. 엔진의 판정을 그대로 옮기고 재해석하지 않습니다.</summary>
    /// <remarks>
    /// 계획서 §4의 두 번째 원칙 — MCP 계층은 <see cref="Core.Safety.SafetyGuard"/>를 호출할 뿐
    /// 안전 판정을 다시 쓰지 않습니다. 여기서 하는 일은 형식 변환과 요약 문장 생성뿐입니다.
    /// </remarks>
    public SafetyDto ToDto(Core.Safety.SafetyReport r, Core.Models.DiskInfo source, Core.Models.DiskInfo target)
    {
        var blockers = r.Blockers.Select(ToDto).ToList();
        var confirmations = r.Confirmations.Select(ToDto).ToList();
        var warnings = r.Warnings.Select(ToDto).ToList();

        string summary = !r.CanProceed
            ? $"BLOCKED — {blockers.Count} reason(s) prevent this: {string.Join("; ", blockers.Select(b => b.Code))}. " +
              "No confirmation can override these."
            : r.NeedsTypedConfirmation
                ? $"Allowed, but the target holds data — the user must type the target model name in the app " +
                  $"before it will run. Warnings: {(warnings.Count == 0 ? "none" : string.Join("; ", warnings.Select(w => w.Code)))}."
                : warnings.Count > 0
                    ? $"Allowed. {warnings.Count} warning(s) worth mentioning: {string.Join("; ", warnings.Select(w => w.Code))}."
                    : "Allowed, with nothing of concern found.";

        return new SafetyDto(
            CanProceed: r.CanProceed,
            NeedsTypedConfirmation: r.NeedsTypedConfirmation,
            Summary: summary,
            Blockers: blockers,
            Confirmations: confirmations,
            Warnings: warnings,
            Source: ToDto(source),
            Target: ToDto(target));
    }

    public SafetyIssueDto ToDto(Core.Safety.SafetyIssue i) =>
        new(i.Code, i.Severity.ToString(), i.Message);

    /// <summary>이미지 무결성 검사 결과를 DTO로.</summary>
    public ImageInspectionDto ToDto(Windows.Jobs.ImageInspectionReport r)
    {
        var failed = r.Items.Where(i => !i.Passed).ToList();

        string summary = r.Ok
            ? $"The image passed all {r.Items.Count} checks and is safe to restore."
            : $"{failed.Count} check(s) failed — restoring this image may produce a broken disk. " +
              $"First failure: {failed[0].Name} — {failed[0].Detail}";

        return new ImageInspectionDto(
            r.Ok, summary,
            r.Items.Select(i => new ImageCheckItemDto(i.Name, i.Passed, i.Detail)).ToList());
    }

    /// <summary>ESP 감사 결과를 DTO로. 서명 발급자의 '의미'를 문장으로 붙입니다.</summary>
    public EspAuditDto ToDto(Core.Registry.EspAuditResult r)
    {
        string summary = !r.Uefi
            ? "No EFI System Partition contents were found — this looks like a BIOS/MBR layout, not UEFI."
            : !r.BootManagerPresent
                ? "The boot manager (bootmgfw.efi) is MISSING from the ESP — the firmware has nothing to start."
                : !r.BcdPresent
                    ? "The boot manager is present but the BCD store is missing — it will not know what to boot."
                    : r.Signature?.Authority == Core.Registry.SigningAuthority.Ca2023
                        ? "Boot files are complete, but the boot manager is signed by the 2023 CA — older boards " +
                          "may not carry that certificate and can fail Secure Boot verification silently."
                        : r.ForeignBootFolders.Count > 0
                            ? $"Boot files are complete. Note {r.ForeignBootFolders.Count} non-Microsoft boot " +
                              $"folder(s) left on the ESP: {string.Join(", ", r.ForeignBootFolders)}."
                            : "Boot files are complete and the boot manager is signed by a widely trusted authority.";

        return new EspAuditDto(
            Uefi: r.Uefi,
            BootManagerPresent: r.BootManagerPresent,
            FallbackPresent: r.FallbackPresent,
            BcdPresent: r.BcdPresent,
            Signature: r.Signature is null ? null : ToDto(r.Signature),
            KeyFiles: r.KeyFiles.Select(f => new EspFileDto(f.RelativePath, f.SizeBytes, f.LastWriteUtc)).ToList(),
            TotalFileCount: r.TotalFileCount,
            TotalSizeBytes: r.TotalSizeBytes,
            ForeignBootFolders: r.ForeignBootFolders,
            Summary: summary);
    }

    /// <summary>
    /// 서명 정보를 DTO로 — 발급자 문자열만 주면 Claude가 그 의미를 스스로 추측해야 하므로,
    /// <b>구형 보드에서 무엇을 뜻하는지</b>를 함께 담습니다.
    /// </summary>
    public SignatureDto ToDto(Core.Registry.BootManagerSignature s)
    {
        string meaning = s.Authority switch
        {
            Core.Registry.SigningAuthority.Pca2011 =>
                "Microsoft Windows Production PCA 2011 — trusted by practically every UEFI board, including " +
                "old ones. This is what you want on a disk destined for older hardware.",

            Core.Registry.SigningAuthority.Ca2023 =>
                "Windows UEFI CA 2023 — boards released before 2023 may not carry this certificate in their " +
                "Secure Boot database. Verification then fails, and some firmware simply hangs with no error. " +
                "If the target PC is older, either disable Secure Boot or use a 2011-signed boot manager.",

            Core.Registry.SigningAuthority.OtherMicrosoft =>
                "Signed by Microsoft, but not one of the two well-known boot authorities. Worth a closer look.",

            _ => "The signing authority could not be identified. Treat Secure Boot compatibility as unknown.",
        };

        return new SignatureDto(s.Issuer, s.Authority, s.NotBefore, s.NotAfter, meaning);
    }

    /// <summary>빠른 시작 상태를 DTO로. 요약에 "무슨 일이 벌어지는지"를 적습니다.</summary>
    public FastStartupDto ToDto(Core.Registry.FastStartupStateResult r)
    {
        string summary = r.ResumeWouldBeAttempted
            ? "A hibernation image (hiberfil.sys) is present, so the boot manager will try to RESUME " +
              "instead of booting. On different hardware that resume fails and hangs at the logo — " +
              "and boot diagnostics stay silent because they only apply to the normal boot path."
            : r.HiberbootEnabled == 1
                ? "No hibernation image right now, but Fast Startup is ON — shutting down will create one again."
                : "Fast Startup is off and there is no hibernation image. The disk will take the normal boot path.";

        return new FastStartupDto(
            r.HiberbootEnabled, r.HibernateEnabled, r.HiberfilExists, r.HiberfilSizeBytes,
            r.ResumeWouldBeAttempted, summary);
    }

    /// <summary>부팅 흔적 분석을 DTO로. 판정 문장이 이 도구의 핵심 산출물입니다.</summary>
    /// <remarks>
    /// 단계 이름만 돌려주면 Claude가 그 의미를 스스로 추론해야 합니다. 무엇을 뜻하는지와
    /// <b>다음에 무엇을 봐야 하는지</b>까지 문장으로 만들어, 조사가 엉뚱한 곳으로 새지 않게 합니다.
    /// </remarks>
    public BootTraceDto ToDto(Core.Registry.BootTraceResult r)
    {
        string verdict = r.Progress switch
        {
            Core.Registry.BootProgress.BootloaderOnly =>
                "The boot loader ran and wrote to the disk, but the kernel left no trace at all — " +
                "the registry hives and event log were not touched. The kernel never really started. " +
                "Look at storage drivers (read_boot_drivers), a pending resume image (read_fast_startup), " +
                "or firmware-level transfer problems. Note the loader could WRITE to the disk, so the " +
                "medium itself is reachable.",

            Core.Registry.BootProgress.KernelStarted =>
                "The kernel started and wrote to the registry, but device enumeration left no trace. " +
                "It stalled early — most likely while loading boot drivers.",

            Core.Registry.BootProgress.DevicesEnumerated =>
                "The kernel reached device enumeration and installed drivers, so it got quite far. " +
                "A failure after this point is usually a service or a late driver, not the storage stack.",

            Core.Registry.BootProgress.BootCompleted =>
                "This boot went all the way through — servicing ran, which happens late. " +
                "If the machine still failed, the failure was on a DIFFERENT boot attempt than this trace.",

            _ => "There is not enough evidence to judge — the boot loader left no timestamp on this disk. " +
                 "Either it never ran, or the disk was written by something else since.",
        };

        return new BootTraceDto(
            LastAttemptUtc: r.LastAttemptUtc,
            Progress: r.Progress.ToString(),
            Verdict: verdict,
            Files: r.Files.Select(ToDto).ToList(),
            NtbtlogTail: r.NtbtlogTailLines,
            NtbtlogNotLoaded: r.NtbtlogNotLoaded);
    }

    public BootTraceFileDto ToDto(Core.Registry.BootTraceFile f) =>
        new(f.Name, f.Exists, f.LastWriteUtc, f.SizeBytes, f.Stage.ToString(), f.Meaning);

    /// <summary>
    /// 파티션이 무엇인지 사람이 이해할 수 있게 분류합니다. Claude가 GUID를 해석하지 않아도 되게.
    /// </summary>
    private static string DescribeKind(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EfiSystem";

        if (p.GptPartitionType is { } g)
        {
            string s = g.ToString();
            if (s.Equals("c12a7328-f81f-11d2-ba4b-00a0c93ec93b", StringComparison.OrdinalIgnoreCase)) return "EfiSystem";
            if (s.Equals("e3c9e316-0b5c-4db8-817d-f92df00215ae", StringComparison.OrdinalIgnoreCase)) return "MicrosoftReserved";
            if (s.Equals("de94bba4-06d1-4d40-a16a-bfd50179d6ac", StringComparison.OrdinalIgnoreCase)) return "WindowsRecovery";
            if (s.Equals("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", StringComparison.OrdinalIgnoreCase)) return "BasicData";
            return "Other";
        }

        return p.MbrPartitionType switch
        {
            0x07 => "BasicData",     // NTFS/exFAT
            0x0B or 0x0C => "Fat32",
            0x27 => "WindowsRecovery",
            0xEE => "GptProtective",
            null => "Unknown",
            _ => "Other",
        };
    }

    /// <summary>
    /// 민감 문자열을 가립니다 — 앞 2글자만 남기고 나머지는 별표.
    /// 완전히 지우지 않는 이유는, 사용자가 "그 디스크 맞나?"를 대조할 수 있어야 하기 때문입니다.
    /// </summary>
    private string? Mask(string? value)
    {
        if (IncludeSensitive || string.IsNullOrEmpty(value)) return value;
        return value.Length <= 2 ? new string('*', value.Length) : value[..2] + new string('*', value.Length - 2);
    }
}
