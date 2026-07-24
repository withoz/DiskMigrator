using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// VHDX 가상 디스크를 만들거나 열어 <b>물리 디스크로 부착</b>합니다(Windows virtdisk API).
/// </summary>
/// <remarks>
/// 이미지 백업/복원의 토대입니다. 부착하면 이 VHDX가 <c>\\.\PhysicalDriveN</c>으로 나타나므로,
/// 기존 <see cref="RawDiskDevice"/>로 그대로 읽고 쓸 수 있어 클론 엔진을 재사용할 수 있습니다:
/// <list type="bullet">
/// <item>백업 = 원본 디스크 → (부착된 새 VHDX)에 섹터 복제</item>
/// <item>복원 = (부착된 기존 VHDX) → 대상 디스크에 섹터 복제</item>
/// </list>
/// 동적(확장) VHDX라 <b>실제로 쓴 블록만 파일에 할당</b>됩니다 — 스마트 클론과 만나면 사용 영역만
/// 저장돼 이미지가 작아집니다. <see cref="Dispose"/>가 부착을 해제하고 핸들을 닫습니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VirtualDisk : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _detached;

    /// <summary>부착된 가상 디스크의 물리 경로(<c>\\.\PhysicalDriveN</c>).</summary>
    public string PhysicalPath { get; }

    /// <summary>부착된 가상 디스크의 디스크 번호(N). 오프라인 처리 등에 씁니다.</summary>
    public int DiskNumber { get; }

    /// <summary>가상 디스크의 논리 크기(바이트).</summary>
    public long SizeBytes { get; }

    private VirtualDisk(SafeFileHandle handle, string physicalPath, int diskNumber, long size)
    {
        _handle = handle;
        PhysicalPath = physicalPath;
        DiskNumber = diskNumber;
        SizeBytes = size;
    }

    /// <summary>
    /// 새 동적 VHDX를 만들어 물리 디스크로 부착합니다(백업 대상). 드라이브 문자는 붙이지 않습니다.
    /// </summary>
    /// <param name="path">만들 .vhdx 파일 경로. 이미 있으면 실패합니다(덮어쓰지 않음).</param>
    /// <param name="sizeBytes">가상 디스크 크기(원본 디스크 크기와 같게). 섹터 배수여야 합니다.</param>
    /// <param name="sectorSize">논리 섹터 크기(원본과 동일, 보통 512).</param>
    public static VirtualDisk CreateAndAttach(string path, long sizeBytes, int sectorSize = 512)
    {
        if (sizeBytes <= 0 || sizeBytes % sectorSize != 0)
            throw new ArgumentException($"크기 {sizeBytes:N0}가 섹터 크기 {sectorSize}의 배수가 아닙니다.", nameof(sizeBytes));

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
        };

        var createParams = new CREATE_VIRTUAL_DISK_PARAMETERS
        {
            Version = CREATE_VIRTUAL_DISK_VERSION_2,
            UniqueId = Guid.NewGuid(),
            MaximumSize = (ulong)sizeBytes,
            BlockSizeInBytes = 0,                       // 0 = VHDX 기본(동적 32MB 블록)
            SectorSizeInBytes = (uint)sectorSize,
            PhysicalSectorSizeInBytes = (uint)sectorSize,
        };

        // Flags=NONE → 동적(확장) VHDX. FULL_PHYSICAL_ALLOCATION을 주면 고정 크기가 됩니다.
        uint result = CreateVirtualDisk(
            ref storageType, path, VIRTUAL_DISK_ACCESS_NONE, IntPtr.Zero,
            CREATE_VIRTUAL_DISK_FLAG_NONE, 0, ref createParams, IntPtr.Zero, out SafeFileHandle handle);

        if (result != ERROR_SUCCESS)
            throw new Win32Exception((int)result, $"VHDX 생성 실패: {path}");

        return AttachAndDescribe(handle, path, sizeBytes, readOnly: false);
    }

    /// <summary>
    /// <paramref name="parentPath"/>를 부모로 하는 <b>차등(differencing) VHDX</b>를 만들어 쓰기
    /// 가능하게 부착합니다. 쓰기는 모두 자식 파일로만 가고 <b>부모는 절대 바뀌지 않습니다</b>.
    /// </summary>
    /// <remarks>
    /// 축소 리사이즈에서 원본 이미지를 보존하기 위한 것입니다. 부모(백업 이미지)는 읽기 전용으로
    /// 두고, 얇은 자식에 파일시스템 축소(Windows 축소기)를 적용한 뒤 복원은 자식(=부모+변경분의
    /// 병합 뷰)에서 읽습니다. 작업이 끝나면 자식 파일만 지우면 됩니다 — 전체 복사 없이(바뀐
    /// 블록만) 부모가 그대로 남습니다. 크기·섹터는 부모에서 상속합니다.
    /// </remarks>
    /// <param name="childPath">만들 자식 .vhdx 경로. 이미 있으면 실패합니다.</param>
    /// <param name="parentPath">부모 .vhdx 경로(그대로 유지됨).</param>
    public static VirtualDisk CreateDifferencingAndAttach(string childPath, string parentPath)
    {
        if (!File.Exists(parentPath))
            throw new FileNotFoundException($"부모 VHDX를 찾지 못했습니다: {parentPath}", parentPath);

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
        };

        // ParentPath가 설정되면 차등 디스크가 됩니다. 크기·섹터는 부모에서 상속하므로 0으로 둡니다.
        IntPtr parentPtr = Marshal.StringToHGlobalUni(parentPath);
        try
        {
            var createParams = new CREATE_VIRTUAL_DISK_PARAMETERS
            {
                Version = CREATE_VIRTUAL_DISK_VERSION_2,
                UniqueId = Guid.NewGuid(),
                MaximumSize = 0,                        // 부모 상속
                BlockSizeInBytes = 0,
                SectorSizeInBytes = 0,                  // 부모 상속
                PhysicalSectorSizeInBytes = 0,
                ParentPath = parentPtr,                 // 설정 → 차등 디스크
                ParentVirtualStorageType = storageType,
            };

            uint result = CreateVirtualDisk(
                ref storageType, childPath, VIRTUAL_DISK_ACCESS_NONE, IntPtr.Zero,
                CREATE_VIRTUAL_DISK_FLAG_NONE, 0, ref createParams, IntPtr.Zero, out SafeFileHandle handle);

            if (result != ERROR_SUCCESS)
                throw new Win32Exception((int)result, $"차등 VHDX 생성 실패: {childPath} (부모 {parentPath})");

            // 크기는 부모에서 상속하므로 생성 시점엔 모릅니다(0). 필요하면 디스크 열거로 확인합니다.
            return AttachAndDescribe(handle, childPath, sizeBytes: 0, readOnly: false);
        }
        finally
        {
            Marshal.FreeHGlobal(parentPtr);
        }
    }

    /// <summary>
    /// 기존 VHDX를 열어 물리 디스크로 부착합니다(복원 원본). 기본은 읽기 전용.
    /// </summary>
    public static VirtualDisk OpenAndAttach(string path, bool readOnly = true)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"VHDX 파일을 찾지 못했습니다: {path}", path);

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
        };

        // Parameters=NULL 허용(기본값). 접근권은 넉넉히 ALL로 열고 부착 단계에서 RO/RW를 정합니다.
        uint result = OpenVirtualDisk(
            ref storageType, path, VIRTUAL_DISK_ACCESS_ALL, OPEN_VIRTUAL_DISK_FLAG_NONE,
            IntPtr.Zero, out SafeFileHandle handle);

        if (result != ERROR_SUCCESS)
            throw new Win32Exception((int)result, $"VHDX 열기 실패: {path}");

        return AttachAndDescribe(handle, path, sizeBytes: 0, readOnly);
    }

    private static VirtualDisk AttachAndDescribe(SafeFileHandle handle, string path, long sizeBytes, bool readOnly)
    {
        try
        {
            uint attachFlags = ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER;
            if (readOnly) attachFlags |= ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY;

            var attachParams = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = ATTACH_VIRTUAL_DISK_VERSION_1 };

            uint result = AttachVirtualDisk(handle, IntPtr.Zero, attachFlags, 0, ref attachParams, IntPtr.Zero);
            if (result != ERROR_SUCCESS)
                throw new Win32Exception((int)result, $"VHDX 부착 실패: {path}");

            string physicalPath = QueryPhysicalPath(handle);
            int diskNumber = ParseDiskNumber(physicalPath);

            return new VirtualDisk(handle, physicalPath, diskNumber,
                sizeBytes > 0 ? sizeBytes : 0);
        }
        catch
        {
            // 부착 이후 실패했으면 되돌립니다.
            try { DetachVirtualDisk(handle, DETACH_VIRTUAL_DISK_FLAG_NONE, 0); } catch { /* best-effort */ }
            handle.Dispose();
            throw;
        }
    }

    private static string QueryPhysicalPath(SafeFileHandle handle)
    {
        // 먼저 필요한 버퍼 크기를 물어보고, 그 크기로 다시 호출합니다.
        uint size = 0;
        GetVirtualDiskPhysicalPath(handle, ref size, null);
        if (size == 0) size = 260 * 2; // 안전한 기본치(문자 260개, UTF-16)

        var sb = new StringBuilder((int)(size / 2) + 1);
        uint result = GetVirtualDiskPhysicalPath(handle, ref size, sb);
        if (result != ERROR_SUCCESS)
            throw new Win32Exception((int)result, "부착된 VHDX의 물리 경로를 얻지 못했습니다.");

        return sb.ToString();
    }

    private static int ParseDiskNumber(string physicalPath)
    {
        // "\\.\PhysicalDrive5" → 5
        int i = physicalPath.Length;
        while (i > 0 && char.IsDigit(physicalPath[i - 1])) i--;
        if (i < physicalPath.Length && int.TryParse(physicalPath.AsSpan(i), out int n))
            return n;
        throw new FormatException($"물리 경로에서 디스크 번호를 파싱하지 못했습니다: {physicalPath}");
    }

    public void Dispose()
    {
        if (!_detached && !_handle.IsInvalid)
        {
            try { DetachVirtualDisk(_handle, DETACH_VIRTUAL_DISK_FLAG_NONE, 0); } catch { /* best-effort */ }
            _detached = true;
        }
        _handle.Dispose();
    }

    // --- P/Invoke (virtdisk.dll) --------------------------------------------

    private const uint ERROR_SUCCESS = 0;

    private const uint VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;
    private static readonly Guid VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT =
        new("ec984aec-a0f9-47e9-901f-71415a66345b");

    private const uint CREATE_VIRTUAL_DISK_VERSION_2 = 2;
    private const uint ATTACH_VIRTUAL_DISK_VERSION_1 = 1;

    private const uint VIRTUAL_DISK_ACCESS_NONE = 0;
    private const uint VIRTUAL_DISK_ACCESS_ALL = 0x003f0000;

    private const uint CREATE_VIRTUAL_DISK_FLAG_NONE = 0;
    private const uint OPEN_VIRTUAL_DISK_FLAG_NONE = 0;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 1;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 2;
    private const uint DETACH_VIRTUAL_DISK_FLAG_NONE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    // 네이티브 union이 ULONGLONG 때문에 8바이트 정렬이라, Version 뒤로 4바이트를 띄워
    // 나머지가 정확히 놓이도록 명시적 오프셋으로 배치합니다(잘못 정렬하면 MaximumSize가 깨짐).
    [StructLayout(LayoutKind.Explicit)]
    private struct CREATE_VIRTUAL_DISK_PARAMETERS
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSizeInBytes;
        [FieldOffset(36)] public uint SectorSizeInBytes;
        [FieldOffset(40)] public uint PhysicalSectorSizeInBytes;
        [FieldOffset(48)] public IntPtr ParentPath;
        [FieldOffset(56)] public IntPtr SourcePath;
        [FieldOffset(64)] public uint OpenFlags;
        [FieldOffset(68)] public VIRTUAL_STORAGE_TYPE ParentVirtualStorageType;
        [FieldOffset(88)] public VIRTUAL_STORAGE_TYPE SourceVirtualStorageType;
        [FieldOffset(108)] public Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint CreateVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE VirtualStorageType, string Path,
        uint VirtualDiskAccessMask, IntPtr SecurityDescriptor,
        uint Flags, uint ProviderSpecificFlags,
        ref CREATE_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped,
        out SafeFileHandle Handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint OpenVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE VirtualStorageType, string Path,
        uint VirtualDiskAccessMask, uint Flags,
        IntPtr Parameters, out SafeFileHandle Handle);

    [DllImport("virtdisk.dll", SetLastError = false)]
    private static extern uint AttachVirtualDisk(
        SafeFileHandle VirtualDiskHandle, IntPtr SecurityDescriptor,
        uint Flags, uint ProviderSpecificFlags,
        ref ATTACH_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

    [DllImport("virtdisk.dll", SetLastError = false)]
    private static extern uint DetachVirtualDisk(SafeFileHandle VirtualDiskHandle, uint Flags, uint ProviderSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle VirtualDiskHandle, ref uint DiskPathSizeInBytes,
        StringBuilder? DiskPath);
}
