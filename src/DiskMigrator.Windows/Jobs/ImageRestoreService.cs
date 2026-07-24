using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
using DiskMigrator.Windows.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>이미지 복원 한 건의 결과(복제 + 후처리).</summary>
public sealed record ImageRestoreReport(
    CloneResult Result,
    GptRepairResult? GptRepair,
    UniversalRestoreReport? UniversalRestore);

/// <summary>
/// VHDX 이미지 파일을 디스크로 복원하고, 클론과 동일한 후처리를 적용합니다.
/// </summary>
/// <remarks>
/// 이미지를 읽기 전용으로 부착해 <c>\\.\PhysicalDriveN</c>으로 만든 뒤, 대상 실디스크를
/// 클론과 <b>똑같이</b> 배타적 쓰기(오프라인+볼륨 잠금)로 열어 복제합니다. 이어서 클론
/// 오케스트레이터와 같은 후처리를 합니다:
/// <list type="number">
/// <item><b>GPT 백업 헤더 보정</b> — 대상이 이미지보다 크면 이미지에서 온 백업 헤더가 디스크
///   중간에 놓이므로, 대상 끝으로 옮기고 남는 공간을 쓸 수 있게 합니다.</item>
/// <item><b>Universal Restore</b> — (요청 시) 복원된 Windows의 저장소 드라이버를 부팅 시작으로
///   설정해 다른 하드웨어에서도 부팅되게 합니다.</item>
/// </list>
///
/// <para>복원은 대상 디스크를 파괴하므로 호출자가 <b>먼저 SafetyGuard 검사와 사용자 확인</b>을
/// 마쳐야 합니다(일반 클론과 동일).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ImageRestoreService(IDiskService diskService, ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="imagePath">복원할 .vhdx 이미지 경로.</param>
    /// <param name="target">덮어쓸 대상 디스크. 모든 데이터가 파괴됩니다.</param>
    /// <param name="universalRestore">true면 복원 후 대상 Windows를 하드웨어 독립화합니다.</param>
    public async Task<ImageRestoreReport> RestoreAsync(
        string imagePath, DiskInfo target, bool universalRestore,
        CloneOptions options, IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ImageRestoreService>();

        // 이미지를 읽기 전용으로 부착 → 물리 디스크로 읽습니다.
        using var vhd = VirtualDisk.OpenAndAttach(imagePath, readOnly: true);
        using var source = RawDiskDevice.OpenRead(vhd.PhysicalPath);
        long imageLength = AlignDown(source.Length, source.SectorSize);

        logger.LogInformation(
            "이미지 복원 시작 — {Image} → {Phys} ({Size:N0} 바이트) → 대상 [{Num}] {Model} ({TSize:N0} 바이트)",
            imagePath, vhd.PhysicalPath, source.Length, target.DeviceNumber, target.Model, target.SizeBytes);

        CloneResult result;
        GptRepairResult? gptRepair = null;

        // 대상(실디스크)을 오프라인+잠금으로 배타적 쓰기 오픈. GPT 보정까지 마친 뒤 닫아야
        // (닫으면 온라인이 되어 볼륨이 마운트되고, 그다음 Universal Restore가 하이브에 접근).
        var targetDevice = diskService.OpenWriteExclusive(target);
        try
        {
            // 대상이 이미지(원본 디스크 전체)보다 작으면 조용히 잘라 넣지 않습니다 — 파티션
            // 테이블은 원본 크기를 가리키는데 데이터가 잘려 부팅·사용 불가가 됩니다. 클론과 같은
            // 규칙으로 차단합니다(대상이 크면 뒤에서 GPT 백업 헤더를 보정해 남는 공간을 씁니다).
            long targetLength = AlignDown(targetDevice.Length, targetDevice.SectorSize);
            if (targetLength < imageLength)
            {
                throw new InvalidOperationException(
                    $"대상 디스크가 이미지보다 작아 복원할 수 없습니다. " +
                    $"이미지 {imageLength:N0}바이트, 대상 {targetLength:N0}바이트. " +
                    "이미지 전체가 들어가는 크기 이상의 디스크에 복원하십시오.");
            }

            // 스마트 복원: VHDX의 BAT를 읽어 할당된 블록(백업이 실제로 기록한 사용 영역 + 파티션
            // 테이블)만 복원하고 빈 공간은 건너뜁니다 — 백업만큼 빨라집니다. BAT를 못 읽으면
            // 전체 복원으로 안전하게 되돌립니다.
            var allocated = VhdxAllocatedRanges.TryRead(imagePath);
            List<CopyRegion> regions;
            if (allocated is not null)
            {
                regions = allocated
                    .Where(r => r.Offset + r.Length <= imageLength)
                    .Select(r => new CopyRegion
                    {
                        Source = source,
                        SourceOffset = r.Offset,
                        TargetOffset = r.Offset,
                        Length = r.Length,
                        Description = "이미지(할당 블록)",
                    })
                    .ToList();

                logger.LogInformation(
                    "스마트 복원: 할당 {Count}구간 / {Bytes:N0}바이트만 복원 (이미지 {Full:N0}바이트 중).",
                    regions.Count, regions.Sum(r => r.Length), imageLength);
            }
            else
            {
                logger.LogInformation("VHDX BAT를 읽지 못해 전체 복원으로 진행합니다.");
                regions =
                [
                    new CopyRegion
                    {
                        Source = source, SourceOffset = 0, TargetOffset = 0,
                        Length = imageLength, Description = "전체 이미지",
                    },
                ];
            }

            var plan = new ClonePlan
            {
                Name = $"이미지 복원 {Path.GetFileName(imagePath)} → [{target.DeviceNumber}] {target.Model}",
                Target = targetDevice,
                Regions = regions,
            };

            var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
            result = await engine.RunAsync(plan, options, progress, null, ct);
            targetDevice.Flush();

            // 대상이 이미지보다 크면 GPT 백업 헤더가 디스크 중간에 있으므로 끝으로 옮깁니다.
            if (result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors &&
                targetDevice.Length > imageLength)
            {
                try
                {
                    gptRepair = new GptRepair(_loggerFactory.CreateLogger<GptRepair>()).RepairIfNeeded(targetDevice);
                    logger.LogInformation("GPT 보정: {Desc}", gptRepair.Description);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "GPT 백업 헤더 보정 실패.");
                    gptRepair = new GptRepairResult(false,
                        $"데이터 복원은 정상이지만 GPT 백업 헤더 보정에 실패했습니다: {ex.Message} " +
                        "Windows 디스크 관리에서 자동 복구를 제안할 수 있습니다.");
                }
            }
        }
        finally
        {
            // 대상을 닫으면 다시 온라인이 되어 볼륨이 마운트됩니다. Universal Restore는 이 뒤에.
            targetDevice.Dispose();
        }

        // Universal Restore — 복원된 Windows를 하드웨어 독립화(요청 시, 복제 성공 시).
        UniversalRestoreReport? ur = null;
        if (universalRestore &&
            result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
        {
            try
            {
                var svc = new UniversalRestoreService(
                    diskService, _loggerFactory.CreateLogger<UniversalRestoreService>());
                ur = await svc.ApplyAsync(target, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Universal Restore 적용 중 오류. 복원 데이터는 정상입니다.");
                ur = new UniversalRestoreReport(false, null, [],
                    $"하드웨어 독립화 실패: {ex.Message}. 복원 데이터 자체는 정상입니다.");
            }
        }

        logger.LogInformation("이미지 복원 종료: {Outcome}", result.Outcome);
        return new ImageRestoreReport(result, gptRepair, ur);
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
}
