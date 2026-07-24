using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// VHDX 이미지 파일을 디스크로 복원합니다(이미지 → 디스크 섹터 복제).
/// </summary>
/// <remarks>
/// 이미지를 읽기 전용으로 부착해 <c>\\.\PhysicalDriveN</c>으로 만든 뒤, 대상 실디스크를
/// 클론과 <b>똑같이</b> 배타적 쓰기(오프라인+볼륨 잠금)로 열어 기존 클론 엔진으로 복제합니다.
///
/// <para>복원은 대상 디스크를 파괴하므로, 호출자가 <b>먼저 SafetyGuard 검사와 사용자 확인</b>을
/// 마쳐야 합니다(일반 클론과 동일). 이번 단계는 전체 섹터 복제이며, 대상이 이미지보다 클 때의
/// GPT 백업 헤더 보정·하드웨어 독립화·부팅 복구는 다음 단계에서 클론 오케스트레이터 로직을
/// 공유해 붙입니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ImageRestoreService(IDiskService diskService, ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="imagePath">복원할 .vhdx 이미지 경로.</param>
    /// <param name="target">덮어쓸 대상 디스크. 모든 데이터가 파괴됩니다.</param>
    public async Task<CloneResult> RestoreAsync(
        string imagePath, DiskInfo target, CloneOptions options,
        IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ImageRestoreService>();

        // 이미지를 읽기 전용으로 부착 → 물리 디스크로 읽습니다.
        using var vhd = VirtualDisk.OpenAndAttach(imagePath, readOnly: true);
        using var source = RawDiskDevice.OpenRead(vhd.PhysicalPath);
        logger.LogInformation(
            "이미지 복원 시작 — {Image} → {Phys} ({Size:N0} 바이트) → 대상 [{Num}] {Model}",
            imagePath, vhd.PhysicalPath, source.Length, target.DeviceNumber, target.Model);

        // 대상(실디스크)을 오프라인+잠금으로 배타적 쓰기 오픈(일반 클론의 대상 준비와 동일).
        using var targetDevice = diskService.OpenWriteExclusive(target);

        long length = Math.Min(
            AlignDown(source.Length, source.SectorSize),
            AlignDown(targetDevice.Length, targetDevice.SectorSize));

        var plan = new ClonePlan
        {
            Name = $"이미지 복원 {Path.GetFileName(imagePath)} → [{target.DeviceNumber}] {target.Model}",
            Target = targetDevice,
            Regions =
            [
                new CopyRegion
                {
                    Source = source,
                    SourceOffset = 0,
                    TargetOffset = 0,
                    Length = length,
                    Description = "전체 이미지",
                },
            ],
        };

        var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
        var result = await engine.RunAsync(plan, options, progress, null, ct);

        targetDevice.Flush();
        logger.LogInformation("이미지 복원 종료: {Outcome}", result.Outcome);
        return result;
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
}
