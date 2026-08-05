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
        "List all physical disks on this PC with their size, bus type, partition style, and " +
        "whether each is the system/boot/pagefile disk. Always call this first — every other " +
        "disk tool takes a deviceNumber from here. Drive letters change; device numbers are stable " +
        "within a session.")]
    public async Task<ToolResult<IReadOnlyList<DiskDto>>> ListDisksAsync(CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<IReadOnlyList<DiskDto>>.Fail(
                    ToolErrorCodes.NotElevated,
                    "Disk enumeration requires administrator rights.",
                    "Restart DiskMigrator as administrator.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var dtos = disks.Select(mapping.ToDto).ToList();

            _logger.LogInformation("MCP list_disks → 디스크 {Count}개", dtos.Count);
            return ToolResult<IReadOnlyList<DiskDto>>.Success(dtos);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP list_disks 실패.");
            return ToolResult<IReadOnlyList<DiskDto>>.Fail(ToolErrorCodes.Internal, ex.Message);
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

    [McpServerTool(Name = "inspect_disk")]
    [Description(
        "Inspect one disk in detail: its partition layout (offset, size, file system, label, " +
        "drive letter, used space) plus the GPT disk GUID or MBR signature. Use this to understand " +
        "what is on a disk before planning anything, or to see whether a target disk is empty.")]
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
