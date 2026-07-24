using System.Runtime.Versioning;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// 디스크를 VHDX 이미지 파일로 백업합니다(디스크 → 이미지 섹터 복제).
/// </summary>
/// <remarks>
/// 동적 VHDX를 원본 크기로 만들어 물리 디스크로 부착한 뒤, 기존 클론 엔진으로 원본을 그대로
/// 복제합니다. 동적이라 실제로 쓴 블록만 파일에 할당됩니다. 부착된 VHDX는 쓰는 동안
/// 오프라인으로 두어 Windows가 이미지 속 볼륨을 자동 마운트해 덮어쓰는 것을 막습니다
/// (일반 클론의 대상 디스크와 같은 이유).
///
/// <para>이번 단계는 <b>전체 섹터 복제</b>입니다. VSS 스냅샷(라이브 시스템)·스마트 클론(빈 영역
/// 건너뛰기)은 다음 단계에서 <see cref="CloneSessionFactory"/>의 계획을 재사용해 붙입니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ImageBackupService(ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="sourceDevicePath">읽을 원본 디스크(<c>\\.\PhysicalDriveN</c>).</param>
    /// <param name="imagePath">만들 .vhdx 파일 경로(이미 있으면 실패 — 덮어쓰지 않음).</param>
    public async Task<CloneResult> BackupAsync(
        string sourceDevicePath, string imagePath, CloneOptions options,
        IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDevicePath);
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ImageBackupService>();

        using var source = RawDiskDevice.OpenRead(sourceDevicePath);
        logger.LogInformation(
            "이미지 백업 시작 — 원본 {Path} ({Size:N0} 바이트, 섹터 {Sector}) → {Image}",
            sourceDevicePath, source.Length, source.SectorSize, imagePath);

        // 원본 크기의 동적 VHDX를 만들어 부착합니다(쓴 블록만 파일에 할당).
        using var vhd = VirtualDisk.CreateAndAttach(imagePath, source.Length, source.SectorSize);
        logger.LogInformation("VHDX 부착: {Phys} (디스크 {Num})", vhd.PhysicalPath, vhd.DiskNumber);

        // 쓰는 동안 Windows가 이미지 속 볼륨을 자동 마운트하지 못하게 오프라인으로.
        using var offline = DiskOfflineScope.Take(vhd.PhysicalPath, vhd.DiskNumber, logger);

        using var target = RawDiskDevice.OpenWrite(vhd.PhysicalPath);

        var plan = new ClonePlan
        {
            Name = $"이미지 백업 → {Path.GetFileName(imagePath)}",
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = source,
                    SourceOffset = 0,
                    TargetOffset = 0,
                    Length = source.Length,
                    Description = "전체 디스크",
                },
            ],
        };

        var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
        var result = await engine.RunAsync(plan, options, progress, null, ct);

        target.Flush();
        logger.LogInformation("이미지 백업 종료: {Outcome}", result.Outcome);
        return result;
    }
}
