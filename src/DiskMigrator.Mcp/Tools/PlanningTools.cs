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
    IClonePlanner clonePlanner,
    Mapping mapping,
    // 이미지를 읽기 전용으로 부착해 그 안에서 부팅 진단을 돌리는 데 씁니다.
    // 완성된 객체로 받습니다 — 안의 IDiskService에 도구가 손대지 못하게(ReadOnlyTools와 같은 이유).
    Windows.Jobs.ImageInspector imageInspector,
    // 화면이 지금 어떤 설정으로 서 있는지 보기 위한 것 — 읽기만 합니다.
    // 안전 판정이 화면과 갈리지 않으려면 같은 입력으로 재야 합니다.
    IAppState appState,
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
        "target model name in the app — you cannot do that for them, and should say so plainly. " +
        "'notes' holds facts the app's own screen shows the user (leftover space on a larger target, " +
        "a disk attached over USB); mention them so your answer and the screen agree.")]
    public async Task<ToolResult<SafetyDto>> EvaluateSafetyAsync(
        [Description("Physical disk number to copy FROM. It is only read.")] int sourceDeviceNumber,
        [Description("Physical disk number to copy TO. Everything on it will be erased.")] int targetDeviceNumber,
        [Description(
            "Leave this out. It defaults to the setting the app is actually showing, so your verdict " +
            "matches the app's. Only pass it to answer a what-if question the user asked.")]
        bool? useSnapshot = null,
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

            // 인자를 안 주면 <b>화면이 지금 보여 주는 설정</b>을 씁니다.
            //
            // ⚠ 예전에는 늘 true로 가정했습니다. 사용자가 화면에서 VSS를 껐다면 Claude는
            //   "안전하다"는데 화면은 경고를 띄우게 됩니다 — 안전 판정에서 두 답이 갈리는 것은
            //   그냥 틀린 것보다 나쁩니다. 사용자가 어느 쪽을 믿어야 할지 모르게 되니까요.
            bool snapshot = useSnapshot ?? appState.UseSnapshot;

            var report = Core.Safety.SafetyGuard.Evaluate(
                source, target, diskService.IsElevated, snapshot, hibernated);

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

    [McpServerTool(Name = "plan_clone")]
    [Description(
        "Work out what a clone would actually do: which regions get copied where, how much data, " +
        "whether the source layout even fits on the target, and roughly how long it would take. " +
        "Nothing is written and no snapshot is taken. " +
        "ALWAYS pass on the 'caveats' field — the plan is computed without a snapshot, so the " +
        "reduction from smart-clone is not included and the real copy is usually smaller and faster. " +
        "Presenting the number as a firm prediction misleads people. " +
        "If targetFitsSource is false the source layout is too large and shrinking would be needed; " +
        "say that plainly rather than implying the clone will just work. " +
        "Run evaluate_safety as well — a plan that fits can still be blocked.")]
    public async Task<ToolResult<ClonePlanDto>> PlanCloneAsync(
        [Description("Physical disk number to copy FROM. It is only read.")] int sourceDeviceNumber,
        [Description("Physical disk number to copy TO. Nothing is written during planning.")] int targetDeviceNumber,
        CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<ClonePlanDto>.Fail(
                    ToolErrorCodes.NotElevated, "Reading disk information requires administrator rights.",
                    "Restart DiskMigrator-X as administrator.");
            }

            if (sourceDeviceNumber == targetDeviceNumber)
            {
                return ToolResult<ClonePlanDto>.Fail(
                    ToolErrorCodes.InvalidArgument, "Source and target are the same disk number.",
                    "Pick two different disks.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceDeviceNumber);
            var target = disks.FirstOrDefault(d => d.DeviceNumber == targetDeviceNumber);

            if (source is null || target is null)
            {
                return ToolResult<ClonePlanDto>.Fail(
                    ToolErrorCodes.DiskNotFound,
                    $"Disk {(source is null ? sourceDeviceNumber : targetDeviceNumber)} was not found.",
                    "Call list_disks again — a disk may have been disconnected.");
            }

            // useSnapshot: false — 계획을 세우는 데 스냅샷은 필요 없고, 만들었다 지우는 부작용만 남습니다.
            // 구간 배치는 스냅샷 유무와 무관하게 같습니다.
            using var preview = await clonePlanner.PreviewAsync(source, useSnapshot: false, ct);

            _logger.LogInformation("MCP plan_clone({Src}→{Tgt}) → 구간 {Count}개 {Bytes:N0}바이트",
                sourceDeviceNumber, targetDeviceNumber, preview.Regions.Count, preview.TotalBytes);

            return ToolResult<ClonePlanDto>.Success(mapping.ToDto(preview, source, target));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP plan_clone 실패.");
            return ToolResult<ClonePlanDto>.Fail(ToolErrorCodes.Internal, ex.Message);
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

            var dto = await Task.Run(() => AnalyzeBoot(disk), ct);

            _logger.LogInformation("MCP explain_boot_failure({Number}) → {Verdict}", deviceNumber, dto.Verdict);
            return ToolResult<BootFailureDto>.Success(dto);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP explain_boot_failure 실패.");
            return ToolResult<BootFailureDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "explain_image_boot")]
    [Description(
        "Answer the question people actually ask about a backup: WOULD THIS IMAGE BOOT IF I RESTORED IT? " +
        "inspect_image only tells you the file is structurally sound and its filesystems are clean — that " +
        "is not the same as bootable. This attaches the image READ-ONLY and runs every boot diagnostic " +
        "inside it: boot files, BCD, boot-start drivers and their files, leftover resume image, boot " +
        "manager signature, boot traces. Nothing is written and no disk is touched. " +
        "Use this BEFORE proposing a restore — restoring first and finding out afterwards is exactly the " +
        "risk the user wants to avoid, because the target is already erased by then. " +
        "Run inspect_image as well: a sound-but-unbootable image and a corrupt image need different advice.")]
    public async Task<ToolResult<BootFailureDto>> ExplainImageBootAsync(
        [Description(@"Full path to the .vhdx backup image, e.g. E:\backup.vhdx")] string path,
        CancellationToken ct = default)
    {
        try
        {
            if (!diskService.IsElevated)
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.NotElevated, "Attaching an image requires administrator rights.",
                    "Restart DiskMigrator-X as administrator.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.InvalidArgument, "An image path is required.",
                    @"Give a full path ending in .vhdx, e.g. E:\backup.vhdx");
            }

            string? failure = null;
            var dto = await imageInspector.WithAttachedDiskAsync(
                path, AnalyzeBoot, f => failure = f, ct);

            if (dto is null)
            {
                return ToolResult<BootFailureDto>.Fail(
                    ToolErrorCodes.FileNotFound,
                    failure ?? "The image could not be examined.",
                    "Check the path, and run inspect_image to see whether the file itself is damaged.");
            }

            _logger.LogInformation("MCP explain_image_boot({Path}) → {Verdict}", path, dto.Verdict);
            return ToolResult<BootFailureDto>.Success(dto);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP explain_image_boot 실패.");
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
    /// 디스크 하나에 부팅 진단 전부를 돌리고 원인을 추립니다.
    /// </summary>
    /// <remarks>
    /// 물리 디스크와 <b>부착된 이미지</b>가 이 함수를 공유합니다 — 진단들은 <see cref="DiskInfo"/>만
    /// 보므로 둘을 구분할 이유가 없습니다. 갈라 두면 한쪽만 고치게 됩니다.
    ///
    /// <para>진단 하나가 실패해도 나머지로 판단합니다 — 부분적인 근거라도 없는 것보다 낫고,
    /// 무엇을 못 봤는지도 결과에 드러납니다.</para>
    /// </remarks>
    private BootFailureDto AnalyzeBoot(Core.Models.DiskInfo disk)
    {
        var input = Core.Registry.BootReadinessCheck.ResolveInput(disk);

        var boot = Try(() => Core.Registry.BootReadinessCheck.Inspect(input));
        var drivers = input.WindowsRoot is { } w1 ? Try(() => Core.Registry.BootDriverInventory.Inspect(w1)) : null;
        var fast = input.WindowsRoot is { } w2 ? Try(() => Core.Registry.FastStartupState.Inspect(w2)) : null;
        var trace = input.WindowsRoot is { } w3 ? Try(() => Core.Registry.BootTraceAnalysis.Inspect(w3)) : null;
        var esp = input.SystemRoot is { } s1 ? Try(() => Core.Registry.EspAudit.Inspect(s1)) : null;

        var analysis = Core.Registry.BootFailureAnalysis.Analyze(
            boot, drivers, fast, trace, esp, disk.IsOffline);

        return new BootFailureDto(
            Verdict: analysis.Verdict,
            Causes: analysis.Causes
                .Select(c => new BootFailureCauseDto(c.Code, c.Confidence, c.Finding, c.Action)).ToList(),
            UserChecks: analysis.UserChecks,
            Diagnostics: new BootFailureEvidenceDto(
                boot is null ? null : mapping.ToDto(boot),
                drivers is null ? null : mapping.ToDto(drivers),
                fast is null ? null : mapping.ToDto(fast),
                trace is null ? null : mapping.ToDto(trace),
                esp is null ? null : mapping.ToDto(esp)));
    }

    /// <summary>
    /// 원본에 최대 절전 이미지가 있는지. 읽지 못하면 <b>없다고 단정하지 않고</b> false를 돌려주되,
    /// 그 경우 엔진의 다른 검사가 잡습니다.
    /// </summary>
    /// <remarks>
    /// 판정은 <see cref="Core.Registry.HibernationImage"/>에 있습니다 — 화면(<c>MainViewModel</c>)과
    /// 이 도구가 <b>반드시 같은 답</b>을 내야 하기 때문입니다. 안전 판정이 갈리면 사용자는
    /// Claude와 화면 중 어느 쪽을 믿어야 할지 모르게 됩니다.
    ///
    /// <para>예전에는 여기서 <see cref="Core.Registry.FastStartupState"/>를 거쳐 보고 실패하면
    /// 파일 존재로 되돌아갔습니다. 하이브까지 읽을 이유가 없었습니다 — 안전 판정이 묻는 것은
    /// "재개 이미지가 지금 있는가" 하나뿐입니다.</para>
    /// </remarks>
    private static bool IsHibernated(Core.Models.DiskInfo disk) =>
        Core.Registry.HibernationImage.IsPresent(disk);
}
