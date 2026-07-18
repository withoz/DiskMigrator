using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// 대상 디스크 위의 모든 볼륨을 잠그고 마운트 해제합니다. Dispose하면 해제됩니다.
/// </summary>
/// <remarks>
/// Windows Vista 이후로는 마운트된 볼륨이 차지하는 영역에 원시 섹터 쓰기를 하면
/// 조용히 거부되거나 데이터가 깨집니다. 반드시 FSCTL_LOCK_VOLUME으로 잠그고
/// FSCTL_DISMOUNT_VOLUME으로 마운트를 해제한 뒤에 써야 합니다.
///
/// 디스마운트 효과는 <b>핸들이 열려 있는 동안만</b> 유지됩니다. 그래서 이 객체는
/// 클론이 끝날 때까지 핸들을 붙들고 있어야 하며, 절대 먼저 Dispose하면 안 됩니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VolumeLock : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>잠근 볼륨 경로 목록.</summary>
    public IReadOnlyList<string> LockedVolumes { get; }

    private VolumeLock(List<SafeFileHandle> handles, List<string> lockedVolumes, ILogger logger)
    {
        _handles = handles;
        LockedVolumes = lockedVolumes;
        _logger = logger;
    }

    /// <summary>
    /// 디스크의 모든 볼륨을 잠급니다. 하나라도 실패하면 이미 잠근 것들을 되돌리고 예외를 던집니다.
    /// </summary>
    /// <param name="disk">잠글 디스크.</param>
    /// <param name="retryCount">잠금 재시도 횟수. 백신·인덱서가 잠깐 볼륨을 붙잡는 일이 흔합니다.</param>
    /// <param name="retryDelay">재시도 간격.</param>
    /// <param name="logger">진단 로그.</param>
    public static VolumeLock LockDisk(
        DiskInfo disk,
        int retryCount = 5,
        TimeSpan? retryDelay = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var log = logger ?? NullLogger.Instance;
        var delay = retryDelay ?? TimeSpan.FromSeconds(1);
        var handles = new List<SafeFileHandle>();
        var locked = new List<string>();

        try
        {
            foreach (var partition in disk.Partitions)
            {
                string? path = ResolveVolumePath(partition);
                if (path is null)
                {
                    // 마운트되지 않은 파티션(예: MSR, 복구 파티션)은 파일 시스템이 붙잡고 있지
                    // 않으므로 잠글 대상이 아닙니다.
                    log.LogDebug("파티션 {Number}는 마운트되어 있지 않아 잠금을 건너뜁니다.", partition.Number);
                    continue;
                }

                var handle = LockOne(path, retryCount, delay, log);
                handles.Add(handle);
                locked.Add(path);
            }

            log.LogInformation(
                "디스크 [{Disk}] 의 볼륨 {Count}개를 잠그고 마운트 해제했습니다.",
                disk.DeviceNumber, locked.Count);

            return new VolumeLock(handles, locked, log);
        }
        catch
        {
            // 일부만 잠긴 상태로 남기면 사용자의 볼륨이 계속 마운트 해제된 채로 있게 됩니다.
            foreach (var handle in handles)
            {
                TryUnlock(handle, log);
                handle.Dispose();
            }
            throw;
        }
    }

    private static SafeFileHandle LockOne(string volumePath, int retryCount, TimeSpan delay, ILogger log)
    {
        var handle = NativeMethods.CreateFile(
            volumePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            0);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"볼륨 {volumePath} 을(를) 열지 못했습니다.");
        }

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (DiskIoctl.TryControl(handle, NativeMethods.FSCTL_LOCK_VOLUME))
                {
                    break;
                }

                int error = Marshal.GetLastWin32Error();

                if (attempt >= retryCount)
                {
                    throw new IOException(
                        $"볼륨 {volumePath} 을(를) 잠그지 못했습니다 (Win32 오류 {error}). " +
                        "이 볼륨의 파일을 열어 둔 프로그램이 있습니다. 탐색기 창, 백신 검사, " +
                        "열려 있는 문서를 모두 닫고 다시 시도하십시오.");
                }

                log.LogDebug(
                    "볼륨 {Volume} 잠금 재시도 {Attempt}/{Total} (Win32 오류 {Error})",
                    volumePath, attempt + 1, retryCount, error);

                Thread.Sleep(delay);
            }

            // 잠금만으로는 부족합니다. 마운트를 해제해야 파일 시스템이 캐시를 버리고
            // 우리가 쓴 내용과 충돌하지 않습니다.
            DiskIoctl.Control(handle, NativeMethods.FSCTL_DISMOUNT_VOLUME, $"볼륨 {volumePath} 마운트 해제");

            log.LogInformation("볼륨 {Volume} 잠금 및 마운트 해제 완료.", volumePath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// CreateFile에 넘길 볼륨 경로를 만듭니다.
    /// </summary>
    /// <remarks>
    /// CreateFile은 <b>후행 백슬래시가 없는</b> 볼륨 경로만 받습니다. WMI가 주는
    /// \\?\Volume{GUID}\ 를 그대로 넘기면 실패하므로 반드시 잘라내야 합니다.
    /// </remarks>
    private static string? ResolveVolumePath(PartitionInfo partition)
    {
        if (!string.IsNullOrEmpty(partition.VolumeGuidPath))
        {
            return partition.VolumeGuidPath.TrimEnd('\\');
        }

        if (!string.IsNullOrEmpty(partition.DriveLetter))
        {
            return $@"\\.\{partition.DriveLetter}:";
        }

        return null;
    }

    private static void TryUnlock(SafeFileHandle handle, ILogger log)
    {
        try
        {
            DiskIoctl.TryControl(handle, NativeMethods.FSCTL_UNLOCK_VOLUME);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "볼륨 잠금 해제에 실패했습니다.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var handle in _handles)
        {
            TryUnlock(handle, _logger);
            handle.Dispose();
        }

        _handles.Clear();
        _logger.LogInformation("볼륨 잠금을 모두 해제했습니다.");
    }
}
