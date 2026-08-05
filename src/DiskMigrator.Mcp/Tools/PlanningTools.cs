using System.ComponentModel;
using System.Runtime.Versioning;
using DiskMigrator.Mcp.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace DiskMigrator.Mcp.Tools;

/// <summary>
/// 2단계 도구 — <b>계획하고 설명합니다. 쓰지 않습니다.</b>
/// </summary>
/// <remarks>
/// 1단계 진단이 "지금 상태가 어떤가"를 답한다면, 여기서는 "그래서 이 작업이 안전한가,
/// 무엇이 일어나는가"를 답합니다. 여전히 <see cref="IDiskReader"/>만 받으므로 쓰기 경로가 없습니다.
///
/// <para>안전 판정은 <see cref="Core.Safety.SafetyGuard"/>를 그대로 호출합니다 —
/// 계획서 §4의 원칙대로 MCP 계층에서 판정을 재구현하거나 완화하지 않습니다.
/// GUI·CLI와 같은 코드를 쓰므로 같은 입력에는 반드시 같은 답이 나옵니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PlanningTools(
    IDiskReader diskService,
    Mapping mapping,
    ILogger<PlanningTools>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    [McpServerTool(Name = "evaluate_safety")]
    [Description(
        "Judge whether cloning one disk onto another is safe, using the SAME rules the app itself " +
        "enforces — this is not a second opinion, it is the actual gate. Returns blockers (which " +
        "nothing can override), items needing typed confirmation, and warnings, each with a stable " +
        "code you should reason about instead of the wording. " +
        "Call this BEFORE describing any clone to the user, and repeat the blockers verbatim rather " +
        "than paraphrasing them away. If needsTypedConfirmation is true, the user must type the " +
        "target model name in the app — you cannot do that for them, and should say so plainly.")]
    public async Task<ToolResult<SafetyDto>> EvaluateSafetyAsync(
        [Description("Physical disk number to copy FROM. It is only read.")] int sourceDeviceNumber,
        [Description("Physical disk number to copy TO. Everything on it will be erased.")] int targetDeviceNumber,
        [Description("Whether a VSS snapshot would be used (matters when the source is the running system).")]
        bool useSnapshot = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<SafetyDto>.Fail(
                    ToolErrorCodes.NotElevated,
                    "Reading disk information requires administrator rights.",
                    "Restart DiskMigrator-X as administrator.");
            }

            if (sourceDeviceNumber == targetDeviceNumber)
            {
                return ToolResult<SafetyDto>.Fail(
                    ToolErrorCodes.InvalidArgument,
                    "Source and target are the same disk number.",
                    "Pick two different disks — a disk cannot be cloned onto itself.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceDeviceNumber);
            var target = disks.FirstOrDefault(d => d.DeviceNumber == targetDeviceNumber);

            if (source is null || target is null)
            {
                return ToolResult<SafetyDto>.Fail(
                    ToolErrorCodes.DiskNotFound,
                    $"Disk {(source is null ? sourceDeviceNumber : targetDeviceNumber)} was not found.",
                    "Call list_disks again — a disk may have been disconnected.");
            }

            // 원본에 재개 이미지가 있으면 사본이 다른 하드웨어에서 멈춥니다. 엔진이 그 경고를
            // 내려면 이 사실을 알려줘야 합니다 — 1단계에서 만든 진단을 그대로 씁니다.
            bool hibernated = await Task.Run(() => IsHibernated(source), ct);

            var report = Core.Safety.SafetyGuard.Evaluate(
                source, target, diskService.IsElevated, useSnapshot, hibernated);

            _logger.LogInformation("MCP evaluate_safety({Src}→{Tgt}) → 진행가능={Can} 확인필요={Confirm} 차단={Blockers}",
                sourceDeviceNumber, targetDeviceNumber, report.CanProceed, report.NeedsTypedConfirmation,
                report.Blockers.Count());

            return ToolResult<SafetyDto>.Success(mapping.ToDto(report, source, target));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP evaluate_safety 실패.");
            return ToolResult<SafetyDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "explain_boot_failure")]
    [Description(
        "Run every boot diagnostic on a disk and REASON ACROSS them to explain why it will not boot. " +
        "Individual tools only report facts; this one connects them — for example: boot files intact, " +
        "all driver files present, no resume image, yet the kernel left no trace at all, therefore the " +
        "cause lies outside the disk. " +
        "Each candidate carries a confidence level: state 'Certain' findings plainly, but present 'Low' " +
        "ones as possibilities, never as conclusions — a wrong confident answer sends people to replace " +
        "working hardware. The result also lists checks only the user can perform; ask them rather than " +
        "guessing. Evidence from each diagnostic is returned alongside so the reasoning can be verified. " +
        "NOTE: reads the disk's registry and files, so it cannot target the disk this app runs from.")]
    public async Task<ToolResult<BootFailureDto>> ExplainBootFailureAsync(
        [Description("Physical disk number that fails to boot.")] int deviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.NotElevated, "Reading disk information requires administrator rights.",
                    "Restart DiskMigrator-X as administrator.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var disk = disks.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
            if (disk is null)
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.DiskNotFound, $"No disk with device number {deviceNumber}.",
                    "Call list_disks again — the disk may have been disconnected.");
            }

            if (disk.IsSystemDisk || disk.IsBootDisk)
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.LiveSystemDisk,
                    $"Disk {deviceNumber} is the Windows installation this app is running from — and it " +
                    "evidently boots. Its registry hive is also locked and cannot be read as a file.",
                    "Point this at the disk that fails: a clone, a restored copy, or one taken from another PC.");
            }

            var input = Core.Registry.BootReadinessCheck.ResolveInput(disk);

            // 진단을 하나씩 돌립니다. 실패한 것은 null로 두고 나머지로 판단합니다 —
            // 부분적인 근거라도 없는 것보다 낫고, 무엇을 못 봤는지도 결과에 드러납니다.
            var (boot, drivers, fast, trace, esp) = await Task.Run(() =>
            (
                Try(() => Core.Registry.BootReadinessCheck.Inspect(input)),
                input.WindowsRoot is { } w1 ? Try(() => Core.Registry.BootDriverInventory.Inspect(w1)) : null,
                input.WindowsRoot is { } w2 ? Try(() => Core.Registry.FastStartupState.Inspect(w2)) : null,
                input.WindowsRoot is { } w3 ? Try(() => Core.Registry.BootTraceAnalysis.Inspect(w3)) : null,
                input.SystemRoot is { } s1 ? Try(() => Core.Registry.EspAudit.Inspect(s1)) : null
            ), ct);

            var analysis = Core.Registry.BootFailureAnalysis.Analyze(boot, drivers, fast, trace, esp);

            _logger.LogInformation("MCP explain_boot_failure({Number}) → 원인 후보 {Count}개, 최상위 {Top}",
                deviceNumber, analysis.Causes.Count,
                analysis.Causes.Count > 0 ? analysis.Causes[0].Code : "(없음)");

            return ToolResult<BootFailureDto>.Success(new BootFailureDto(
                Verdict: analysis.Verdict,
                Causes: analysis.Causes
                    .Select(c => new BootFailureCauseDto(c.Code, c.Confidence, c.Finding, c.Action)).ToList(),
                UserChecks: analysis.UserChecks,
                Diagnostics: new BootFailureEvidenceDto(
                    boot is null ? null : mapping.ToDto(boot),
                    drivers is null ? null : mapping.ToDto(drivers),
                    fast is null ? null : mapping.ToDto(fast),
                    trace is null ? null : mapping.ToDto(trace),
                    esp is null ? null : mapping.ToDto(esp))));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP explain_boot_failure 실패.");
            return ToolResult<BootFailureDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>진단 하나가 실패해도 나머지 분석을 계속하기 위한 감싸개.</summary>
    private static T? Try<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }

    /// <summary>
    /// 원본에 최대 절전 이미지가 있는지. 읽지 못하면 <b>없다고 단정하지 않고</b> false를 돌려주되,
    /// 그 경우 엔진의 다른 검사가 잡습니다.
    /// </summary>
    private static bool IsHibernated(Core.Models.DiskInfo disk)
    {
        try
        {
            string? windowsRoot = Core.Registry.BootReadinessCheck.ResolveInput(disk).WindowsRoot;
            if (windowsRoot is null) return false;
            return Core.Registry.FastStartupState.Inspect(windowsRoot).HiberfilExists;
        }
        catch
        {
            // 실행 중인 시스템이면 하이브를 못 읽습니다 — 그 경우 파일 존재만 따로 봅니다.
            try
            {
                string? root = Core.Registry.BootReadinessCheck.ResolveInput(disk).WindowsRoot;
                return root is not null && File.Exists(Path.Combine(root, "hiberfil.sys"));
            }
            catch { return false; }
        }
    }
}
