using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// 대상 디스크를 쓰기 전에 오프라인으로 내리고, Dispose 시 원래 상태로 되돌립니다.
/// </summary>
/// <remarks>
/// 이게 없으면 무슨 일이 생기는지 실기에서 확인했습니다: 클론이 대상에 파티션 테이블을
/// 쓰는 순간 대상에는 원본과 똑같은 NTFS 볼륨이 생깁니다. 대상이 온라인이면 Windows가
/// 그 볼륨을 자동 마운트하고 NTFS 로그를 재생하며, 클론이 도는 내내 우리가 복사해 넣은
/// 데이터 위에 계속 씁니다. 실제로 931GB 클론에서 파일 시스템 영역에만 617,692건의
/// 불일치가 생겼고, 파일 시스템이 없는 MSR 영역에는 한 건도 없었습니다.
///
/// 디스크를 오프라인으로 내리면 Windows가 그 디스크의 볼륨을 아예 표면화하지 않으므로
/// 자동 마운트도, 로그 재생도 일어나지 않습니다. 원시 디스크 쓰기는 그대로 됩니다.
/// 상용 클론 도구들이 모두 이렇게 합니다.
///
/// "볼륨 잠금"(<see cref="VolumeLock"/>)만으로는 부족합니다. 잠금은 <b>이미 존재하는</b>
/// 볼륨만 막지, 클론 중에 <b>새로 생기는</b> 볼륨은 막지 못합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DiskOfflineScope : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _diskNumber;
    private readonly ILogger _logger;
    private readonly bool _wasOffline;
    private readonly bool _wasReadOnly;
    private bool _disposed;

    private DiskOfflineScope(
        SafeFileHandle handle, int diskNumber, bool wasOffline, bool wasReadOnly, ILogger logger)
    {
        _handle = handle;
        _diskNumber = diskNumber;
        _wasOffline = wasOffline;
        _wasReadOnly = wasReadOnly;
        _logger = logger;
    }

    /// <summary>
    /// 디스크를 오프라인으로 내리고 읽기 전용 속성을 해제합니다.
    /// </summary>
    /// <remarks>
    /// 반환된 객체는 클론이 끝날 때까지 살아 있어야 합니다. 핸들이 닫히면 속성 변경의
    /// 근거가 사라질 수 있으므로, 이 객체가 디스크 핸들을 계속 붙들고 있습니다.
    /// </remarks>
    public static DiskOfflineScope Take(DiskInfo disk, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var log = logger ?? NullLogger.Instance;

        var handle = NativeMethods.CreateFile(
            disk.DevicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error,
                $"{disk.DevicePath} 을(를) 속성 변경용으로 열지 못했습니다.");
        }

        try
        {
            var (wasOffline, wasReadOnly) = GetAttributes(handle);

            log.LogInformation(
                "대상 디스크 {Disk} 현재 속성: 오프라인={Offline}, 읽기전용={ReadOnly}",
                disk.DeviceNumber, wasOffline, wasReadOnly);

            // 오프라인으로 내리고, 혹시 읽기 전용이면 해제합니다.
            SetAttributes(
                handle,
                attributes: NativeMethods.DISK_ATTRIBUTE_OFFLINE,
                mask: NativeMethods.DISK_ATTRIBUTE_OFFLINE | NativeMethods.DISK_ATTRIBUTE_READ_ONLY);

            // 속성 변경을 실제 상태에 반영시킵니다.
            DiskIoctl.TryControl(handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES);

            log.LogInformation("대상 디스크 {Disk} 을(를) 오프라인으로 내렸습니다.", disk.DeviceNumber);

            return new DiskOfflineScope(handle, disk.DeviceNumber, wasOffline, wasReadOnly, log);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static (bool Offline, bool ReadOnly) GetAttributes(SafeFileHandle handle)
    {
        if (!DiskIoctl.TryQuery<GET_DISK_ATTRIBUTES>(
                handle, NativeMethods.IOCTL_DISK_GET_DISK_ATTRIBUTES, out var attrs))
        {
            // 조회 실패 시 "온라인·쓰기 가능"이었다고 가정합니다. Dispose에서 다시 온라인으로
            // 올리려 시도하게 되며, 이미 온라인이면 무해합니다.
            return (false, false);
        }

        return (
            (attrs.Attributes & NativeMethods.DISK_ATTRIBUTE_OFFLINE) != 0,
            (attrs.Attributes & NativeMethods.DISK_ATTRIBUTE_READ_ONLY) != 0);
    }

    private static unsafe void SetAttributes(SafeFileHandle handle, ulong attributes, ulong mask)
    {
        var request = new SET_DISK_ATTRIBUTES
        {
            Version = (uint)Unsafe.SizeOf<SET_DISK_ATTRIBUTES>(),
            Persist = 0, // 재부팅 후에는 유지하지 않습니다.
            Attributes = attributes,
            AttributesMask = mask,
        };

        int size = Unsafe.SizeOf<SET_DISK_ATTRIBUTES>();

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                (nint)(&request), (uint)size, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "디스크 속성 변경(오프라인 전환)에 실패했습니다.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // 원래 오프라인이 아니었던 디스크만 다시 온라인으로 올립니다.
            // 사용자가 일부러 오프라인 상태로 둔 디스크를 함부로 온라인으로 바꾸지 않습니다.
            if (!_wasOffline)
            {
                SetAttributes(
                    _handle,
                    attributes: 0,
                    mask: NativeMethods.DISK_ATTRIBUTE_OFFLINE);

                DiskIoctl.TryControl(_handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES);

                _logger.LogInformation("대상 디스크 {Disk} 을(를) 다시 온라인으로 올렸습니다.", _diskNumber);
            }

            // 읽기 전용 속성은 우리가 해제한 경우에만 복원합니다.
            if (_wasReadOnly)
            {
                SetAttributes(
                    _handle,
                    attributes: NativeMethods.DISK_ATTRIBUTE_READ_ONLY,
                    mask: NativeMethods.DISK_ATTRIBUTE_READ_ONLY);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "대상 디스크 {Disk} 속성 복원에 실패했습니다. 디스크 관리에서 수동으로 " +
                "온라인 전환이 필요할 수 있습니다.", _diskNumber);
        }
        finally
        {
            _handle.Dispose();
        }
    }
}
