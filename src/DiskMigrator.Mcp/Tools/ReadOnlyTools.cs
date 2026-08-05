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
            if (!diskService.IsElevated)
            {
                return ToolResult<DiskDetailDto>.Fail(
                    ToolErrorCodes.NotElevated,
                    "Reading disk layout requires administrator rights.",
                    "Restart DiskMigrator as administrator.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var disk = disks.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
            if (disk is null)
            {
                return ToolResult<DiskDetailDto>.Fail(
                    ToolErrorCodes.DiskNotFound,
                    $"No disk with device number {deviceNumber}.",
                    "Call list_disks again — the disk may have been disconnected.");
            }

            _logger.LogInformation("MCP inspect_disk({Number}) → 파티션 {Count}개",
                deviceNumber, disk.Partitions.Count);
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
