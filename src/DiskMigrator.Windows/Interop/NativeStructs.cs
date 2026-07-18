using System.Runtime.InteropServices;

namespace DiskMigrator.Windows.Interop;

// ReSharper disable InconsistentNaming — Win32 구조체 이름을 그대로 유지해 문서와 대조하기 쉽게 합니다.
//
// 이 파일의 모든 구조체는 blittable(unmanaged)로 유지해야 합니다.
// 이유: DeviceIoControl이 돌려준 원시 바이트를 MemoryMarshal.Read<T>로 해석하는데,
// string이나 bool 같은 "마샬링이 필요한" 필드가 섞이면 Marshal.SizeOf가 계산하는
// 네이티브 레이아웃과 CLR의 관리 레이아웃이 서로 달라져, 컴파일은 되지만
// 필드 값이 조용히 어긋나 읽힙니다. 그래서 BOOLEAN은 byte로, WCHAR[]는 fixed로 씁니다.
// 크기는 반드시 Unsafe.SizeOf<T>()로 구하십시오 (Marshal.SizeOf 금지).

[StructLayout(LayoutKind.Sequential)]
internal struct GET_LENGTH_INFORMATION
{
    internal long Length;
}

internal enum MEDIA_TYPE : uint
{
    Unknown = 0,
    RemovableMedia = 11,
    FixedMedia = 12,
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISK_GEOMETRY
{
    internal long Cylinders;
    internal MEDIA_TYPE MediaType;
    internal uint TracksPerCylinder;
    internal uint SectorsPerTrack;
    internal uint BytesPerSector;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISK_GEOMETRY_EX
{
    internal DISK_GEOMETRY Geometry;
    internal long DiskSize;
    // 뒤에 가변 길이 Data[1]이 따라오지만 여기서는 쓰지 않습니다.
}

// --- IOCTL_STORAGE_QUERY_PROPERTY ------------------------------------------

internal enum STORAGE_PROPERTY_ID : uint
{
    StorageDeviceProperty = 0,
    StorageAdapterProperty = 1,
    StorageAccessAlignmentProperty = 6,
    StorageDeviceSeekPenaltyProperty = 7,
}

internal enum STORAGE_QUERY_TYPE : uint
{
    PropertyStandardQuery = 0,
    PropertyExistsQuery = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct STORAGE_PROPERTY_QUERY
{
    internal STORAGE_PROPERTY_ID PropertyId;
    internal STORAGE_QUERY_TYPE QueryType;
    internal byte AdditionalParameters; // 네이티브로는 UCHAR AdditionalParameters[1]
}

internal enum STORAGE_BUS_TYPE : uint
{
    BusTypeUnknown = 0x00,
    BusTypeScsi = 0x01,
    BusTypeAtapi = 0x02,
    BusTypeAta = 0x03,
    BusType1394 = 0x04,
    BusTypeSsa = 0x05,
    BusTypeFibre = 0x06,
    BusTypeUsb = 0x07,
    BusTypeRAID = 0x08,
    BusTypeiScsi = 0x09,
    BusTypeSas = 0x0A,
    BusTypeSata = 0x0B,
    BusTypeSd = 0x0C,
    BusTypeMmc = 0x0D,
    BusTypeVirtual = 0x0E,
    BusTypeFileBackedVirtual = 0x0F,
    BusTypeSpaces = 0x10,
    BusTypeNvme = 0x11,
    BusTypeSCM = 0x12,
    BusTypeUfs = 0x13,
}

/// <summary>크기 36바이트. 뒤에 벤더/모델/시리얼 문자열이 담긴 바이트 풀이 이어집니다.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct STORAGE_DEVICE_DESCRIPTOR
{
    internal uint Version;
    internal uint Size;
    internal byte DeviceType;
    internal byte DeviceTypeModifier;
    internal byte RemovableMedia;   // BOOLEAN
    internal byte CommandQueueing;  // BOOLEAN
    internal uint VendorIdOffset;
    internal uint ProductIdOffset;
    internal uint ProductRevisionOffset;
    internal uint SerialNumberOffset;
    internal STORAGE_BUS_TYPE BusType;
    internal uint RawPropertiesLength;
}

/// <summary>StorageAccessAlignmentProperty 응답 — 물리 섹터 크기를 알려줍니다.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR
{
    internal uint Version;
    internal uint Size;
    internal uint BytesPerCacheLine;
    internal uint BytesOffsetForCacheAlignment;
    internal uint BytesPerLogicalSector;
    internal uint BytesPerPhysicalSector;
    internal uint BytesOffsetForSectorAlignment;
}

// --- IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS ----------------------------------

/// <summary>크기 24바이트 (ULONG + 4바이트 패딩 + LARGE_INTEGER 2개).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DISK_EXTENT
{
    internal uint DiskNumber;
    internal long StartingOffset;
    internal long ExtentLength;
}

// --- IOCTL_DISK_GET_DRIVE_LAYOUT_EX ----------------------------------------

internal enum PARTITION_STYLE : uint
{
    PARTITION_STYLE_MBR = 0,
    PARTITION_STYLE_GPT = 1,
    PARTITION_STYLE_RAW = 2,
}

/// <summary>크기 24바이트.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PARTITION_INFORMATION_MBR
{
    internal byte PartitionType;
    internal byte BootIndicator;        // BOOLEAN — 0이 아니면 활성 파티션
    internal byte RecognizedPartition;  // BOOLEAN
    internal uint HiddenSectors;
    internal Guid PartitionId;
}

/// <summary>크기 112바이트 (GUID 2개 + ULONGLONG + WCHAR Name[36]).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PARTITION_INFORMATION_GPT
{
    internal Guid PartitionType;
    internal Guid PartitionId;
    internal ulong Attributes;
    internal fixed char Name[36];
}

[StructLayout(LayoutKind.Explicit)]
internal struct PARTITION_INFORMATION_UNION
{
    [FieldOffset(0)] internal PARTITION_INFORMATION_MBR Mbr;
    [FieldOffset(0)] internal PARTITION_INFORMATION_GPT Gpt;
}

/// <summary>크기 144바이트. 유니온이 8바이트 정렬이라 오프셋 32에서 시작합니다.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PARTITION_INFORMATION_EX
{
    internal PARTITION_STYLE PartitionStyle;
    internal long StartingOffset;
    internal long PartitionLength;
    internal uint PartitionNumber;
    internal byte RewritePartition;    // BOOLEAN
    internal byte IsServicePartition;  // BOOLEAN
    internal PARTITION_INFORMATION_UNION Info;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DRIVE_LAYOUT_INFORMATION_MBR
{
    internal uint Signature;
    internal uint CheckSum;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DRIVE_LAYOUT_INFORMATION_GPT
{
    internal Guid DiskId;
    internal long StartingUsableOffset;
    internal long UsableLength;
    internal uint MaxPartitionCount;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DRIVE_LAYOUT_INFORMATION_UNION
{
    [FieldOffset(0)] internal DRIVE_LAYOUT_INFORMATION_MBR Mbr;
    [FieldOffset(0)] internal DRIVE_LAYOUT_INFORMATION_GPT Gpt;
}

/// <summary>크기 48바이트. 뒤에 PARTITION_INFORMATION_EX 배열이 PartitionCount개 이어집니다.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DRIVE_LAYOUT_INFORMATION_EX
{
    internal PARTITION_STYLE PartitionStyle;
    internal uint PartitionCount;
    internal DRIVE_LAYOUT_INFORMATION_UNION Info;
}

/// <summary>
/// IOCTL_DISK_SET_DISK_ATTRIBUTES 입력. 크기 40바이트.
/// </summary>
/// <remarks>
/// AttributesMask로 "어떤 비트를 건드릴지" 정하고, Attributes로 그 비트의 값을 정합니다.
/// 예: 오프라인만 켜려면 Mask=OFFLINE, Attributes=OFFLINE.
///
/// Version 필드에는 반드시 이 구조체의 전체 크기(40)를 넣어야 합니다. 드라이버가
/// Version과 입력 버퍼 크기를 대조하므로, Reserved2까지 포함한 완전한 레이아웃이 필요합니다.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SET_DISK_ATTRIBUTES
{
    internal uint Version;
    internal byte Persist;      // BOOLEAN — 재부팅 후에도 유지할지. 우리는 유지하지 않습니다(false).
    internal byte Reserved1_0;
    internal byte Reserved1_1;
    internal byte Reserved1_2;
    internal ulong Attributes;
    internal ulong AttributesMask;
    internal uint Reserved2_0;
    internal uint Reserved2_1;
    internal uint Reserved2_2;
    internal uint Reserved2_3;
}

/// <summary>IOCTL_DISK_GET_DISK_ATTRIBUTES 출력. 크기 16바이트.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GET_DISK_ATTRIBUTES
{
    internal uint Version;
    internal uint Reserved1;
    internal ulong Attributes;
}

/// <summary>잘 알려진 GPT 파티션 타입 GUID.</summary>
internal static class GptTypes
{
    /// <summary>EFI 시스템 파티션 — UEFI 부팅에 필요합니다.</summary>
    internal static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");

    internal static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    internal static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    internal static readonly Guid WindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
}
