using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
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
/// 동적 VHDX를 원본 크기로 만들어 물리 디스크로 부착한 뒤, <b>클론과 똑같은 복사 계획</b>을
/// 그 VHDX에 실행합니다. 계획은 <see cref="CloneSessionFactory.PreviewAsync"/>가 만들어 주므로
/// 다음이 그대로 딸려옵니다:
/// <list type="bullet">
/// <item><b>VSS 스냅샷</b> — 실행 중인 시스템·마운트된 볼륨을 그 시점에 정지시켜 일관된 백업.</item>
/// <item><b>스마트 클론</b> — NTFS 빈 영역을 건너뛰고 사용 블록만 이미지에 기록. 동적 VHDX라
///   쓴 블록만 파일에 할당되므로 이미지가 실사용량만큼만 커집니다.</item>
/// <item>파티션 테이블·EFI·GPT 백업 헤더는 원시로 그대로.</item>
/// </list>
/// 부착된 VHDX는 쓰는 동안 오프라인으로 두어 Windows가 이미지 속 볼륨을 자동 마운트해
/// 덮어쓰는 것을 막습니다(일반 클론의 대상 디스크와 같은 이유).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ImageBackupService(
    IDiskService diskService,
    ISnapshotProvider snapshotProvider,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <param name="source">백업할 원본 디스크.</param>
    /// <param name="imagePath">만들 .vhdx 파일 경로(이미 있으면 실패 — 덮어쓰지 않음).</param>
    /// <param name="useSnapshot">true면 원본의 NTFS 볼륨을 VSS로 스냅샷해서 읽습니다(라이브 시스템 필수).</param>
    /// <param name="skipUnusedBlocks">true면 스마트 클론(NTFS 빈 영역 건너뛰기). 스냅샷이 전제입니다.</param>
    public async Task<CloneResult> BackupAsync(
        DiskInfo source, string imagePath, bool useSnapshot, bool skipUnusedBlocks,
        CloneOptions options, IProgress<CloneProgress>? progress = null,
        PauseController? pause = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ImageBackupService>();

        // 클론과 동일한 복사 계획을 만듭니다(VSS 스냅샷 + 스마트 클론 + 원시 테이블). 대상은 없습니다.
        var factory = new CloneSessionFactory(
            diskService, snapshotProvider, _loggerFactory.CreateLogger<CloneSessionFactory>());

        using var preview = await factory.PreviewAsync(source, useSnapshot, skipUnusedBlocks, resizeLayout: null, ct);

        logger.LogInformation(
            "이미지 백업 시작 — 원본 [{Num}] {Model} ({Size:N0} 바이트) → {Image} " +
            "(구간 {Regions}개, 복사 {Copy:N0} 바이트, 스냅샷 {Snap})",
            source.DeviceNumber, source.Model, source.SizeBytes, imagePath,
            preview.Regions.Count, preview.TotalBytes, preview.SnapshotTimeUtc is null ? "미사용" : "사용");

        // 원본 크기의 동적 VHDX를 만들어 부착합니다(쓴 블록만 파일에 할당).
        using var vhd = VirtualDisk.CreateAndAttach(imagePath, source.SizeBytes, source.LogicalSectorSize);
        logger.LogInformation("VHDX 부착: {Phys} (디스크 {Num})", vhd.PhysicalPath, vhd.DiskNumber);

        // 쓰는 동안 Windows가 이미지 속 볼륨을 자동 마운트하지 못하게 오프라인으로.
        using var offline = DiskOfflineScope.Take(vhd.PhysicalPath, vhd.DiskNumber, logger);

        using var target = RawDiskDevice.OpenWrite(vhd.PhysicalPath);

        var plan = new ClonePlan
        {
            Name = $"이미지 백업 → {Path.GetFileName(imagePath)}",
            Target = target,
            Regions = preview.Regions,
        };

        var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
        var result = await engine.RunAsync(plan, options, progress, pause, ct);

        target.Flush();
        logger.LogInformation("이미지 백업 종료: {Outcome}", result.Outcome);
        return result;
    }
}
