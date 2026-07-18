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

    public async Task<CloneJobReport> RunAsync(
        DiskInfo source,
        DiskInfo target,
        bool useSnapshot,
        CloneOptions options,
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

        using var session = await factory.CreateAsync(source, target, useSnapshot, ct);

        var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
        var result = await engine.RunAsync(session.Plan, options, progress, pause, ct);

        GptRepairResult? gptRepair = null;

        // 복제가 성공했을 때만 GPT를 손댑니다. 실패했거나 취소된 디스크의 GPT를 고쳐 봤자
        // 데이터가 불완전하므로 의미가 없고, 오히려 "쓸 수 있는 디스크"처럼 보이게 만듭니다.
        if (result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
        {
            gptRepair = TryRepairGpt(session, target, logger);
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
        };
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
