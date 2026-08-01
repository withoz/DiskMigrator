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

    /// <summary>
    /// 섀도 저장소(diff 영역)를 둘 볼륨. null이면 스냅샷 대상 볼륨 자신에 둡니다.
    /// </summary>
    /// <remarks>
    /// diff 영역을 스냅샷 대상 볼륨 자신에 두면, VSS가 그 diff 영역을 스냅샷에서 제외하므로
    /// diff 영역이 차지한 블록을 스냅샷에서 읽을 때 시간에 따라 값이 바뀝니다(드리프트).
    /// diff 영역을 다른 볼륨에 두면 이 문제가 사라집니다. 실행 중 시스템 디스크를 복제할 때
    /// 여유 있는 다른 디스크의 볼륨 경로를 지정하십시오.
    /// </remarks>
    public string? DiffAreaVolumeOverride { get; set; }

    public bool IsAvailable => Diagnose().Available;

    /// <summary>VSS 사용 가능 여부와, 불가능하면 그 이유(사용자에게 보여줄 문구).</summary>
    /// <param name="Available">스냅샷을 만들 수 있는지.</param>
    /// <param name="Reason">불가능한 이유. 가능하면 null.</param>
    /// <param name="Hint">사용자가 해볼 수 있는 조치. 없으면 null.</param>
    public sealed record Availability(bool Available, string? Reason, string? Hint);

    /// <summary>
    /// VSS를 쓸 수 있는지 진단합니다 — <b>왜</b> 안 되는지까지 알려줍니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 <see cref="IsAvailable"/>이 참/거짓만 돌려주어, 화면에서는 체크박스가 회색으로
    /// 잠기기만 하고 사용자는 원인도 해결책도 알 수 없었습니다(실기에서 실제로 막혔습니다).
    /// 두 가지를 나누어 봅니다:
    /// <list type="number">
    /// <item><b>AlphaVSS 네이티브 어셈블리 로드</b> — 실패하면 VC++ 재배포 패키지 누락이 흔합니다.</item>
    /// <item><b>VSS 서비스 상태</b> — "사용 안 함(Disabled)"이면 어셈블리가 멀쩡해도 스냅샷 생성이
    ///   실패합니다. 중지(Stopped)는 정상입니다 — 수동 시작이라 필요할 때 Windows가 켭니다.</item>
    /// </list>
    /// 진단만 하고 서비스를 건드리지는 않습니다(시스템 설정 변경은 사용자 몫).
    /// </remarks>
    public Availability Diagnose()
    {
        try
        {
            // 플랫폼별 네이티브 어셈블리를 실제로 로드해 봐야 사용 가능 여부를 알 수 있습니다.
            _ = VssFactoryProvider.Default.GetVssFactory();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VSS 라이브러리를 로드하지 못했습니다.");

            // 가장 흔한 원인을 직접 확인해 정확히 짚어 줍니다: AlphaVSS.x64는 C++/CLI 혼합
            // 어셈블리라 Visual C++ 런타임(msvcp140.dll)이 있어야 로드됩니다. 이 DLL은 앱에
            // 동봉되지 않고 PC에 설치된 것을 쓰므로, 없는 PC에서는 VSS가 통째로 잠깁니다
            // (실기에서 확인). 있으면 원래 예외 메시지를 그대로 보여 줍니다.
            bool missingRuntime = !VcRuntimeInstalled();
            string detail = ex.InnerException?.Message ?? ex.Message;

            return new(false,
                missingRuntime
                    ? Core.Localization.L.T(
                        "이 PC에 Microsoft Visual C++ 재배포 패키지(x64)가 없어 VSS 라이브러리를 불러올 수 없습니다.",
                        "The Microsoft Visual C++ Redistributable (x64) is missing on this PC, so the VSS library cannot be loaded.")
                    : Core.Localization.L.T(
                        $"VSS 라이브러리를 불러오지 못했습니다: {detail}",
                        $"Could not load the VSS library: {detail}"),
                missingRuntime
                    ? Core.Localization.L.T(
                        "Microsoft 사이트에서 'Visual C++ 재배포 가능 패키지(x64)'를 설치한 뒤 '새로고침'을 누르십시오.",
                        "Install the 'Visual C++ Redistributable (x64)' from Microsoft, then press Refresh.")
                    : Core.Localization.L.T(
                        "앱을 관리자 권한으로 다시 실행해 보고, 계속되면 로그를 첨부해 문의하십시오.",
                        "Try running the app as administrator again; if it persists, please report with the log."));
        }

        // 서비스가 '사용 안 함'이면 스냅샷 생성 시점에 실패합니다 — 미리 알려 줍니다.
        if (IsServiceDisabled("VSS", out string? svcNote))
        {
            return new(false,
                Core.Localization.L.T(
                    "Windows의 볼륨 섀도 복사본(VSS) 서비스가 '사용 안 함'으로 설정돼 있습니다.",
                    "The Windows Volume Shadow Copy (VSS) service is set to Disabled."),
                Core.Localization.L.T(
                    "services.msc에서 'Volume Shadow Copy' 서비스의 시작 유형을 '수동'으로 바꾸십시오.",
                    "In services.msc, set the 'Volume Shadow Copy' service startup type to Manual."));
        }
        if (svcNote is not null) _logger.LogInformation("VSS 서비스 상태 확인: {Note}", svcNote);

        return new(true, null, null);
    }

    /// <summary>
    /// AlphaVSS의 혼합 어셈블리가 요구하는 <c>vcruntime140.dll</c>을 찾을 수 있는지 —
    /// <b>앱 폴더(동봉본)</b> 또는 시스템 폴더(재배포 패키지) 어느 쪽이든 있으면 true.
    /// </summary>
    /// <remarks>
    /// v1.3.1부터 이 DLL을 앱에 동봉하므로 정상 배포에서는 항상 true입니다. 사용자가 파일을
    /// 일부만 복사했거나 백신이 격리한 경우를 잡기 위해 남겨 둡니다.
    /// </remarks>
    private static bool VcRuntimeInstalled()
    {
        try
        {
            string? appDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (appDir is not null && File.Exists(Path.Combine(appDir, "vcruntime140.dll")))
                return true;

            // 단일 exe는 추출 폴더에서 실행되므로 어셈블리 위치도 확인합니다.
            string? asmDir = Path.GetDirectoryName(typeof(VssSnapshotProvider).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir) && File.Exists(Path.Combine(asmDir, "vcruntime140.dll")))
                return true;

            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(sys, "vcruntime140.dll"));
        }
        catch
        {
            return true;   // 확인 불가면 단정하지 않습니다(원래 예외 메시지를 보여 줍니다).
        }
    }

    /// <summary>서비스가 '사용 안 함(Disabled)'인지. 조회 실패는 false(알 수 없음)로 넘깁니다.</summary>
    private static bool IsServiceDisabled(string serviceName, out string? note)
    {
        note = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("Start") is not int start) return false;

            note = $"{serviceName} Start={start}";
            return start == 4;   // SERVICE_DISABLED
        }
        catch
        {
            // 조회 자체가 안 되면 판단하지 않습니다 — 실제 생성 시점에 확인됩니다.
            return false;
        }
    }

    public Task<ISnapshotSet> CreateSnapshotSetAsync(
        IReadOnlyList<string> volumeGuidPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(volumeGuidPaths);

        if (volumeGuidPaths.Count == 0)
        {
            throw new ArgumentException(DiskMigrator.Core.Localization.L.T("스냅샷을 만들 볼륨이 없습니다.", "There are no volumes to snapshot."), nameof(volumeGuidPaths));
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

                // diff 영역을 둘 볼륨: 지정이 있으면 그쪽, 없으면 대상 볼륨 자신.
                string diffVolume = DiffAreaVolumeOverride is { } ov
                    ? (ov.EndsWith('\\') ? ov : ov + '\\')
                    : normalized;

                long desired = ComputeDesiredDiffArea(normalized);

                try
                {
                    // 이 볼륨의 diff 영역을 diffVolume 위에 두고, 최대 크기를 desired로 올립니다.
                    // 세 번째 인자가 최대 크기(바이트).
                    diffMgmt.ChangeDiffAreaMaximumSize(normalized, diffVolume, desired);
                    _logger.LogInformation(
                        "볼륨 {Volume}의 섀도 저장소를 {DiffVol}에 최대 {Size:N0}바이트로 요청했습니다.",
                        normalized, diffVolume, desired);
                }
                catch (Exception ex)
                {
                    // AddDiffArea가 필요한 경우(아직 연결이 없는 볼륨)를 시도합니다.
                    try
                    {
                        diffMgmt.AddDiffArea(normalized, diffVolume, desired);
                        _logger.LogInformation(
                            "볼륨 {Volume}의 섀도 저장소 {Size:N0}바이트를 {DiffVol}에 새로 연결 요청했습니다.",
                            normalized, desired, diffVolume);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(
                            "볼륨 {Volume}의 섀도 저장소 크기를 확보하지 못했습니다 ({Err1} / {Err2}). " +
                            "긴 클론에서 스냅샷이 붕괴할 수 있습니다.",
                            normalized, ex.Message, ex2.Message);
                    }
                }

                // VSS가 실제로 확보한 diff 영역 크기를 되물어 로그에 남깁니다. 요청값과 실제가
                // 다를 수 있고(볼륨이 작으면 캡됨), 이 값이 클론 중 쓰기량보다 작으면 스냅샷이
                // 붕괴합니다. 진단에 핵심 정보라 실패해도 무시하고 남깁니다.
                try
                {
                    foreach (var area in diffMgmt.QueryDiffAreasForVolume(normalized))
                    {
                        _logger.LogInformation(
                            "  실제 섀도 저장소: 볼륨 {Vol} / 저장위치 {Store} / 최대 {Max:N0} / 할당 {Alloc:N0} / 사용 {Used:N0}바이트",
                            area.VolumeName, area.DiffAreaVolumeName,
                            area.MaximumDiffSpace, area.AllocatedDiffSpace, area.UsedDiffSpace);
                    }
                }
                catch { /* 진단용이므로 실패는 무시 */ }
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
