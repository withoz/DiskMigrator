using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// \\.\PhysicalDriveN 또는 \\?\Volume{GUID} 를 여는 원시 블록 장치.
/// </summary>
/// <remarks>
/// FILE_FLAG_NO_BUFFERING으로 열기 때문에 오프셋·길이·<b>버퍼 주소</b>가 모두 섹터 정렬이어야
/// 합니다. 정렬되지 않은 버퍼를 넘기면 ERROR_INVALID_PARAMETER가 납니다 —
/// 호출자는 <see cref="Core.Util.AlignedBuffer"/>를 써야 합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RawDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    public string Id { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite { get; }

    private RawDiskDevice(SafeFileHandle handle, string id, long length, int sectorSize, bool canWrite)
    {
        _handle = handle;
        Id = id;
        Length = length;
        SectorSize = sectorSize;
        CanWrite = canWrite;
    }

    /// <summary>장치를 읽기 전용으로 엽니다. 다른 프로세스의 사용을 막지 않습니다.</summary>
    public static RawDiskDevice OpenRead(string devicePath)
    {
        var handle = Open(
            devicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            NativeMethods.FILE_FLAG_NO_BUFFERING);

        var (length, sectorSize) = QueryGeometry(handle, devicePath);
        return new RawDiskDevice(handle, devicePath, length, sectorSize, canWrite: false);
    }

    /// <summary>
    /// 장치를 읽기/쓰기로 엽니다.
    /// </summary>
    /// <remarks>
    /// 이 장치 위의 볼륨들이 먼저 잠기고 디스마운트되어 있어야 합니다
    /// (<see cref="VolumeLock"/>). 그렇지 않으면 Vista 이후 Windows가 섹터 쓰기를 거부합니다.
    /// </remarks>
    public static RawDiskDevice OpenWrite(string devicePath)
    {
        var handle = Open(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_WRITE_THROUGH);

        // 볼륨 경계를 넘는 원시 쓰기를 허용합니다. 실패해도 치명적이지 않으므로 무시합니다
        // (물리 디스크 핸들에서는 지원하지 않는 경우가 있습니다).
        DiskIoctl.TryControl(handle, NativeMethods.FSCTL_ALLOW_EXTENDED_DASD_IO);

        var (length, sectorSize) = QueryGeometry(handle, devicePath);
        return new RawDiskDevice(handle, devicePath, length, sectorSize, canWrite: true);
    }

    private static SafeFileHandle Open(string devicePath, uint access, uint share, uint flags)
    {
        var handle = NativeMethods.CreateFile(
            devicePath, access, share, 0, NativeMethods.OPEN_EXISTING,
            flags | NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();

            throw error switch
            {
                NativeMethods.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                    $"{devicePath} 접근이 거부되었습니다. 관리자 권한으로 실행했는지 확인하십시오."),

                NativeMethods.ERROR_SHARING_VIOLATION => new IOException(
                    $"{devicePath} 을(를) 다른 프로그램이 사용 중입니다. " +
                    "이 디스크의 볼륨을 사용하는 프로그램을 모두 닫아야 합니다."),

                NativeMethods.ERROR_DEV_NOT_EXIST or 2 => new IOException(
                    $"{devicePath} 장치가 존재하지 않습니다. 연결이 끊겼을 수 있습니다."),

                _ => new Win32Exception(error, $"{devicePath} 을(를) 열지 못했습니다."),
            };
        }

        return handle;
    }

    /// <summary>
    /// 장치의 크기와 논리 섹터 크기를 알아냅니다.
    /// </summary>
    /// <remarks>
    /// 이 메서드는 두 종류의 장치를 모두 다뤄야 합니다:
    ///
    /// - 물리 디스크(\\.\PhysicalDriveN) — IOCTL_DISK_GET_DRIVE_GEOMETRY_EX가 통합니다.
    /// - 볼륨 장치(VSS 섀도 복사본 \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN) —
    ///   GEOMETRY_EX를 지원하지 않아 실패합니다.
    ///
    /// 섀도 복사본에서 이게 실패하면 VSS 스냅샷을 만들어 놓고도 읽지 못해,
    /// 살아 있는 볼륨을 원시로 읽는 쪽으로 조용히 되돌아가게 됩니다. 그러면
    /// 스냅샷을 쓴다고 믿는 사용자가 일관성 없는 클론을 받습니다.
    /// 그래서 여러 IOCTL을 순서대로 시도합니다.
    /// </remarks>
    private static (long Length, int SectorSize) QueryGeometry(SafeFileHandle handle, string devicePath)
    {
        try
        {
            long? length = null;
            int? sectorSize = null;

            // 1) 물리 디스크 경로.
            if (DiskIoctl.TryQuery<DISK_GEOMETRY_EX>(
                    handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, out var geometryEx))
            {
                sectorSize = (int)geometryEx.Geometry.BytesPerSector;
                length = geometryEx.DiskSize;
            }

            // 2) 구형 지오메트리 — 볼륨 장치에서도 동작합니다. 섹터 크기만 취합니다
            //    (Cylinders 기반 크기 계산은 실제 크기보다 작게 나올 수 있어 쓰지 않습니다).
            if (sectorSize is null or <= 0 &&
                DiskIoctl.TryQuery<DISK_GEOMETRY>(
                    handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY, out var geometry))
            {
                sectorSize = (int)geometry.BytesPerSector;
            }

            // 3) 저장장치 정렬 속성 — 위 둘이 모두 실패했을 때의 마지막 수단.
            if (sectorSize is null or <= 0)
            {
                sectorSize = StorageDescriptorReader.ReadLogicalSectorSize(handle);
            }

            // 4) 크기: GET_LENGTH_INFO가 가장 정확하고 물리 디스크·볼륨 모두에서 동작합니다.
            if (DiskIoctl.TryQuery<GET_LENGTH_INFORMATION>(
                    handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO, out var lengthInfo))
            {
                length = lengthInfo.Length;
            }

            if (sectorSize is null or <= 0)
            {
                throw new IOException(
                    $"{devicePath}: 섹터 크기를 알아내지 못했습니다 (지오메트리 IOCTL이 모두 실패).");
            }

            if (length is null or <= 0)
            {
                throw new IOException($"{devicePath}: 장치 크기를 알아내지 못했습니다.");
            }

            return (length.Value, sectorSize.Value);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        ValidateAlignment(offset, buffer.Length);

        if (offset >= Length) return 0;

        // 장치 끝을 넘어가는 요청은 남은 만큼으로 줄입니다. NO_BUFFERING에서는 요청 길이도
        // 섹터 배수여야 하므로 내림 정렬합니다.
        long remaining = Length - offset;
        int toRead = (int)Math.Min(buffer.Length, remaining);
        toRead -= toRead % SectorSize;

        if (toRead == 0) return 0;

        try
        {
            int total = 0;
            while (total < toRead)
            {
                int read = RandomAccess.Read(_handle, buffer.Slice(total, toRead - total), offset + total);
                if (read == 0) break;
                total += read;
            }

            return total;
        }
        catch (IOException ex)
        {
            throw new IOException(DescribeIoFailure(offset, ex), ex);
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException($"{Id} 은(는) 읽기 전용으로 열렸습니다.");
        }

        ValidateAlignment(offset, buffer.Length);

        if (offset + buffer.Length > Length)
        {
            throw new IOException(
                $"{Id}: 장치 끝을 넘어서 쓰려 합니다 (오프셋 {offset:N0} + {buffer.Length:N0} > {Length:N0}).");
        }

        try
        {
            RandomAccess.Write(_handle, buffer, offset);
        }
        catch (IOException ex)
        {
            throw new IOException(DescribeIoFailure(offset, ex), ex);
        }
    }

    public void Flush()
    {
        // FILE_FLAG_WRITE_THROUGH로 열었으므로 각 쓰기는 이미 장치까지 내려갑니다.
        // FlushFileBuffers는 장치 캐시까지 밀어내는 추가 보증입니다.
        if (CanWrite)
        {
            RandomAccess.FlushToDisk(_handle);
        }
    }

    public void Dispose() => _handle.Dispose();

    private void ValidateAlignment(long offset, int length)
    {
        if (offset % SectorSize != 0)
        {
            throw new ArgumentException(
                $"{Id}: 오프셋 {offset}이(가) 섹터 크기 {SectorSize}에 정렬되지 않았습니다.", nameof(offset));
        }

        if (length % SectorSize != 0)
        {
            throw new ArgumentException(
                $"{Id}: 길이 {length}이(가) 섹터 크기 {SectorSize}의 배수가 아닙니다.", nameof(length));
        }
    }

    /// <summary>
    /// Win32 오류를 사용자가 이해할 수 있는 설명으로 바꿉니다.
    /// 불량 섹터인지, 케이블이 빠진 건지, 쓰기 방지인지 구분되어야 대응이 가능합니다.
    /// </summary>
    private string DescribeIoFailure(long offset, IOException ex)
    {
        int code = ex.HResult & 0xFFFF;

        string reason = code switch
        {
            NativeMethods.ERROR_CRC =>
                "데이터 오류(CRC) — 이 위치는 불량 섹터일 가능성이 높습니다.",
            NativeMethods.ERROR_SECTOR_NOT_FOUND =>
                "섹터를 찾을 수 없습니다 — 매체 손상일 가능성이 높습니다.",
            NativeMethods.ERROR_NOT_READY =>
                "장치가 준비되지 않았습니다 — 연결이 끊겼거나 스핀업 중일 수 있습니다.",
            NativeMethods.ERROR_WRITE_PROTECT =>
                "장치가 쓰기 방지 상태입니다.",
            NativeMethods.ERROR_DEV_NOT_EXIST =>
                "장치가 사라졌습니다 — 케이블이나 USB 연결을 확인하십시오.",
            NativeMethods.ERROR_INVALID_PARAMETER =>
                "잘못된 매개변수 — 섹터 정렬 요구를 위반했을 수 있습니다.",
            _ => ex.Message,
        };

        long sector = offset / SectorSize;
        return $"{Id}: 오프셋 {offset:N0} (섹터 {sector:N0}) 에서 I/O 실패 — {reason}";
    }
}
