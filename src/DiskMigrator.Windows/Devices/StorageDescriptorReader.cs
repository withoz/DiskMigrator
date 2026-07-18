using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

internal sealed record StorageDescriptor(
    string? VendorId,
    string? ProductId,
    string? ProductRevision,
    string? SerialNumber,
    DiskBusType BusType,
    bool RemovableMedia);

/// <summary>
/// IOCTL_STORAGE_QUERY_PROPERTY로 장치의 벤더/모델/시리얼/버스 종류를 읽습니다.
/// </summary>
/// <remarks>
/// WMI의 Win32_DiskDrive보다 이쪽이 정확합니다. 특히 InterfaceType은 NVMe를 그냥 "SCSI"로
/// 보고하는 반면, STORAGE_BUS_TYPE은 BusTypeNvme를 제대로 알려줍니다.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StorageDescriptorReader
{
    public static unsafe StorageDescriptor? Read(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        byte[] raw;
        try
        {
            raw = DiskIoctl.QueryVariable(
                handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                (nint)(&query),
                (uint)Unsafe.SizeOf<STORAGE_PROPERTY_QUERY>(),
                initialSize: 1024);
        }
        catch
        {
            return null;
        }

        if (raw.Length < Unsafe.SizeOf<STORAGE_DEVICE_DESCRIPTOR>()) return null;

        var descriptor = MemoryMarshal.Read<STORAGE_DEVICE_DESCRIPTOR>(raw);

        return new StorageDescriptor(
            ReadOffsetString(raw, descriptor.VendorIdOffset),
            ReadOffsetString(raw, descriptor.ProductIdOffset),
            ReadOffsetString(raw, descriptor.ProductRevisionOffset),
            ReadOffsetString(raw, descriptor.SerialNumberOffset),
            MapBusType(descriptor.BusType),
            descriptor.RemovableMedia != 0);
    }

    /// <summary>물리 섹터 크기를 조회합니다. 512e 디스크는 논리 512 / 물리 4096입니다.</summary>
    public static int? ReadPhysicalSectorSize(SafeFileHandle handle)
    {
        var alignment = ReadAlignment(handle);
        if (alignment is null || alignment.Value.BytesPerPhysicalSector == 0) return null;

        return (int)alignment.Value.BytesPerPhysicalSector;
    }

    /// <summary>논리 섹터 크기를 조회합니다. 지오메트리 IOCTL이 안 통하는 볼륨 장치용 대체 수단입니다.</summary>
    public static int? ReadLogicalSectorSize(SafeFileHandle handle)
    {
        var alignment = ReadAlignment(handle);
        if (alignment is null || alignment.Value.BytesPerLogicalSector == 0) return null;

        return (int)alignment.Value.BytesPerLogicalSector;
    }

    private static unsafe STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR? ReadAlignment(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageAccessAlignmentProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        try
        {
            byte[] raw = DiskIoctl.QueryVariable(
                handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                (nint)(&query),
                (uint)Unsafe.SizeOf<STORAGE_PROPERTY_QUERY>(),
                initialSize: 128);

            if (raw.Length < Unsafe.SizeOf<STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>()) return null;

            return MemoryMarshal.Read<STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 디스크립터 뒤쪽 문자열 풀에서 오프셋 기반 ANSI 문자열을 읽습니다.
    /// 오프셋 0은 "해당 정보 없음"을 뜻합니다.
    /// </summary>
    private static string? ReadOffsetString(byte[] raw, uint offset)
    {
        if (offset == 0 || offset >= raw.Length) return null;

        int end = (int)offset;
        while (end < raw.Length && raw[end] != 0) end++;

        if (end == offset) return null;

        string value = Encoding.ASCII.GetString(raw, (int)offset, end - (int)offset).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DiskBusType MapBusType(STORAGE_BUS_TYPE busType) => busType switch
    {
        STORAGE_BUS_TYPE.BusTypeScsi => DiskBusType.Scsi,
        STORAGE_BUS_TYPE.BusTypeAtapi => DiskBusType.Atapi,
        STORAGE_BUS_TYPE.BusTypeAta => DiskBusType.Ata,
        STORAGE_BUS_TYPE.BusType1394 => DiskBusType.Ieee1394,
        STORAGE_BUS_TYPE.BusTypeSsa => DiskBusType.Ssa,
        STORAGE_BUS_TYPE.BusTypeFibre => DiskBusType.Fibre,
        STORAGE_BUS_TYPE.BusTypeUsb => DiskBusType.Usb,
        STORAGE_BUS_TYPE.BusTypeRAID => DiskBusType.RAID,
        STORAGE_BUS_TYPE.BusTypeiScsi => DiskBusType.Iscsi,
        STORAGE_BUS_TYPE.BusTypeSas => DiskBusType.Sas,
        STORAGE_BUS_TYPE.BusTypeSata => DiskBusType.Sata,
        STORAGE_BUS_TYPE.BusTypeSd => DiskBusType.Sd,
        STORAGE_BUS_TYPE.BusTypeMmc => DiskBusType.Mmc,
        STORAGE_BUS_TYPE.BusTypeVirtual => DiskBusType.Virtual,
        STORAGE_BUS_TYPE.BusTypeFileBackedVirtual => DiskBusType.FileBackedVirtual,
        STORAGE_BUS_TYPE.BusTypeNvme => DiskBusType.Nvme,
        _ => DiskBusType.Unknown,
    };
}
