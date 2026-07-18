using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Interop;

/// <summary>
/// 원시 디스크 접근에 필요한 Win32 API 선언.
/// </summary>
/// <remarks>
/// IOCTL 상수는 CTL_CODE 매크로로 계산된 값입니다:
/// (DeviceType &lt;&lt; 16) | (Access &lt;&lt; 14) | (Function &lt;&lt; 2) | Method
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    // --- CreateFile 접근/공유/플래그 -----------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;

    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;

    internal const uint OPEN_EXISTING = 3;

    /// <summary>시스템 캐시를 우회합니다. 오프셋·길이·버퍼 주소가 모두 섹터 정렬이어야 합니다.</summary>
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    /// <summary>쓰기를 장치 캐시까지 밀어냅니다. 중간에 전원이 나가도 손실 범위를 줄입니다.</summary>
    internal const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // --- IOCTL ---------------------------------------------------------------

    /// <summary>디스크의 정확한 바이트 크기. 파일 크기 조회로는 얻을 수 없습니다.</summary>
    internal const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    /// <summary>지오메트리 + 논리 섹터 크기. 물리 디스크 전용 — 볼륨 핸들에서는 실패합니다.</summary>
    internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

    /// <summary>
    /// 구형 지오메트리 조회. VSS 섀도 복사본 같은 볼륨 장치에서도 동작하므로
    /// GEOMETRY_EX가 실패할 때의 대체 수단입니다.
    /// </summary>
    internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;

    /// <summary>파티션 테이블(MBR/GPT)과 각 파티션 항목.</summary>
    internal const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;

    /// <summary>쓰기 후 Windows가 파티션 테이블을 다시 읽게 합니다.</summary>
    internal const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    /// <summary>디스크가 쓰기 가능한지(쓰기 방지 스위치 등).</summary>
    internal const uint IOCTL_DISK_IS_WRITABLE = 0x00070024;

    /// <summary>버스 종류, 착탈 여부, 물리 섹터 크기 등 저장장치 속성.</summary>
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>볼륨이 어느 물리 디스크의 어느 오프셋에 있는지. 시스템 디스크 판별의 근거입니다.</summary>
    internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    /// <summary>
    /// 디스크 속성(오프라인/읽기전용)을 변경합니다.
    /// </summary>
    /// <remarks>
    /// 대상 디스크를 쓰기 전에 오프라인으로 내리는 데 씁니다. 오프라인이 아니면,
    /// 클론이 파티션 테이블을 쓰는 순간 Windows가 새로 생긴 볼륨을 자동 마운트하고
    /// NTFS 로그를 재생하며 우리가 쓴 데이터를 덮어씁니다.
    /// </remarks>
    internal const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;

    /// <summary>현재 디스크 속성을 조회합니다.</summary>
    internal const uint IOCTL_DISK_GET_DISK_ATTRIBUTES = 0x000700F0;

    internal const ulong DISK_ATTRIBUTE_OFFLINE = 0x0000000000000001;
    internal const ulong DISK_ATTRIBUTE_READ_ONLY = 0x0000000000000002;

    // --- FSCTL ---------------------------------------------------------------

    /// <summary>볼륨을 잠급니다. 다른 핸들이 열려 있으면 실패합니다.</summary>
    internal const uint FSCTL_LOCK_VOLUME = 0x00090018;

    internal const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;

    /// <summary>볼륨을 마운트 해제합니다. 이후 파일 시스템이 접근하지 않습니다.</summary>
    internal const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    /// <summary>
    /// 볼륨 경계를 넘는 원시 I/O를 허용합니다.
    /// 이걸 하지 않으면 마운트된 볼륨 영역의 섹터 쓰기가 조용히 거부될 수 있습니다.
    /// </summary>
    internal const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

    // --- Win32 오류 코드 ------------------------------------------------------

    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_WRITE_PROTECT = 19;
    internal const int ERROR_CRC = 23;
    internal const int ERROR_SECTOR_NOT_FOUND = 27;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_DEV_NOT_EXIST = 55;

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);
}
