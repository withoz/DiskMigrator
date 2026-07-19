using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>클론 한 건의 전체 결과.</summary>
public sealed class CloneJobReport
{
    public required CloneResult Result { get; init; }
    public required DiskInfo Source { get; init; }
    public required DiskInfo Target { get; init; }
    public DateTime? SnapshotTimeUtc { get; init; }
    public IReadOnlyList<string> UnsnapshottedPartitions { get; init; } = [];
    public GptRepairResult? GptRepair { get; init; }
    public UniversalRestoreReport? UniversalRestore { get; init; }
    public PartitionExpandResult? PartitionExpand { get; init; }
}

/// <summary>
/// 클론 한 건을 처음부터 끝까지 실행합니다: 세션 준비 → 복제 → 검증 → GPT 보정 → 정리.
/// </summary>
/// <remarks>
/// ViewModel이 저수준 순서(잠금 유지, GPT 보정 시점, 정리 순서)를 알 필요가 없도록
/// 여기에 모아 두었습니다. 순서를 틀리면 조용히 깨진 디스크가 나옵니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CloneOrchestrator(
    IDiskService diskService,
    ISnapshotProvider snapshotProvider,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="universalRestore">
    /// true면 클론 후 대상 Windows의 SYSTEM 하이브를 손봐, 다른 하드웨어에서도 부팅되게 합니다
    /// (표준 저장소 드라이버를 부팅 시작으로). 시스템 디스크를 다른 PC로 옮길 때 켭니다.
    /// </param>
    public async Task<CloneJobReport> RunAsync(
        DiskInfo source,
        DiskInfo target,
        bool useSnapshot,
        CloneOptions options,
        bool universalRestore = false,
        IProgress<CloneProgress>? progress = null,
        PauseController? pause = null,
        CancellationToken ct = default)
    {
        var logger = _loggerFactory.CreateLogger<CloneOrchestrator>();

        logger.LogInformation(
            "=== 클론 시작 ===\n원본: [{SourceNum}] {SourceModel} ({SourceSize:N0} 바이트)\n" +
            "대상: [{TargetNum}] {TargetModel} ({TargetSize:N0} 바이트)\n스냅샷: {Snapshot}",
            source.DeviceNumber, source.Model, source.SizeBytes,
            target.DeviceNumber, target.Model, target.SizeBytes,
            useSnapshot ? "사용" : "미사용");

        var factory = new CloneSessionFactory(
            diskService, snapshotProvider, _loggerFactory.CreateLogger<CloneSessionFactory>());

        // 리사이즈 요청이 있으면 지금(원본 최신 상태)에 맞춰 배치를 계산합니다. 대상이 원본보다
        // 커야 하고, 계획기가 확대 규칙(정렬·겹침·초과)을 검증합니다.
        ResizeLayout? resizeLayout = null;
        if (options.GrowRequest is { } growRequest)
        {
            // 리사이즈는 GPT 전용입니다. MBR은 파티션 테이블을 다시 쓰는 GptRewriter가 동작하지
            // 않아, 파티션을 새 위치로 옮겨 놓고도 테이블은 옛 위치를 가리켜 배치가 깨집니다.
            // 몇 시간짜리 클론을 시작하기 전에 여기서 즉시 막습니다.
            if (source.PartitionStyle != PartitionStyle.Gpt)
            {
                throw new InvalidOperationException(
                    $"파티션 리사이즈(확대)는 GPT 디스크만 지원합니다. 이 원본은 {source.PartitionStyle} " +
                    "형식이라 리사이즈 옵션을 끄고 클론해야 합니다. (MBR 리사이즈는 아직 지원하지 않습니다.)");
            }

            resizeLayout = ResizePlanner.Plan(source.Partitions, target.SizeBytes, growRequest);
            logger.LogInformation(
                "파티션 리사이즈: 파티션 {Num} 확대, 뒤 파티션 시프트.", growRequest.PartitionNumber);
        }

        var session = await factory.CreateAsync(
            source, target, useSnapshot, options.SkipUnusedBlocks, resizeLayout, ct);

        CloneResult result;
        GptRepairResult? gptRepair = null;
        try
        {
            var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
            result = await engine.RunAsync(session.Plan, options, progress, pause, ct);

            // 복제가 성공했을 때만 GPT를 손댑니다. 실패·취소된 디스크의 GPT를 고쳐 봤자
            // 데이터가 불완전하므로 의미가 없고, 오히려 "쓸 수 있는 디스크"처럼 보이게 만듭니다.
            if (result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
            {
                // 리사이즈면 GPT를 새 배치로 다시 쓰고(엔트리 위치 변경), 아니면 백업 헤더만 보정합니다.
                gptRepair = resizeLayout is not null
                    ? RewriteGptForResize(session, source, resizeLayout, logger)
                    : TryRepairGpt(session, target, logger);
            }
        }
        finally
        {
            // 세션 Dispose가 대상을 다시 온라인으로 올려 볼륨을 마운트시킵니다.
            // Universal Restore는 반드시 이 뒤에 실행되어야 하이브 파일에 접근할 수 있습니다.
            session.Dispose();
        }

        // 클론이 성공했고 요청받았으면, 대상 Windows를 하드웨어 독립화합니다.
        UniversalRestoreReport? universalRestoreReport = null;
        if (universalRestore &&
            result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
        {
            try
            {
                var svc = new UniversalRestoreService(
                    diskService, _loggerFactory.CreateLogger<UniversalRestoreService>());
                universalRestoreReport = await svc.ApplyAsync(target, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Universal Restore 적용 중 오류. 클론 데이터는 정상입니다.");
                universalRestoreReport = new UniversalRestoreReport(false, null, [],
                    $"하드웨어 독립화 실패: {ex.Message}. 클론 데이터 자체는 정상입니다.");
            }
        }

        // 클론이 성공했으면 파티션을 확장합니다.
        // - 리사이즈: 확대한 파티션의 NTFS를 시프트로 확보한 뒤 공간까지 늘립니다.
        // - 일반: 요청 시 남는 공간을 마지막 파티션에 합칩니다(GPT 보정이 만든 미할당 공간).
        PartitionExpandResult? partitionExpand = null;
        bool cloneOk = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;
        if (cloneOk && (resizeLayout?.GrownPartition is not null || options.ExpandLastPartition))
        {
            try
            {
                var extender = new PartitionExtender(diskService, _loggerFactory.CreateLogger<PartitionExtender>());
                partitionExpand = resizeLayout?.GrownPartition is { } grown
                    ? await extender.TryExpandPartitionAsync(target.DeviceNumber, grown.SourceNumber, ct)
                    : await extender.TryExpandLastAsync(target.DeviceNumber, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "파티션 확장 중 오류. 클론 데이터는 정상입니다.");
                partitionExpand = new PartitionExpandResult(true, false,
                    $"파티션 확장에 실패했습니다: {ex.Message}. 남는 공간은 디스크 관리에서 수동 확장할 수 있습니다.");
            }
        }

        logger.LogInformation("=== 클론 종료: {Outcome} ===", result.Outcome);

        return new CloneJobReport
        {
            Result = result,
            Source = source,
            Target = target,
            SnapshotTimeUtc = session.SnapshotTimeUtc,
            UnsnapshottedPartitions = session.UnsnapshottedPartitions,
            GptRepair = gptRepair,
            UniversalRestore = universalRestoreReport,
            PartitionExpand = partitionExpand,
        };
    }

    /// <summary>
    /// 리사이즈 클론 후 대상 GPT를 새 파티션 배치로 다시 씁니다(엔트리 GUID 보존, 위치만 변경).
    /// </summary>
    /// <remarks>
    /// 대상에는 원본 GPT가 그대로 복제돼 있으므로(엔트리에 타입·고유 GUID·이름이 온전),
    /// 각 엔트리의 StartingLBA/EndingLBA만 배치대로 고치고 백업 헤더를 끝으로 옮깁니다.
    /// 실패해도 데이터 자체는 정확히 복제된 상태이므로 전체를 실패로 만들지 않습니다.
    ///
    /// <para><b>확대할 파티션은 GPT에 원래 크기로 남겨 둡니다.</b> 뒤 파티션은 새 위치로 밀리므로
    /// 확대 파티션과 그 뒤 파티션 사이에 미할당 공간이 생기고, 클론 후 <c>diskpart extend</c>가
    /// 파티션과 NTFS를 그 공간까지 함께 늘립니다(v0.2.0 마지막 파티션 확장과 같은 검증된 경로).
    /// GPT에서 미리 슬롯만 키우면 그 뒤에 미할당이 없어 <c>extend</c>가 무효 인자로 실패하고,
    /// NTFS는 큰 슬롯 안에서 원래 크기로 남습니다.</para>
    /// </remarks>
    private GptRepairResult? RewriteGptForResize(
        CloneSession session, DiskInfo source, ResizeLayout layout, ILogger logger)
    {
        int sector = session.TargetDevice.SectorSize;

        var remaps = layout.Partitions.Select(tp =>
        {
            var src = source.Partitions.First(p => p.Number == tp.SourceNumber);
            long oldStartLba = src.StartingOffset / sector;
            long newStartLba = tp.StartingOffset / sector;
            // 확대 파티션은 원래 크기 유지(뒤에 미할당 공간을 두고 extend가 채움), 나머지는 그대로.
            long lengthBytes = tp.Grown ? src.LengthBytes : tp.LengthBytes;
            long newEndLba = (tp.StartingOffset + lengthBytes) / sector - 1;
            return new PartitionRemap(oldStartLba, newStartLba, newEndLba);
        }).ToList();

        try
        {
            var rewriter = new GptRewriter(_loggerFactory.CreateLogger<GptRewriter>());
            var r = rewriter.Rewrite(session.TargetDevice, remaps);
            return new GptRepairResult(r.Rewritten, r.Description);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "리사이즈 GPT 재작성에 실패했습니다.");
            return new GptRepairResult(false,
                $"데이터 복제는 정상적으로 끝났지만 파티션 배치를 반영한 GPT 재작성에 실패했습니다: {ex.Message}");
        }
    }

    private GptRepairResult? TryRepairGpt(CloneSession session, DiskInfo target, ILogger logger)
    {
        // 대상이 원본과 크기가 같으면 백업 헤더가 이미 제자리이므로 아무 일도 하지 않습니다.
        if (target.SizeBytes <= session.Plan.TotalBytes) return null;

        try
        {
            var repair = new GptRepair(_loggerFactory.CreateLogger<GptRepair>());
            return repair.RepairIfNeeded(session.TargetDevice);
        }
        catch (Exception ex)
        {
            // 데이터는 이미 정확히 복제되었습니다. GPT 보정 실패는 "남는 공간을 못 쓴다" 정도의
            // 문제이므로 전체를 실패로 만들지 않고, 사용자에게 알리기만 합니다.
            logger.LogWarning(ex, "GPT 백업 헤더 보정에 실패했습니다.");

            return new GptRepairResult(false,
                $"데이터 복제는 정상적으로 끝났지만 GPT 백업 헤더 보정에 실패했습니다: {ex.Message} " +
                "Windows 디스크 관리에서 디스크를 열면 자동으로 복구를 제안할 수 있습니다.");
        }
    }
}
