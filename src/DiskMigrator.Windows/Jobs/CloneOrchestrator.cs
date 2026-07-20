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

    /// <summary>
    /// 리사이즈 클론에서 GPT 재작성이 실패해 파티션 배치가 깨졌는지. true면 데이터는
    /// 복사됐어도 파티션 테이블이 옛 위치를 가리켜, 이 디스크는 부팅·사용할 수 없습니다.
    /// 이 경우 성공이 아니라 손상으로 다뤄야 합니다(확장·하드웨어 독립화도 건너뜀).
    /// </summary>
    public bool ResizeLayoutCorrupted { get; init; }
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
        // 남는 공간 처리 방식이 GrowPartition이면 어느 파티션을 넓힐지 반드시 있어야 합니다.
        // 없는 채로 진행하면 아무 일도 안 하면서 사용자는 넓혀졌다고 믿게 됩니다.
        if (options.FreeSpace == FreeSpaceMode.GrowPartition && options.GrowRequest is null)
        {
            throw new InvalidOperationException(
                "넓힐 파티션을 고르지 않았습니다. 파티션을 고르거나 다른 방식을 선택하십시오.");
        }

        ResizeLayout? resizeLayout = null;
        if (options.FreeSpace == FreeSpaceMode.GrowPartition && options.GrowRequest is { } growRequest)
        {
            // 리사이즈는 파티션을 새 위치로 옮기므로 파티션 테이블을 다시 쓸 수 있어야 합니다.
            // GPT는 GptRewriter가, MBR은 MbrRewriter가 맡습니다. 둘 다 아닌 형식(초기화되지 않은
            // 디스크 등)은 옮겨 놓고 테이블을 못 고쳐 배치가 깨지므로, 몇 시간짜리 클론을
            // 시작하기 전에 여기서 막습니다.
            if (source.PartitionStyle is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
            {
                throw new InvalidOperationException(
                    $"파티션 리사이즈(확대)는 GPT 또는 MBR 디스크만 지원합니다. 이 원본은 " +
                    $"{source.PartitionStyle} 형식이라 리사이즈 옵션을 끄고 클론해야 합니다.");
            }

            // 확장 파티션(논리 드라이브)은 EBR 체인을 함께 다시 써야 해서 지원하지 않습니다.
            // MbrRewriter도 거절하지만 그 시점은 복제가 끝난 뒤입니다 — 몇 시간을 쓰고 나서
            // 파티션은 옮겨졌는데 테이블은 못 고친 못 쓰는 디스크가 남습니다. 여기서 막습니다.
            if (source.HasExtendedPartition)
            {
                throw new InvalidOperationException(
                    "원본에 확장 파티션(논리 드라이브)이 있어 리사이즈할 수 없습니다. " +
                    "논리 드라이브는 EBR 체인으로 이어져 있어 옮기려면 체인 전체를 다시 써야 합니다. " +
                    "'마지막 파티션에 합치기'나 '그대로 둡니다'를 쓰십시오.");
            }

            // MBR의 시작·크기 필드는 32비트 섹터 수라 약 2 TB까지만 가리킬 수 있습니다. 대상이
            // 그보다 크면 뒤로 밀린 파티션이 표현되지 않아 테이블이 깨집니다. 미리 막습니다.
            if (source.PartitionStyle == PartitionStyle.Mbr &&
                target.SizeBytes / target.LogicalSectorSize - 1 > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "MBR 디스크는 약 2 TB까지만 파티션 위치를 가리킬 수 있어, 이보다 큰 대상에는 " +
                    "리사이즈를 적용할 수 없습니다. '마지막 파티션에 합치기'를 쓰거나 원본을 " +
                    "GPT로 바꾼 뒤 클론하십시오.");
            }

            // GPT 엔트리의 위치는 LBA(섹터 번호)로 저장됩니다. 리사이즈는 원본 GPT를 대상에 그대로
            // 복사한 뒤 엔트리의 LBA만 새 배치로 고치는데, 원본과 대상의 논리 섹터 크기가 다르면
            // 그 LBA가 서로 다른 단위가 되어 재배치 대응(옛 StartingLBA 매칭)이 어긋납니다. 그러면
            // 데이터는 새 위치에 있는데 GPT 재작성은 실패해 배치가 깨지므로, 미리 막습니다.
            if (source.LogicalSectorSize != target.LogicalSectorSize)
            {
                throw new InvalidOperationException(
                    "파티션 리사이즈(확대)는 원본과 대상의 논리 섹터 크기가 같아야 합니다. " +
                    $"원본 {source.LogicalSectorSize}바이트, 대상 {target.LogicalSectorSize}바이트라 " +
                    "리사이즈 옵션을 끄고 클론해야 합니다.");
            }

            resizeLayout = ResizePlanner.Plan(source.Partitions, target.SizeBytes, growRequest);
            logger.LogInformation(
                "파티션 리사이즈: 파티션 {Num} 확대, 뒤 파티션 시프트.", growRequest.PartitionNumber);
        }
        else if (target.SizeBytes < source.SizeBytes)
        {
            // 대상이 원본보다 작습니다. SafetyGuard가 "원본 파티션이 모두 들어간다"를 확인했을
            // 때만 여기까지 옵니다(아니면 차단). 파티션은 제자리에 복제하고, 원본 끝의 GPT 백업
            // 헤더는 복사하지 않은 채 클론 후 줄어든 대상 끝에 다시 씁니다. 원본에는 쓰지 않습니다.
            if (source.PartitionStyle != PartitionStyle.Gpt)
            {
                throw new InvalidOperationException(
                    $"대상이 원본보다 작은 클론은 GPT 원본만 지원합니다. 이 원본은 {source.PartitionStyle} " +
                    "형식이라 대상이 원본 이상 크기여야 합니다.");
            }

            resizeLayout = ResizePlanner.PlanFit(source.Partitions, target.SizeBytes);
            logger.LogInformation(
                "맞춤 클론: 대상({Target:N0}바이트)이 원본({Source:N0}바이트)보다 작지만 파티션이 모두 " +
                "들어갑니다. 파티션은 제자리 복제, GPT 백업 헤더만 새 끝으로 재작성합니다.",
                target.SizeBytes, source.SizeBytes);
        }

        var session = await factory.CreateAsync(
            source, target, useSnapshot, options.SkipUnusedBlocks, resizeLayout, ct);

        CloneResult result;
        GptRepairResult? gptRepair = null;
        bool resizeLayoutCorrupted = false;
        try
        {
            var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
            result = await engine.RunAsync(session.Plan, options, progress, pause, ct);

            // 복제가 성공했을 때만 GPT를 손댑니다. 실패·취소된 디스크의 GPT를 고쳐 봤자
            // 데이터가 불완전하므로 의미가 없고, 오히려 "쓸 수 있는 디스크"처럼 보이게 만듭니다.
            if (result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
            {
                if (resizeLayout is not null)
                {
                    // 리사이즈: GPT를 새 배치로 다시 씁니다(엔트리 위치 변경).
                    gptRepair = RewriteGptForResize(session, source, resizeLayout, logger);

                    // 재작성 실패의 무게는 배치가 파티션을 옮겼는지에 달렸습니다.
                    //
                    // - 옮긴 경우(확대 리사이즈): 데이터는 새 위치에 있는데 파티션 테이블은 옛
                    //   위치를 가리켜 배치가 깨집니다. 데이터가 복사됐다는 이유로 "완료"로 보이면
                    //   사용자가 부팅 불가 디스크를 정상 사본으로 오인합니다(실기 MBR 버그와 같은
                    //   실패 모드) → 손상으로 표시해 뒤 단계를 건너뛰고 실패로 알립니다.
                    // - 제자리인 경우(맞춤 클론): 파티션 위치는 정확하고 백업 헤더·사용 가능 경계만
                    //   못 고친 것이라, 일반 GPT 보정 실패와 같은 수준입니다. 여기서 "부팅 불가"로
                    //   경고하면 멀쩡한 사본을 두고 늑대를 부르는 셈이므로 경고만 남깁니다.
                    resizeLayoutCorrupted =
                        MovesPartitions(source, resizeLayout) && gptRepair is not { WasRepaired: true };
                }
                else
                {
                    // 일반 클론: 백업 헤더만 보정합니다(실패해도 데이터는 온전).
                    gptRepair = TryRepairGpt(session, target, logger);
                }
            }
        }
        finally
        {
            // 세션 Dispose가 대상을 다시 온라인으로 올려 볼륨을 마운트시킵니다.
            // Universal Restore는 반드시 이 뒤에 실행되어야 하이브 파일에 접근할 수 있습니다.
            session.Dispose();
        }

        // 클론이 성공했고 요청받았으면, 대상 Windows를 하드웨어 독립화합니다.
        // 리사이즈 배치가 깨졌으면(GPT 재작성 실패) 볼륨이 기대 위치에 없어 하이브 접근이
        // 실패하고, 애초에 부팅 불가 디스크라 손볼 의미가 없으므로 건너뜁니다.
        UniversalRestoreReport? universalRestoreReport = null;
        if (universalRestore && !resizeLayoutCorrupted &&
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

        // 남는 공간 처리 — 고른 방식 하나만 실행합니다.
        //
        // 예전에는 "리사이즈"와 "마지막 파티션 확장"이 별개 불리언이라 둘 다 켜면 리사이즈가
        // 이기고 다른 하나는 조용히 버려졌습니다. 이제 모드가 하나뿐이라 그 조합이 없습니다.
        //
        // 배치가 깨진 리사이즈 클론에서는 확장하지 않습니다. 파티션 테이블이 옛 위치를 가리키는
        // 디스크에 extend를 걸면 엉뚱한 파티션을 건드릴 수 있고, 어차피 못 쓰는 사본입니다.
        PartitionExpandResult? partitionExpand = null;
        bool cloneOk = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;
        if (cloneOk && !resizeLayoutCorrupted && options.FreeSpace != FreeSpaceMode.Leave)
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
            ResizeLayoutCorrupted = resizeLayoutCorrupted,
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
    /// <summary>
    /// 이 배치가 파티션을 실제로 옮기거나 키우는지(확대 리사이즈) 아니면 제자리인지(맞춤 클론).
    /// </summary>
    /// <remarks>
    /// GPT 재작성 실패의 심각도를 가르는 기준입니다. 옮긴 배치에서 재작성이 실패하면 파티션
    /// 테이블이 옛 위치를 가리켜 디스크를 쓸 수 없지만, 제자리 배치라면 파티션은 정확한 곳에
    /// 있고 백업 헤더만 못 고친 상태입니다.
    /// </remarks>
    private static bool MovesPartitions(DiskInfo source, ResizeLayout layout) =>
        layout.Partitions.Any(tp =>
            tp.Grown ||
            source.Partitions.FirstOrDefault(p => p.Number == tp.SourceNumber) is not { } src ||
            src.StartingOffset != tp.StartingOffset);

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

        // 파티션 배치 계산과 remap은 두 형식이 똑같습니다. 다른 것은 그 배치를 어느 테이블에
        // 적어 넣느냐뿐이라, 여기서만 갈라집니다.
        bool isMbr = source.PartitionStyle == PartitionStyle.Mbr;
        string tableName = isMbr ? "MBR" : "GPT";

        try
        {
            if (isMbr)
            {
                var r = new MbrRewriter(_loggerFactory.CreateLogger<MbrRewriter>())
                    .Rewrite(session.TargetDevice, remaps);
                return new GptRepairResult(r.Rewritten, r.Description);
            }
            else
            {
                var r = new GptRewriter(_loggerFactory.CreateLogger<GptRewriter>())
                    .Rewrite(session.TargetDevice, remaps);
                return new GptRepairResult(r.Rewritten, r.Description);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "리사이즈 {Table} 재작성에 실패했습니다 — 파티션 배치가 깨졌습니다.", tableName);
            return new GptRepairResult(false,
                $"데이터는 복제됐지만 파티션 배치를 반영한 {tableName} 재작성에 실패했습니다({ex.Message}). " +
                "파티션 테이블이 옛 위치를 가리켜 이 디스크는 부팅·사용할 수 없습니다. " +
                "리사이즈를 끄고 다시 클론하십시오.");
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
