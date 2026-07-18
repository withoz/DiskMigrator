using System.Runtime.Versioning;
using Alphaleonis.Win32.Vss;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Snapshots;

/// <summary>
/// VSS(볼륨 섀도 복사본)로 실행 중인 볼륨의 정지된 이미지를 만듭니다.
/// </summary>
/// <remarks>
/// 실행 중인 시스템 디스크를 그냥 읽으면, 읽는 몇십 분 동안 파일이 계속 바뀌어
/// 결과물이 "앞부분은 10시 상태, 뒷부분은 10시 40분 상태"인 뒤죽박죽이 됩니다.
/// NTFS 저널과 레지스트리 하이브가 서로 맞지 않게 되어 대개 부팅되지 않습니다.
/// VSS는 스냅샷 시점의 정지된 이미지를 제공해 이 문제를 해결합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VssSnapshotProvider(ILogger<VssSnapshotProvider>? logger = null) : ISnapshotProvider
{
    private readonly ILogger _logger = logger ?? NullLogger<VssSnapshotProvider>.Instance;

    public bool IsAvailable
    {
        get
        {
            try
            {
                // 플랫폼별 네이티브 어셈블리를 실제로 로드해 봐야 사용 가능 여부를 알 수 있습니다.
                _ = VssFactoryProvider.Default.GetVssFactory();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VSS를 사용할 수 없습니다.");
                return false;
            }
        }
    }

    public Task<ISnapshotSet> CreateSnapshotSetAsync(
        IReadOnlyList<string> volumeGuidPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(volumeGuidPaths);

        if (volumeGuidPaths.Count == 0)
        {
            throw new ArgumentException("스냅샷을 만들 볼륨이 없습니다.", nameof(volumeGuidPaths));
        }

        return Task.Run<ISnapshotSet>(() => Create(volumeGuidPaths, ct), ct);
    }

    /// <summary>
    /// 각 볼륨의 섀도 저장소(diff 영역) 최대 크기를 이 값 이상으로 확보합니다.
    /// </summary>
    /// <remarks>
    /// 스냅샷은 copy-on-write입니다. 스냅샷을 뜬 뒤 원본 볼륨에 쓰기가 일어날 때마다,
    /// 원래 블록이 diff 영역으로 복사되어 스냅샷의 그 시점 데이터를 보존합니다. diff 영역이
    /// 꽉 차면 VSS는 스냅샷을 무효화하고, 그때부터 스냅샷 읽기가 라이브 데이터를 반환합니다.
    ///
    /// 실기에서 이 문제가 정확히 재현됐습니다: 느린 QLC SSD 대상 때문에 클론이 3시간 걸렸고,
    /// 그동안 C:에 쌓인 쓰기가 기본 섀도 저장소 한도를 넘겨 스냅샷이 붕괴했습니다. 복제 초반엔
    /// 스냅샷이 올바른 데이터를 줬지만, 검증 시점(2.5시간 후)엔 같은 스냅샷이 라이브 데이터를
    /// 반환해 대량의 가짜 불일치가 났습니다.
    ///
    /// 볼륨 크기의 20%를 확보하되 최소 40GB로 잡습니다. 긴 클론 동안의 쓰기를 견디기 위함입니다.
    /// </remarks>
    private const long MinDiffAreaBytes = 40L * 1024 * 1024 * 1024;
    private const double DiffAreaFractionOfVolume = 0.20;

    private VssSnapshotSet Create(IReadOnlyList<string> volumeGuidPaths, CancellationToken ct)
    {
        // 스냅샷을 뜨기 전에 섀도 저장소를 넉넉히 확보합니다. 이 단계가 실패해도 스냅샷 자체는
        // 만들 수 있으므로(작은 diff 영역으로), 실패는 경고만 남기고 계속합니다.
        TryEnsureDiffAreaCapacity(volumeGuidPaths);

        var factory = VssFactoryProvider.Default.GetVssFactory();
        var backup = factory.CreateVssBackupComponents();

        // StartSnapshotSet 이후 DoSnapshotSet 전에 실패하면 VSS는 "스냅샷 생성이 진행 중"인
        // 상태로 남습니다. 그러면 이후 모든 스냅샷 시도가 VSS_E_SNAPSHOT_SET_IN_PROGRESS로
        // 실패합니다 — 프로세스를 다시 띄워도, 재부팅 전까지 계속. 반드시 풀어 줘야 합니다.
        bool snapshotSetStarted = false;

        try
        {
            backup.InitializeForBackup(null);

            // Backup 컨텍스트 + NoWriters: 우리는 파일 단위 백업이 아니라 볼륨 전체 이미지를
            // 원시로 읽습니다. 애플리케이션 라이터를 부르지 않아도 파일 시스템은 일관됩니다.
            //
            // NoWriters를 지정하면 라이터를 다루는 호출(GatherWriterMetadata, PrepareForBackup,
            // BackupComplete)을 해서는 안 됩니다. 실제로 호출하면 PrepareForBackup에서
            // VSS_E_BAD_STATE가 납니다 — 컨텍스트로는 "라이터 안 씁니다"라고 해 놓고
            // 라이터에게 이벤트를 보내려 하기 때문입니다.
            backup.SetContext(VssVolumeSnapshotAttributes.NoWriters);

            // selectComponents: false — 컴포넌트 선택 없이 볼륨 전체를 뜹니다.
            // backupBootableSystemState: true — 부팅 상태를 포함한다고 알립니다.
            // VssBackupType.Full — 전체 백업.
            // partialFileSupport: false
            backup.SetBackupState(false, true, VssBackupType.Full, false);

            ct.ThrowIfCancellationRequested();

            Guid snapshotSetId = backup.StartSnapshotSet();
            snapshotSetStarted = true;

            var snapshotIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            foreach (string volume in volumeGuidPaths)
            {
                ct.ThrowIfCancellationRequested();

                // AddToSnapshotSet은 후행 백슬래시가 있는 볼륨 경로를 요구합니다.
                string normalized = volume.EndsWith('\\') ? volume : volume + '\\';

                try
                {
                    snapshotIds[volume] = backup.AddToSnapshotSet(normalized, Guid.Empty);
                    _logger.LogInformation("스냅샷 세트에 볼륨 추가: {Volume}", normalized);
                }
                catch (VssVolumeNotSupportedException)
                {
                    // FAT32(EFI 시스템 파티션)나 인식되지 않는 파일 시스템은 VSS가 지원하지 않습니다.
                    // 이런 볼륨은 원시 디스크에서 직접 복사해야 하므로, 호출자가 판단하도록
                    // 목록에서 빼고 계속 진행합니다.
                    _logger.LogWarning(
                        "볼륨 {Volume} 은(는) VSS를 지원하지 않습니다 (보통 FAT32인 EFI 파티션). " +
                        "이 볼륨은 원시 복사로 처리해야 합니다.", normalized);
                }
            }

            if (snapshotIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "스냅샷을 만들 수 있는 볼륨이 하나도 없습니다. 대상 볼륨이 모두 NTFS가 아닐 수 있습니다.");
            }

            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("스냅샷 생성 중... (볼륨 {Count}개)", snapshotIds.Count);

            // 이 호출이 실제로 파일 시스템을 잠깐 얼리고 스냅샷을 찍습니다.
            // Windows는 이 정지 구간을 10초로 제한하므로 오래 걸리지 않습니다.
            backup.DoSnapshotSet();

            var devicePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DateTime createdUtc = DateTime.UtcNow;

            foreach (var (volume, id) in snapshotIds)
            {
                var properties = backup.GetSnapshotProperties(id);
                devicePaths[volume] = properties.SnapshotDeviceObject;

                _logger.LogInformation(
                    "스냅샷 생성됨: {Volume} → {Device}", volume, properties.SnapshotDeviceObject);
            }

            snapshotSetStarted = false; // 성공했으므로 이제 VssSnapshotSet이 생명주기를 넘겨받습니다.

            return new VssSnapshotSet(backup, snapshotSetId, devicePaths, createdUtc, _logger);
        }
        catch
        {
            if (snapshotSetStarted)
            {
                try
                {
                    backup.AbortBackup();
                    _logger.LogInformation("실패한 스냅샷 세트를 중단(AbortBackup)해 VSS 상태를 해제했습니다.");
                }
                catch (Exception abortEx)
                {
                    _logger.LogWarning(abortEx,
                        "AbortBackup에 실패했습니다. 이후 스냅샷 시도가 " +
                        "\"섀도 복사본 생성이 이미 진행 중\" 오류로 실패할 수 있습니다. " +
                        "그럴 때는 VSS 서비스(VSS)를 재시작하십시오.");
                }
            }

            backup.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 스냅샷 대상 볼륨들의 섀도 저장소 최대 크기를 넉넉히 확보합니다.
    /// </summary>
    /// <remarks>
    /// 실패해도 스냅샷 자체는 만들 수 있으므로(작은 diff 영역으로), 예외를 던지지 않고
    /// 경고만 남깁니다. 다만 이게 실패하면 긴 클론에서 스냅샷이 붕괴할 위험이 커집니다.
    /// </remarks>
    private void TryEnsureDiffAreaCapacity(IReadOnlyList<string> volumeGuidPaths)
    {
        try
        {
            var factory = VssFactoryProvider.Default.GetVssFactory();
            var mgmt = factory.CreateVssSnapshotManagement();
            var diffMgmt = mgmt.GetDifferentialSoftwareSnapshotManagementInterface();

            foreach (string volume in volumeGuidPaths)
            {
                string normalized = volume.EndsWith('\\') ? volume : volume + '\\';

                long desired = ComputeDesiredDiffArea(normalized);

                try
                {
                    // 이 볼륨의 diff 영역을 같은 볼륨 위에 두고, 최대 크기를 desired로 올립니다.
                    // 이미 더 큰 연결이 있으면 VSS가 알아서 유지합니다.
                    // 세 번째 인자가 최대 크기(바이트), isVolumeSnapshotted=false로 새로 만들거나 변경.
                    diffMgmt.ChangeDiffAreaMaximumSize(normalized, normalized, desired);
                    _logger.LogInformation(
                        "볼륨 {Volume}의 섀도 저장소 최대 크기를 {Size:N0}바이트로 확보했습니다.",
                        normalized, desired);
                }
                catch (Exception ex)
                {
                    // AddDiffArea가 필요한 경우(아직 연결이 없는 볼륨)를 시도합니다.
                    try
                    {
                        diffMgmt.AddDiffArea(normalized, normalized, desired);
                        _logger.LogInformation(
                            "볼륨 {Volume}에 섀도 저장소 {Size:N0}바이트를 새로 연결했습니다.",
                            normalized, desired);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(
                            "볼륨 {Volume}의 섀도 저장소 크기를 확보하지 못했습니다 ({Err1} / {Err2}). " +
                            "긴 클론에서 스냅샷이 붕괴할 수 있습니다.",
                            normalized, ex.Message, ex2.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "섀도 저장소 관리 인터페이스를 열지 못했습니다. 기본 크기로 진행합니다.");
        }
    }

    private long ComputeDesiredDiffArea(string volumeGuidPath)
    {
        try
        {
            if (VolumeApi.GetDiskFreeSpaceEx(volumeGuidPath, out _, out ulong totalBytes, out _))
            {
                long fraction = (long)(totalBytes * DiffAreaFractionOfVolume);
                return Math.Max(MinDiffAreaBytes, fraction);
            }
        }
        catch
        {
            // 볼륨 크기를 모르면 최소값을 씁니다.
        }

        return MinDiffAreaBytes;
    }
}

/// <summary>생성된 VSS 스냅샷 세트. Dispose하면 스냅샷이 삭제됩니다.</summary>
[SupportedOSPlatform("windows")]
internal sealed class VssSnapshotSet(
    IVssBackupComponents backup,
    Guid snapshotSetId,
    IReadOnlyDictionary<string, string> devicePaths,
    DateTime createdUtc,
    ILogger logger) : ISnapshotSet
{
    private bool _disposed;

    public DateTime CreatedUtc => createdUtc;

    public IReadOnlyDictionary<string, string> SnapshotDevicePaths => devicePaths;

    public IBlockDevice OpenSnapshotRead(string originalVolumeGuidPath)
    {
        if (!devicePaths.TryGetValue(originalVolumeGuidPath, out string? devicePath))
        {
            throw new KeyNotFoundException(
                $"볼륨 {originalVolumeGuidPath} 의 스냅샷이 없습니다.");
        }

        // SnapshotDeviceObject는 \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN 형식이며
        // 일반 블록 장치처럼 CreateFile로 열어 원시로 읽을 수 있습니다.
        return RawDiskDevice.OpenRead(devicePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // NoWriters 컨텍스트이므로 BackupComplete/FreeWriterMetadata는 호출하지 않습니다.
        // 라이터를 쓰지 않는 세션에서 이 호출들은 VSS_E_BAD_STATE를 냅니다.
        try
        {
            // 스냅샷을 지우지 않으면 섀도 저장소 공간을 계속 차지합니다.
            backup.DeleteSnapshotSet(snapshotSetId, forceDelete: true);
            logger.LogInformation("스냅샷 세트를 삭제했습니다.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "스냅샷 세트 삭제에 실패했습니다. vssadmin으로 수동 삭제가 필요할 수 있습니다.");
        }

        backup.Dispose();
    }
}
