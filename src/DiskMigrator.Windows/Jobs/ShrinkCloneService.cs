using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// 대상이 원본보다 작아 제자리 복제가 불가능할 때의 <b>축소 클론</b>: 내부적으로
/// 백업 → (차등 자식에서) 축소 → 압축 복원을 이어 실행합니다.
/// </summary>
/// <remarks>
/// 확정 설계대로 <b>위험한 파티션·파일시스템 조작은 복원 경로에만</b> 둡니다 — 클론 엔진에는
/// 축소가 없습니다. 이 서비스는 그 결정의 실행부로, 검증된 두 서비스를 순서대로 부를 뿐입니다:
/// <list type="number">
/// <item><see cref="ImageBackupService"/> — 원본을 임시 VHDX로(스마트 백업: 사용분만 저장).
///   원본은 읽기만 합니다.</item>
/// <item><see cref="ImageRestoreService.RestoreWithShrinkAsync"/> — 임시 이미지의 차등 자식에서
///   파티션을 줄여 대상에 압축 복원(+GPT 재작성·UR). 임시 이미지도 수정되지 않습니다.</item>
/// </list>
/// 임시 이미지는 성공·실패와 관계없이 마지막에 삭제합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ShrinkCloneService(
    IDiskService diskService,
    ISnapshotProvider snapshotProvider,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="decision">시작 전 판정(<see cref="ShrinkClonePlanner.Evaluate"/>)의 결과.</param>
    /// <param name="tempImagePath">임시 백업 이미지를 둘 경로(호출자가 여유 공간을 확인해 고름).</param>
    public async Task<ImageRestoreReport> RunAsync(
        DiskInfo source, DiskInfo target, ShrinkCloneDecision decision, string tempImagePath,
        bool useSnapshot, bool universalRestore, CloneOptions options,
        IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempImagePath);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ShrinkCloneService>();
        logger.LogInformation(
            "=== 축소 클론 시작 === 원본 [{Src}] {SrcModel} → 대상 [{Tgt}] {TgtModel}. " +
            "파티션 {Part} {Cur} → {New}, 임시 이미지 {Temp}.",
            source.DeviceNumber, source.Model, target.DeviceNumber, target.Model,
            decision.PartitionNumber, SizeFormatter.Format(decision.CurrentBytes),
            SizeFormatter.Format(decision.NewBytes), tempImagePath);

        // 두 단계가 각각 0~100%를 보고하므로, 단계 이름에 순서를 붙여 전체 흐름이 보이게 합니다.
        IProgress<CloneProgress>? backupProgress = progress is null ? null
            : new Progress<CloneProgress>(p => progress.Report(p with { Phase = $"[1/2 백업] {p.Phase}" }));
        IProgress<CloneProgress>? restoreProgress = progress is null ? null
            : new Progress<CloneProgress>(p => progress.Report(p with { Phase = $"[2/2 복원] {p.Phase}" }));

        try
        {
            // --- 1/2: 원본 → 임시 VHDX (스마트 백업: 사용 블록만 저장) -------------
            var backupOptions = new CloneOptions
            {
                BufferSize = options.BufferSize,
                ReadRetryCount = options.ReadRetryCount,
                RetryDelay = options.RetryDelay,
                BadSectorPolicy = options.BadSectorPolicy,
                MaxBadSectors = options.MaxBadSectors,
                VerifyAfterClone = options.VerifyAfterClone,
                ProgressInterval = options.ProgressInterval,
                FlushInterval = options.FlushInterval,
            };

            var backupSvc = new ImageBackupService(diskService, snapshotProvider, _loggerFactory);
            var backupResult = await backupSvc.BackupAsync(
                source, tempImagePath, useSnapshot, skipUnusedBlocks: true,
                backupOptions, backupProgress, ct);

            if (backupResult.Outcome is not (CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors) ||
                backupResult.VerificationPassed == false)
            {
                throw new InvalidOperationException(
                    $"축소 클론의 백업 단계가 실패해 중단했습니다({backupResult.Outcome}). " +
                    "대상에는 아무것도 쓰지 않았습니다.");
            }

            // --- 2/2: 임시 이미지 → 대상 (차등 자식 축소 + 압축 복원 + GPT 재작성 + UR) ---
            var restoreSvc = new ImageRestoreService(diskService, _loggerFactory);
            var report = await restoreSvc.RestoreWithShrinkAsync(
                tempImagePath, target, decision.PartitionNumber, decision.NewBytes,
                universalRestore, options, restoreProgress, ct);

            logger.LogInformation("=== 축소 클론 종료: {Outcome} ===", report.Result.Outcome);
            return report;
        }
        finally
        {
            try { if (File.Exists(tempImagePath)) File.Delete(tempImagePath); }
            catch (Exception ex) { logger.LogWarning(ex, "임시 이미지 삭제 실패: {Path}", tempImagePath); }
        }
    }
}
