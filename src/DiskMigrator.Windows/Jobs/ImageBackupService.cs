using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Localization;
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

    /// <summary>
    /// <b>증분 백업</b>: 기존 백업(<paramref name="parentImagePath"/>)의 차등 자식
    /// (<paramref name="childImagePath"/>)을 만들어, 그 이후 <b>바뀐 블록만</b> 저장합니다.
    /// </summary>
    /// <remarks>
    /// 자식을 쓰기 가능으로 부착하면 병합 뷰(부모 내용)가 보입니다. 전체 백업과 같은 복사
    /// 계획을 돌리되 <see cref="CloneOptions.WriteOnlyChangedBlocks"/>로 병합 뷰와 비교해
    /// 다른 블록만 쓰므로, 자식 파일에는 변경분만 할당됩니다. <b>부모는 절대 수정되지
    /// 않습니다</b>(쓰기는 전부 자식으로). 완성된 자식 = 현재 디스크 상태의 완전한 이미지라,
    /// 복원은 자식 파일을 고르면 기존 경로 그대로 동작합니다(부모 파일들이 같은 폴더에
    /// 원래 이름으로 있어야 함). 검증(VerifyAfterClone)도 병합 뷰 전체를 대조하므로
    /// 건너뛴 블록까지 포함해 확인됩니다.
    /// </remarks>
    public async Task<CloneResult> BackupIncrementalAsync(
        DiskInfo source, string parentImagePath, string childImagePath,
        bool useSnapshot, bool skipUnusedBlocks,
        CloneOptions options, IProgress<CloneProgress>? progress = null,
        PauseController? pause = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(childImagePath);
        ArgumentNullException.ThrowIfNull(options);

        var logger = _loggerFactory.CreateLogger<ImageBackupService>();

        var factory = new CloneSessionFactory(
            diskService, snapshotProvider, _loggerFactory.CreateLogger<CloneSessionFactory>());
        using var preview = await factory.PreviewAsync(source, useSnapshot, skipUnusedBlocks, resizeLayout: null, ct);

        logger.LogInformation(
            "증분 백업 시작 — 원본 [{Num}] {Model} → {Child} (부모 {Parent}, 구간 {Regions}개, 처리 {Copy:N0} 바이트)",
            source.DeviceNumber, source.Model, Path.GetFileName(childImagePath),
            Path.GetFileName(parentImagePath), preview.Regions.Count, preview.TotalBytes);

        // 부모의 차등 자식을 만들어 부착 — 크기·섹터는 부모에서 상속됩니다.
        using var vhd = VirtualDisk.CreateDifferencingAndAttach(childImagePath, parentImagePath);
        logger.LogInformation("차등 VHDX 부착: {Phys} (디스크 {Num})", vhd.PhysicalPath, vhd.DiskNumber);

        using var offline = DiskOfflineScope.Take(vhd.PhysicalPath, vhd.DiskNumber, logger);
        using var target = RawDiskDevice.OpenWrite(vhd.PhysicalPath);

        // 부모가 이 디스크의 백업이 맞는지 최소 확인 — 크기가 다르면 다른 디스크의 이미지입니다.
        if (target.Length != source.SizeBytes)
        {
            throw new InvalidOperationException(L.T(
                $"선택한 백업({Path.GetFileName(parentImagePath)})의 크기({target.Length:N0}바이트)가 " +
                $"원본 디스크({source.SizeBytes:N0}바이트)와 다릅니다 — 다른 디스크의 백업입니다. " +
                "증분 백업은 같은 디스크의 기존 백업에만 이어 쓸 수 있습니다.",
                $"The selected backup ({Path.GetFileName(parentImagePath)}) has a different size " +
                $"({target.Length:N0} bytes) than the source disk ({source.SizeBytes:N0} bytes) — " +
                "it is a backup of a different disk. Incremental backup can only continue an existing " +
                "backup of the same disk."));
        }

        var plan = new ClonePlan
        {
            Name = $"증분 백업 → {Path.GetFileName(childImagePath)}",
            Target = target,
            Regions = preview.Regions,
        };

        var incrementalOptions = new CloneOptions
        {
            BufferSize = options.BufferSize,
            ReadRetryCount = options.ReadRetryCount,
            RetryDelay = options.RetryDelay,
            BadSectorPolicy = options.BadSectorPolicy,
            MaxBadSectors = options.MaxBadSectors,
            VerifyAfterClone = options.VerifyAfterClone,
            ProgressInterval = options.ProgressInterval,
            FlushInterval = options.FlushInterval,
            WriteOnlyChangedBlocks = true,
        };

        var engine = new CloneEngine(_loggerFactory.CreateLogger<CloneEngine>());
        var result = await engine.RunAsync(plan, incrementalOptions, progress, pause, ct);

        target.Flush();
        logger.LogInformation("증분 백업 종료: {Outcome}", result.Outcome);
        return result;
    }
}
