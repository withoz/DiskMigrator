using System.ComponentModel;
using System.Runtime.Versioning;
using DiskMigrator.Mcp.Dto;
using DiskMigrator.Mcp.Proposals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace DiskMigrator.Mcp.Tools;

/// <summary>
/// 3단계 도구 — <b>제안하고 관찰합니다. 실행하지 않습니다.</b>
/// </summary>
/// <remarks>
/// 계획서 §6.3의 게이트를 구현합니다. 이 클래스가 할 수 있는 최대치는 <b>화면에 카드를 띄우는
/// 것</b>이며, 원본·대상·옵션조차 채우지 않습니다. 사용자가 [적용]을 눌러야 앱이 값을 채우고,
/// 그 뒤에도 모델명 입력이라는 기존 관문이 남습니다.
///
/// <para><b>모델명 확인란은 어떤 경로로도 닿을 수 없습니다.</b> 이 클래스는 뷰모델이 아니라
/// <see cref="ProposalStore"/>와 <see cref="IAppState"/>만 받으며, 둘 중 어디에도 그 값을 쓰는
/// 통로가 없습니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProposalTools(
    IDiskReader diskService,
    ProposalStore proposals,
    IAppState appState,
    ILogger<ProposalTools>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    [McpServerTool(Name = "propose_clone")]
    [Description(
        "Put a clone suggestion on the app's screen for the user to accept or ignore. " +
        "This does NOT start anything and does not even fill in the form — it only shows a card. " +
        "The user must press Apply, and after that still type the target model name before the " +
        "operation can run. You cannot do either of those steps; say so plainly rather than implying " +
        "the clone is under way. " +
        "Blocked combinations are refused here rather than shown, so run evaluate_safety first and " +
        "explain any blockers to the user instead of proposing. " +
        "Use get_proposal_status afterwards to see whether the user accepted, ignored, or let it expire — " +
        "do not assume acceptance, and do not re-propose repeatedly if they ignore it.")]
    public async Task<ToolResult<ProposalDto>> ProposeCloneAsync(
        [Description("Physical disk number to copy FROM. It is only read.")] int sourceDeviceNumber,
        [Description("Physical disk number to copy TO. Everything on it would be erased.")] int targetDeviceNumber,
        [Description("Why you are suggesting this. The user reads it on the card — be specific and honest.")]
        string reason,
        [Description("Suggest using a VSS snapshot (needed when the source is the running system).")]
        bool useSnapshot = true,
        [Description("Suggest verifying the copy afterwards. Doubles the time but proves the copy matches.")]
        bool verifyAfterCopy = true,
        CancellationToken ct = default)
    {
        try
        {
            var pre = await PrecheckAsync<ProposalDto>(reason, ct);
            if (pre is not null) return pre;

            if (sourceDeviceNumber == targetDeviceNumber)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.InvalidArgument, "Source and target are the same disk number.",
                    "Pick two different disks.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceDeviceNumber);
            var target = disks.FirstOrDefault(d => d.DeviceNumber == targetDeviceNumber);

            if (source is null || target is null)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.DiskNotFound,
                    $"Disk {(source is null ? sourceDeviceNumber : targetDeviceNumber)} was not found.",
                    "Call list_disks again — a disk may have been disconnected.");
            }

            // 차단된 조합은 카드조차 띄우지 않습니다. "적용해도 실행할 수 없는 카드"는
            // 사용자에게 혼란만 주고, 거절당한 이유를 설명하는 것이 훨씬 유용합니다.
            var safety = Core.Safety.SafetyGuard.Evaluate(source, target, diskService.IsElevated, useSnapshot);
            if (!safety.CanProceed)
            {
                string codes = string.Join(", ", safety.Blockers.Select(b => b.Code));
                _logger.LogInformation("MCP propose_clone 거절(차단): {Codes}", codes);

                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.Blocked,
                    $"This combination is blocked and cannot run: {codes}. No card was shown.",
                    "Call evaluate_safety and explain the blockers to the user instead of proposing.");
            }

            var proposal = proposals.Propose(
                ProposalKind.Clone, source, target, null, reason.Trim(),
                useSnapshot, verifyAfterCopy, safety.NeedsTypedConfirmation);

            _logger.LogInformation("MCP propose_clone → 제안 {Id} 등록 ({Src}→{Tgt}), 타이핑확인={Confirm}",
                proposal.Id, sourceDeviceNumber, targetDeviceNumber, safety.NeedsTypedConfirmation);

            return ToolResult<ProposalDto>.Success(ToDto(proposal,
                "The card is now on screen. Nothing has started — the user must press Apply, and then " +
                (safety.NeedsTypedConfirmation
                    ? "type the target model name before the clone can begin."
                    : "press Start before the clone can begin.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP propose_clone 실패.");
            return ToolResult<ProposalDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "propose_backup")]
    [Description(
        "Suggest backing a disk up to an image file (.vhdx). The disk is only read, so this is the " +
        "least risky of the proposals — but if the path already holds a backup the app will treat it " +
        "as an incremental chain, so say which file you mean and why. " +
        "As with every proposal this only shows a card: the user presses Apply, then Start. " +
        "Nothing begins from this call.")]
    public async Task<ToolResult<ProposalDto>> ProposeBackupAsync(
        [Description("Physical disk number to back up. It is only read.")] int sourceDeviceNumber,
        [Description(@"Where to write the image, e.g. E:\backup.vhdx")] string imagePath,
        [Description("Why you are suggesting this. The user reads it on the card.")] string reason,
        [Description("Suggest using a VSS snapshot (needed when the source is the running system).")]
        bool useSnapshot = true,
        CancellationToken ct = default)
    {
        try
        {
            var pre = await PrecheckAsync<ProposalDto>(reason, ct);
            if (pre is not null) return pre;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.InvalidArgument, "An image path is required.",
                    @"Give a full path ending in .vhdx, e.g. E:\backup.vhdx");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceDeviceNumber);
            if (source is null)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.DiskNotFound, $"Disk {sourceDeviceNumber} was not found.",
                    "Call list_disks again — the disk may have been disconnected.");
            }

            // 백업은 디스크를 읽기만 하므로 SafetyGuard의 대상 파괴 규칙이 적용되지 않습니다.
            // 대신 경로가 이미 있으면 증분 사슬로 이어진다는 사실을 카드에 담습니다.
            bool exists = File.Exists(imagePath);
            var proposal = proposals.Propose(
                ProposalKind.Backup, source, null, imagePath, reason.Trim(),
                useSnapshot, verifyAfterCopy: false, needsTypedConfirmation: false);

            _logger.LogInformation("MCP propose_backup → 제안 {Id} (디스크 {Src} → {Path}, 기존파일={Exists})",
                proposal.Id, sourceDeviceNumber, imagePath, exists);

            return ToolResult<ProposalDto>.Success(ToDto(proposal,
                exists
                    ? "The card is on screen. That file already exists, so the app will add to it as an " +
                      "incremental backup rather than starting fresh — make sure the user expects that."
                    : "The card is on screen. Nothing has started — the user presses Apply, then Start."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP propose_backup 실패.");
            return ToolResult<ProposalDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "propose_restore")]
    [Description(
        "Suggest restoring an image file onto a disk. THIS ERASES THE TARGET DISK — treat it as the " +
        "most dangerous proposal and say so plainly to the user before calling it. " +
        "Check the image first with inspect_image: restoring a damaged image produces a broken disk, " +
        "and by then the target is already overwritten. " +
        "Blocked targets are refused here rather than shown. The user must Apply and then type the " +
        "target model name; you cannot do either.")]
    public async Task<ToolResult<ProposalDto>> ProposeRestoreAsync(
        [Description("Full path to the .vhdx image to restore FROM.")] string imagePath,
        [Description("Physical disk number to restore ONTO. Everything on it would be erased.")]
        int targetDeviceNumber,
        [Description("Why you are suggesting this. The user reads it on the card — be specific.")] string reason,
        CancellationToken ct = default)
    {
        try
        {
            var pre = await PrecheckAsync<ProposalDto>(reason, ct);
            if (pre is not null) return pre;

            if (!File.Exists(imagePath))
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.FileNotFound, $"No image file at {imagePath}.",
                    "Check the path. Images made by this app end in .vhdx.");
            }

            var disks = await diskService.EnumerateDisksAsync(ct);
            var target = disks.FirstOrDefault(d => d.DeviceNumber == targetDeviceNumber);
            if (target is null)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.DiskNotFound, $"Disk {targetDeviceNumber} was not found.",
                    "Call list_disks again — the disk may have been disconnected.");
            }

            // 복원은 대상을 지우므로 클론과 같은 차단 규칙을 적용합니다. 원본이 파일이라
            // SafetyGuard에 넘길 '원본 디스크'가 없으므로, 대상 자체의 금기만 확인합니다.
            if (target.IsSystemDisk || target.IsBootDisk || target.HasPageFile || target.IsReadOnly)
            {
                var reasons = new List<string>();
                if (target.IsSystemDisk) reasons.Add("TARGET_IS_SYSTEM_DISK");
                if (target.IsBootDisk) reasons.Add("TARGET_IS_BOOT_DISK");
                if (target.HasPageFile) reasons.Add("TARGET_HAS_PAGEFILE");
                if (target.IsReadOnly) reasons.Add("TARGET_READ_ONLY");

                _logger.LogInformation("MCP propose_restore 거절(차단): {Codes}", string.Join(", ", reasons));
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.Blocked,
                    $"This target cannot be restored onto: {string.Join(", ", reasons)}. No card was shown.",
                    "Explain to the user why this disk is off limits instead of proposing it.");
            }

            bool needsConfirmation = target.HasExistingData;
            var proposal = proposals.Propose(
                ProposalKind.Restore, null, target, imagePath, reason.Trim(),
                useSnapshot: false, verifyAfterCopy: true, needsTypedConfirmation: needsConfirmation);

            _logger.LogInformation("MCP propose_restore → 제안 {Id} ({Path} → 디스크 {Tgt}), 타이핑확인={Confirm}",
                proposal.Id, imagePath, targetDeviceNumber, needsConfirmation);

            return ToolResult<ProposalDto>.Success(ToDto(proposal,
                "The card is on screen. Nothing has started — the user presses Apply, and then " +
                (needsConfirmation ? "types the target model name before the restore can begin."
                                   : "presses Start before the restore can begin.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP propose_restore 실패.");
            return ToolResult<ProposalDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    [McpServerTool(Name = "propose_boot_repair")]
    [Description(
        "Suggest repairing the boot configuration of a disk — rewriting BCD device references, turning " +
        "off a leftover resume image, and setting standard storage drivers to boot-start so the disk " +
        "can start on different hardware. It changes the disk but does not erase data. " +
        "Run explain_boot_failure first: repairing a disk whose problem lies outside it (firmware, " +
        "hardware) changes nothing and wastes the user's time. The disk this app runs from is refused.")]
    public async Task<ToolResult<ProposalDto>> ProposeBootRepairAsync(
        [Description("Physical disk number whose boot configuration should be repaired.")] int deviceNumber,
        [Description("Why you are suggesting this — which finding led you here.")] string reason,
        CancellationToken ct = default)
    {
        try
        {
            var pre = await PrecheckAsync<ProposalDto>(reason, ct);
            if (pre is not null) return pre;

            var disks = await diskService.EnumerateDisksAsync(ct);
            var target = disks.FirstOrDefault(d => d.DeviceNumber == deviceNumber);
            if (target is null)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.DiskNotFound, $"Disk {deviceNumber} was not found.",
                    "Call list_disks again — the disk may have been disconnected.");
            }

            // 실행 중인 시스템은 고칠 대상이 아닙니다 — 이미 부팅되고 있고, 하이브도 잠겨 있습니다.
            if (target.IsSystemDisk || target.IsBootDisk)
            {
                return ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.LiveSystemDisk,
                    $"Disk {deviceNumber} is the Windows this app is running from — and it evidently boots.",
                    "Point this at the disk that fails: a clone, a restored copy, or one from another PC.");
            }

            var proposal = proposals.Propose(
                ProposalKind.BootRepair, null, target, null, reason.Trim(),
                useSnapshot: false, verifyAfterCopy: false, needsTypedConfirmation: false);

            _logger.LogInformation("MCP propose_boot_repair → 제안 {Id} (디스크 {Tgt})", proposal.Id, deviceNumber);

            return ToolResult<ProposalDto>.Success(ToDto(proposal,
                "The card is on screen. Nothing has started — the user presses Apply, which opens the " +
                "boot repair screen for that disk, and then runs the repair themselves."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP propose_boot_repair 실패.");
            return ToolResult<ProposalDto>.Fail(ToolErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// 모든 제안이 공통으로 거치는 검사 — 권한, 이유, 작업 중 여부.
    /// </summary>
    /// <returns>문제가 있으면 그대로 돌려줄 오류, 없으면 null.</returns>
    private async Task<ToolResult<T>?> PrecheckAsync<T>(string reason, CancellationToken ct)
    {
        if (!diskService.IsElevated)
        {
            return ToolResult<T>.Fail(
                ToolErrorCodes.NotElevated, "Reading disk information requires administrator rights.",
                "Restart DiskMigrator-X as administrator.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ToolResult<T>.Fail(
                ToolErrorCodes.InvalidArgument,
                "A reason is required — the user sees it on the card and needs it to decide.",
                "Explain briefly why this, why now.");
        }

        if (appState.IsBusy)
        {
            return ToolResult<T>.Fail(
                ToolErrorCodes.Busy,
                "The app is running an operation right now, so it cannot take a proposal.",
                "Watch it with get_progress and propose again once it finishes.");
        }

        await Task.CompletedTask;
        return null;
    }

    [McpServerTool(Name = "get_proposal_status")]
    [Description(
        "Check what happened to a proposal: is it still waiting, did the user apply it, ignore it, " +
        "or did it expire. Applied means the form was filled in — NOT that the operation ran; the user " +
        "still has to confirm and start it. If the user ignored it, do not simply propose the same thing " +
        "again — ask what they would prefer instead.")]
    public Task<ToolResult<ProposalDto>> GetProposalStatusAsync(
        [Description("Proposal id returned by propose_clone.")] string proposalId,
        CancellationToken ct = default)
    {
        try
        {
            var p = proposals.Find(proposalId);
            if (p is null)
            {
                return Task.FromResult(ToolResult<ProposalDto>.Fail(
                    ToolErrorCodes.InvalidArgument, $"No proposal with id {proposalId}."));
            }

            string note = p.Status switch
            {
                ProposalStatus.Pending => "Still on screen, waiting for the user.",
                ProposalStatus.Applied => "The user accepted it and the form is filled in — but it has NOT run. " +
                                          "They still need to confirm and press Start.",
                ProposalStatus.Dismissed => "The user dismissed the card. Ask what they would prefer rather than repeating it.",
                ProposalStatus.Superseded => "A newer proposal replaced this one.",
                ProposalStatus.Expired => "It expired — either the disks changed or too much time passed.",
                _ => "",
            };

            return Task.FromResult(ToolResult<ProposalDto>.Success(ToDto(p, note)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP get_proposal_status 실패.");
            return Task.FromResult(ToolResult<ProposalDto>.Fail(ToolErrorCodes.Internal, ex.Message));
        }
    }

    [McpServerTool(Name = "get_progress")]
    [Description(
        "Read how a running operation is going — phase, percent, current region, speed and ETA. " +
        "Read-only. Use it to keep the user informed while a clone or backup runs, and to see whether " +
        "something you proposed was eventually started. " +
        "When running is false, 'lastOutcome' says how the previous operation ended: Completed, " +
        "Cancelled, or Failed (null if none has finished yet). NEVER report a stopped operation as " +
        "finished without reading it — a cancelled clone leaves an incomplete copy that must not be " +
        "used, and 'lastMessage' is the exact wording the user is looking at on screen. " +
        "'paused' true means the user paused it deliberately; the numbers are frozen, not stuck.")]
    public Task<ToolResult<OperationProgress>> GetProgressAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(ToolResult<OperationProgress>.Success(appState.GetProgress()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP get_progress 실패.");
            return Task.FromResult(ToolResult<OperationProgress>.Fail(ToolErrorCodes.Internal, ex.Message));
        }
    }

    [McpServerTool(Name = "cancel_operation")]
    [Description(
        "Ask the app to stop the operation that is currently running. This is a request, not an " +
        "immediate halt — the engine finishes safely and then stops, so the target disk is left in a " +
        "known state rather than mid-write. Tell the user the copy will be incomplete and the target " +
        "should not be treated as usable.")]
    public Task<ToolResult<string>> CancelOperationAsync(CancellationToken ct = default)
    {
        try
        {
            if (!appState.IsBusy)
            {
                return Task.FromResult(ToolResult<string>.Fail(
                    ToolErrorCodes.InvalidArgument, "Nothing is running right now."));
            }

            appState.RequestCancel();
            _logger.LogInformation("MCP cancel_operation → 취소 요청");

            return Task.FromResult(ToolResult<string>.Success(
                "Cancellation requested. The engine stops at a safe point, so this is not instant. " +
                "The target will hold an incomplete copy and should not be used until it is written again. " +
                "Call get_progress to see when it actually stopped — lastOutcome becomes Cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP cancel_operation 실패.");
            return Task.FromResult(ToolResult<string>.Fail(ToolErrorCodes.Internal, ex.Message));
        }
    }

    private static ProposalDto ToDto(CloneProposal p, string note) => new(
        Id: p.Id,
        Kind: p.Kind.ToString(),
        Status: p.Status.ToString(),
        Note: note,
        SourceDeviceNumber: p.Source?.DeviceNumber,
        SourceModel: p.Source?.Model,
        TargetDeviceNumber: p.Target?.DeviceNumber,
        TargetModel: p.Target?.Model,
        ImagePath: p.ImagePath,
        Reason: p.Reason,
        IsDestructive: p.IsDestructive,
        NeedsTypedConfirmation: p.NeedsTypedConfirmation,
        CreatedUtc: p.CreatedUtc,
        ExpiresUtc: p.CreatedUtc + CloneProposal.Lifetime);
}
