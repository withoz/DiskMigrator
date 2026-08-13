using System.ComponentModel;
using System.Runtime.Versioning;
using DiskMigrator.Mcp.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace DiskMigrator.Mcp.Tools;

/// <summary>
/// 1단계 진단 도구 — <b>읽기 전용</b>입니다.
/// </summary>
/// <remarks>
/// 계획서 §4의 첫 번째 원칙("읽기와 쓰기를 계층에서 분리한다")을 <b>타입으로</b> 보장합니다.
/// 이 클래스는 <see cref="IDiskReader"/>만 받습니다 — 클론·백업·복원은 물론 안전 제거 같은
/// 부작용 있는 메서드조차 손에 닿지 않습니다. 새 진단 도구를 여기 추가할 때 이 규칙을 깨지 마십시오.
/// 쓰기가 필요하면 이 클래스가 아니라 제안 도구(3단계)로 가야 합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ReadOnlyTools(
    IDiskReader diskService,
    Mapping mapping,
    Windows.Jobs.ImageInspector imageInspector,
    Windows.Jobs.DiagnosticCollector diagnosticCollector,
    ILogger<ReadOnlyTools>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>
    /// 하이브를 파일로 읽는 진단의 공통 준비 — 디스크를 찾고, Windows 볼륨 루트를 구하고,
    /// 지금 돌고 있는 시스템인지 걸러냅니다.
    /// </summary>
    /// <remarks>
    /// 실행 중인 Windows의 SYSTEM 하이브는 커널이 배타적으로 잠가 관리자 권한으로도 열리지
    /// 않습니다. 예외를 그대로 흘려보내면 Claude에게는 "알 수 없는 내부 오류"로 보여서,
    /// 멀쩡한 디스크를 의심하거나 같은 호출을 반복하게 됩니다. 여기서 미리 걸러 이유를 말합니다.
    /// </remarks>
    private async Task<(string? WindowsRoot, ToolResult<T>? Error)> ResolveHiveRootAsync<T>(
        int deviceNumber, CancellationToken ct)
    {
        var (disk, error) = await ResolveDiskAsync<T>(deviceNumber, ct);
        if (error is not null) return (null, error);

        if (disk!.IsSystemDisk || disk.IsBootDisk)
        {
            return (null, ToolResult<T>.Fail(
                ToolErrorCodes.LiveSystemDisk,
                $"Disk {deviceNumber} is the Windows installation this app is running from. " +
                "Its registry hive is locked by the kernel and cannot be read as a file.",
                "This diagnostic is meant for another disk — a clone, a restored copy, or a disk " +
                "that fails to boot. Connect that disk and use its device number."));
        }

        string? windowsRoot = Core.Registry.BootReadinessCheck.ResolveInput(disk).WindowsRoot;
        if (windowsRoot is null)
        {
            return (null, ToolResult<T>.Fail(
                ToolErrorCodes.InvalidArgument,
                "No Windows installation was found on this disk, or its volume is not accessible.",
                "Check that the disk is online and its Windows partition is mounted."));
        }

        return (windowsRoot, null);
    }

    [McpServerTool(Name = "save_diagnostic")]
    [Description(
        "Collect EVERY boot diagnostic for a disk into a single file. This is how you help a PC that " +
        "will not boot at all: on that machine there is no Claude, so the user runs this from the " +
        "recovery USB, copies the file to a working PC, and you read it there. Collection is read-only — " +
        "nothing is written to the diagnosed disk. Also use it BEFORE and AFTER a repair so you can " +
        "prove with diff_diagnostics whether anything actually changed.")]
    public async Task<ToolResult<DiagnosticSavedDto>> SaveDiagnosticAsync(
        [Description("Physical disk number to diagnose.")] int deviceNumber,
        [Description("Where to write the report, e.g. E:\\before.dmdiag")] string path,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return ToolResult<DiagnosticSavedDto>.Fail(ToolErrorCodes.InvalidArgument, "No output path was given.");

            var (disk, error) = await ResolveDiskAsync<DiagnosticSavedDto>(deviceNumber, ct);
            if (error is not null) return error;

            var report = await diagnosticCollector.CollectAsync(deviceNumber, mapping.IncludeSensitive, ct);
            await diagnosticCollector.SaveAsync(report, path, ct);

            var info = new FileInfo(path);
            _logger.LogInformation("MCP save_diagnostic({Number}) → {Path} ({Size:N0} bytes)",
                deviceNumber, path, info.Length);

            return ToolResult<DiagnosticSavedDto>.Success(
                new DiagnosticSavedDto(path, info.Length, report.CollectedUtc, report.Summary));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP save_diagnostic 실패.");
            return ToolResult<DiagnosticSavedDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "load_diagnostic")]
    [Description(
        "Read a diagnostic report saved by save_diagnostic — typically collected on a PC that cannot " +
        "boot and carried over on a USB stick. Gives you the whole picture at once: disk layout, boot " +
        "checks, boot-start drivers, hibernation state, ESP contents with the boot manager's signing " +
        "authority, and how far the last boot attempt got.")]
    public async Task<ToolResult<Windows.Jobs.DiagnosticReport>> LoadDiagnosticAsync(
        [Description("Path to the .dmdiag report file.")] string path,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ToolResult<Windows.Jobs.DiagnosticReport>.Fail(
                    ToolErrorCodes.FileNotFound, $"No file at {path}.",
                    "Check the path — reports made by this app end in .dmdiag.");
            }

            var report = await diagnosticCollector.LoadAsync(path, ct);
            _logger.LogInformation("MCP load_diagnostic({Path}) → 수집 {When:u}", path, report.CollectedUtc);
            return ToolResult<Windows.Jobs.DiagnosticReport>.Success(report);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP load_diagnostic 실패.");
            return ToolResult<Windows.Jobs.DiagnosticReport>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "diff_diagnostics")]
    [Description(
        "Compare two diagnostic reports to see what a repair actually changed. Use this after any " +
        "boot repair: if the disk still fails, first find out whether the repair reached the disk at all. " +
        "A result of NO CHANGES is itself a strong finding — it means the cause lies outside the disk " +
        "(firmware, hardware, or the target PC), not in its configuration.")]
    public async Task<ToolResult<Windows.Jobs.DiagnosticDiffResult>> DiffDiagnosticsAsync(
        [Description("Path to the earlier report (before the change).")] string beforePath,
        [Description("Path to the later report (after the change).")] string afterPath,
        CancellationToken ct = default)
    {
        try
        {
            foreach (string p in new[] { beforePath, afterPath })
            {
                if (!File.Exists(p))
                {
                    return ToolResult<Windows.Jobs.DiagnosticDiffResult>.Fail(
                        ToolErrorCodes.FileNotFound, $"No file at {p}.");
                }
            }

            var before = await diagnosticCollector.LoadAsync(beforePath, ct);
            var after = await diagnosticCollector.LoadAsync(afterPath, ct);
            var diff = Windows.Jobs.DiagnosticDiff.Compare(before, after);

            _logger.LogInformation("MCP diff_diagnostics → 차이 {Count}건 (같은 디스크={Same})",
                diff.Changes.Count, diff.SameDisk);
            return ToolResult<Windows.Jobs.DiagnosticDiffResult>.Success(diff);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP diff_diagnostics 실패.");
            return ToolResult<Windows.Jobs.DiagnosticDiffResult>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "check_hardware_compatibility")]
    [Description(
        "Judge whether THIS PC can boot from a given disk, based on its firmware generation, UEFI vs " +
        "legacy mode, and how the disk is attached. Returns a verdict (Supported / Uncertain / " +
        "Unsupported) together with a CONFIDENCE level, the reasoning, and what the user can do — " +
        "including when the honest answer is 'this board cannot do it, use a SATA SSD or replace the " +
        "board'. Never state such a conclusion without the reasoning this returns. " +
        "When the verdict is Uncertain the result lists checks only the user can perform (does the " +
        "boot menu show the disk, does a FULL power-off/power-on work) — ask them, do not guess. " +
        "IMPORTANT: this describes the PC the app is running on, not a different target PC.")]
    public async Task<ToolResult<CompatibilityDto>> CheckHardwareCompatibilityAsync(
        [Description("Physical disk number of the disk you intend to boot from. Omit to judge by firmware alone.")]
        int? deviceNumber = null,
        CancellationToken ct = default)
    {
        try
        {
            string busType = "Sata";
            bool isMbr = false;

            if (deviceNumber is { } n)
            {
                var (disk, error) = await ResolveDiskAsync<CompatibilityDto>(n, ct);
                if (error is not null) return error;
                busType = disk!.BusType.ToString();
                isMbr = disk.PartitionStyle == Core.Models.PartitionStyle.Mbr;
            }

            var fw = await Task.Run(Windows.Devices.FirmwareInfo.Read, ct);
            var verdict = Core.Registry.BootCompatibility.Evaluate(
                fw.IsUefi, fw.BiosReleaseDate, busType, isMbr);

            _logger.LogInformation("MCP check_hardware_compatibility({Number}) → {Verdict}/{Conf} (버스 {Bus})",
                deviceNumber, verdict.Verdict, verdict.Confidence, busType);

            return ToolResult<CompatibilityDto>.Success(mapping.ToDto(verdict, fw, busType));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP check_hardware_compatibility 실패.");
            return ToolResult<CompatibilityDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "inspect_image")]
    [Description(
        "Check whether a backup image (.vhdx) is intact BEFORE restoring it. Attaches the image " +
        "read-only and verifies its structure, partition table, and the file system of each volume. " +
        "Use this whenever someone is about to restore an old or unverified backup — a damaged image " +
        "restored onto a disk produces a broken result, and by then the target is already overwritten. " +
        "The image itself is never modified.")]
    public async Task<ToolResult<ImageInspectionDto>> InspectImageAsync(
        [Description("Full path to the .vhdx backup image.")] string path,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ToolResult<ImageInspectionDto>.Fail(
                    ToolErrorCodes.InvalidArgument, "No image path was given.");
            }

            if (!File.Exists(path))
            {
                return ToolResult<ImageInspectionDto>.Fail(
                    ToolErrorCodes.FileNotFound,
                    $"No file at {path}.",
                    "Check the path. Backup images made by this app end in .vhdx.");
            }

            if (!diskService.IsElevated)
            {
                return ToolResult<ImageInspectionDto>.Fail(
                    ToolErrorCodes.NotElevated,
                    "Attaching an image requires administrator rights.",
                    "Restart DiskMigrator-X as administrator.");
            }

            var report = await imageInspector.InspectAsync(path, ct);

            _logger.LogInformation("MCP inspect_image({Path}) → ok={Ok} 항목 {Count}개",
                path, report.Ok, report.Items.Count);
            return ToolResult<ImageInspectionDto>.Success(mapping.ToDto(report));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP inspect_image 실패.");
            return ToolResult<ImageInspectionDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "audit_esp")]
    [Description(
        "Audit the EFI System Partition: which boot files are present (boot manager, the fallback " +
        "EFI\\Boot\\bootx64.efi path, the BCD store), and — importantly — WHICH CERTIFICATE signed " +
        "the boot manager. A boot manager signed by the 2023 UEFI CA can fail Secure Boot on boards " +
        "made before 2023, and some firmware then hangs with no error at all. Also reports non-Microsoft " +
        "boot folders left on the ESP by other tools. Use this when a disk has correct-looking boot " +
        "configuration but still will not start on older hardware.")]
    public async Task<ToolResult<EspAuditDto>> AuditEspAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (disk, error) = await ResolveDiskAsync<EspAuditDto>(deviceNumber, ct);
            if (error is not null) return error;

            // ESP는 보통 드라이브 문자가 없습니다. 엔진이 볼륨 GUID 경로로 해석해 주므로
            // 임시 마운트 없이 그대로 읽을 수 있습니다.
            string? espRoot = Core.Registry.BootReadinessCheck.ResolveInput(disk!).SystemRoot;
            if (espRoot is null)
            {
                return ToolResult<EspAuditDto>.Fail(
                    ToolErrorCodes.InvalidArgument,
                    "The boot partition on this disk could not be reached (no ESP, or the volume is not accessible).",
                    "Check that the disk is online. On a BIOS/MBR disk there is no ESP — use check_boot_readiness instead.");
            }

            var result = await Task.Run(() => Core.Registry.EspAudit.Inspect(espRoot), ct);

            _logger.LogInformation("MCP audit_esp({Number}) → mgr={Mgr} bcd={Bcd} 서명={Auth}",
                deviceNumber, result.BootManagerPresent, result.BcdPresent,
                result.Signature?.Authority ?? "(없음)");
            return ToolResult<EspAuditDto>.Success(mapping.ToDto(result));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP audit_esp 실패.");
            return ToolResult<EspAuditDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// 권한을 확인하고 디스크를 찾습니다. 실패하면 그대로 돌려줄 수 있는 오류를 함께 냅니다.
    /// </summary>
    /// <remarks>
    /// 디스크를 다루는 도구마다 같은 두 검사를 반복하게 되므로 한곳에 모읍니다.
    /// 오류 문구도 통일되어, Claude가 상황을 일관되게 이해합니다.
    /// </remarks>
    private async Task<(Core.Models.DiskInfo? Disk, ToolResult<T>? Error)> ResolveDiskAsync<T>(
        int deviceNumber, CancellationToken ct)
    {
        if (!diskService.IsElevated)
        {
            return (null, ToolResult<T>.Fail(
                ToolErrorCodes.NotElevated,
                "Reading disk information requires administrator rights.",
                "Restart DiskMigrator as administrator."));
        }

        var disks = await diskService.EnumerateDisksAsync(ct);
        var disk = disks.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
        if (disk is null)
        {
            return (null, ToolResult<T>.Fail(
                ToolErrorCodes.DiskNotFound,
                $"No disk with device number {deviceNumber}.",
                "Call list_disks again — the disk may have been disconnected."));
        }

        return (disk, null);
    }

    [McpServerTool(Name = "list_disks")]
    [Description(
        "List every physical disk on this PC **with its full partition layout** — size, bus type, " +
        "partition style, whether it is the system/boot/pagefile disk, and each partition's offset, " +
        "size, file system, label, drive letter and used space. Always call this first. " +
        "You do NOT need inspect_disk afterwards: everything it returns is already here. " +
        "Drive letters change; device numbers are stable within a session.")]
    public async Task<ToolResult<IReadOnlyList<DiskDetailDto>>> ListDisksAsync(CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<IReadOnlyList<DiskDetailDto>>.Fail(
                    ToolErrorCodes.NotElevated,
                    "Disk enumeration requires administrator rights.",
                    "Restart DiskMigrator as administrator.");
            }

            // 파티션까지 함께 돌려줍니다.
            //
            // ⚠ 예전에는 요약만 주고, 파티션을 보려면 디스크마다 inspect_disk를 다시 부르게
            //   했습니다. 그런데 inspect_disk도 <b>같은 EnumerateDisksAsync를 다시 부를 뿐</b>이라
            //   새로 읽는 것이 없었습니다 — 정보는 이미 첫 호출에 다 들어 있었습니다.
            //
            //   대신 도구 호출이 디스크 수만큼 늘었고, 호출 한 번은 모델을 한 번 오가는 일입니다.
            //   2026-08-13 실기: 디스크 8개인 PC에서 왕복 12번 · 62초가 걸렸고 그중 8번이
            //   inspect_disk였습니다. 한 번에 주면 그 8번이 사라집니다.
            //
            //   응답이 길어지는 대신 왕복이 줄어듭니다. 왕복 한 번이 3~6초이므로 훨씬 남는 장사입니다.
            var disks = await diskService.EnumerateDisksAsync(ct);
            var dtos = disks.Select(mapping.ToDetailDto).ToList();

            _logger.LogInformation("MCP list_disks → 디스크 {Count}개(파티션 포함)", dtos.Count);
            return ToolResult<IReadOnlyList<DiskDetailDto>>.Success(dtos);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP list_disks 실패.");
            return ToolResult<IReadOnlyList<DiskDetailDto>>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "check_boot_readiness")]
    [Description(
        "Check whether a disk is configured to boot — without actually booting it. Verifies the " +
        "boot loader files, the BCD store and its device references, winload, and whether the " +
        "standard storage drivers are set to boot-start. Use this after a clone or restore, or " +
        "when a disk fails to boot. Judge by the 'code' field, not the localized text: " +
        "DEVICE_REF means the BCD points at a different disk; HIBERNATION means a leftover " +
        "hibernation image will hang on other hardware. A null 'passed' means the check could not " +
        "run (usually the volume is not mounted) — that is not the same as a failure.")]
    public async Task<ToolResult<BootCheckDto>> CheckBootReadinessAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (disk, error) = await ResolveDiskAsync<BootCheckDto>(deviceNumber, ct);
            if (error is not null) return error;

            // 파일·레지스트리 I/O가 있으므로 요청 스레드를 오래 잡지 않게 옮깁니다.
            var report = await Task.Run(() => Core.Registry.BootReadinessCheck.InspectDisk(disk!), ct);

            _logger.LogInformation("MCP check_boot_readiness({Number}) → 부팅가능={Would} 경고={Warn}",
                deviceNumber, report.WouldBoot, report.HasWarnings);
            return ToolResult<BootCheckDto>.Success(mapping.ToDto(report));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP check_boot_readiness 실패.");
            return ToolResult<BootCheckDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "read_boot_drivers")]
    [Description(
        "List the boot-start (Start=0) drivers on a disk and verify that each driver FILE actually " +
        "exists. A driver can be registered correctly while its file is missing — the loader then " +
        "stalls before the kernel starts, with no error screen. Registry-only checks miss this. " +
        "Returns counts plus the problem entries (missing files, drivers outside System32); " +
        "healthy drivers are summarised as a count rather than listed. " +
        "NOTE: this reads the registry hive as a file, so it cannot target the disk this app is " +
        "running from — use it on a clone, a restored copy, or a disk that fails to boot.")]
    public async Task<ToolResult<BootDriverInventoryDto>> ReadBootDriversAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (windowsRoot, error) = await ResolveHiveRootAsync<BootDriverInventoryDto>(deviceNumber, ct);
            if (error is not null) return error;

            var result = await Task.Run(() => Core.Registry.BootDriverInventory.Inspect(windowsRoot!), ct);

            _logger.LogInformation("MCP read_boot_drivers({Number}) → 전체 {Total} / 누락 {Missing} / 외부 {Outside}",
                deviceNumber, result.Drivers.Count, result.MissingFiles.Count, result.OutsideSystem32.Count);
            return ToolResult<BootDriverInventoryDto>.Success(mapping.ToDto(result));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP read_boot_drivers 실패.");
            return ToolResult<BootDriverInventoryDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "read_fast_startup")]
    [Description(
        "Check whether a disk will try to RESUME from hibernation instead of booting normally. " +
        "Windows 10/11 saves kernel state on shutdown when Fast Startup is on; that saved state " +
        "assumes the original hardware, so on a different PC the resume fails and hangs at the " +
        "manufacturer logo. Boot diagnostics stay silent in that case because they only apply to " +
        "the normal boot path. Check this early when a cloned or restored disk hangs with no error. " +
        "NOTE: reads the registry hive as a file, so it cannot target the disk this app runs from.")]
    public async Task<ToolResult<FastStartupDto>> ReadFastStartupAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (windowsRoot, error) = await ResolveHiveRootAsync<FastStartupDto>(deviceNumber, ct);
            if (error is not null) return error;

            var result = await Task.Run(() => Core.Registry.FastStartupState.Inspect(windowsRoot!), ct);

            _logger.LogInformation("MCP read_fast_startup({Number}) → 재개시도={Resume} hiberboot={Hb}",
                deviceNumber, result.ResumeWouldBeAttempted, result.HiberbootEnabled);
            return ToolResult<FastStartupDto>.Success(mapping.ToDto(result));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP read_fast_startup 실패.");
            return ToolResult<FastStartupDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "analyze_boot_trace")]
    [Description(
        "Work out HOW FAR the last boot attempt got, from the traces left on the disk. When a boot " +
        "fails there is nothing on screen, but the boot loader and the kernel touch DIFFERENT files — " +
        "comparing which ones were updated and which are untouched shows where it stopped. " +
        "This is the strongest tool for a disk that hangs with no error message: it separates " +
        "'the kernel never started' from 'the kernel started and stalled later', which need completely " +
        "different fixes. Also returns the tail of ntbtlog.txt when boot logging was enabled. " +
        "NOTE: reads files from the disk, so it cannot target the disk this app runs from.")]
    public async Task<ToolResult<BootTraceDto>> AnalyzeBootTraceAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (windowsRoot, error) = await ResolveHiveRootAsync<BootTraceDto>(deviceNumber, ct);
            if (error is not null) return error;

            var result = await Task.Run(() => Core.Registry.BootTraceAnalysis.Inspect(windowsRoot!), ct);

            _logger.LogInformation("MCP analyze_boot_trace({Number}) → {Progress} (시도 {When})",
                deviceNumber, result.Progress, result.LastAttemptUtc?.ToString("u") ?? "?");
            return ToolResult<BootTraceDto>.Success(mapping.ToDto(result));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP analyze_boot_trace 실패.");
            return ToolResult<BootTraceDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "inspect_disk")]
    [Description(
        "Re-read ONE disk's partition layout. Rarely needed: list_disks already returns this for " +
        "every disk. Use it only to refresh a single disk after something changed — for example " +
        "after a clone finished, or after the user plugged a disk in.")]
    public async Task<ToolResult<DiskDetailDto>> InspectDiskAsync(
        [Description("Physical disk number, as returned by list_disks.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            var (disk, error) = await ResolveDiskAsync<DiskDetailDto>(deviceNumber, ct);
            if (error is not null) return error;

            _logger.LogInformation("MCP inspect_disk({Number}) → 파티션 {Count}개",
                deviceNumber, disk!.Partitions.Count);
            return ToolResult<DiskDetailDto>.Success(mapping.ToDetailDto(disk));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP inspect_disk 실패.");
            return ToolResult<DiskDetailDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }
}
