using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

internal sealed record RawPartition(
    int Number,
    long StartingOffset,
    long Length,
    Guid? GptType,
    byte? MbrType,
    bool IsActive);

internal sealed record DriveLayout(PartitionStyle Style, IReadOnlyList<RawPartition> Partitions);

/// <summary>
/// IOCTL_DISK_GET_DRIVE_LAYOUT_EX로 파티션 테이블을 읽습니다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DriveLayoutReader
{
    public static DriveLayout Read(SafeFileHandle handle)
    {
        byte[] raw;
        try
        {
            raw = DiskIoctl.QueryVariable(
                handle,
                NativeMethods.IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                initialSize: 4096);
        }
        catch
        {
            // 초기화되지 않은 디스크는 이 IOCTL이 실패합니다 — 정상적인 상황입니다.
            return new DriveLayout(PartitionStyle.Raw, []);
        }

        int headerSize = Unsafe.SizeOf<DRIVE_LAYOUT_INFORMATION_EX>();
        if (raw.Length < headerSize)
        {
            return new DriveLayout(PartitionStyle.Unknown, []);
        }

        var header = MemoryMarshal.Read<DRIVE_LAYOUT_INFORMATION_EX>(raw);

        var style = header.PartitionStyle switch
        {
            PARTITION_STYLE.PARTITION_STYLE_MBR => PartitionStyle.Mbr,
            PARTITION_STYLE.PARTITION_STYLE_GPT => PartitionStyle.Gpt,
            PARTITION_STYLE.PARTITION_STYLE_RAW => PartitionStyle.Raw,
            _ => PartitionStyle.Unknown,
        };

        var partitions = new List<RawPartition>();
        int entrySize = Unsafe.SizeOf<PARTITION_INFORMATION_EX>();
        int offset = headerSize;

        for (int i = 0; i < header.PartitionCount; i++)
        {
            if (offset + entrySize > raw.Length) break;

            var entry = MemoryMarshal.Read<PARTITION_INFORMATION_EX>(raw.AsSpan(offset, entrySize));
            offset += entrySize;

            // MBR 디스크는 항상 4개 항목을 보고하며 빈 슬롯은 길이 0입니다.
            if (entry.PartitionLength == 0) continue;

            // MBR 확장 파티션 컨테이너(타입 0x05/0x0F)는 데이터가 없는 껍데기이므로 제외합니다.
            if (entry.PartitionStyle == PARTITION_STYLE.PARTITION_STYLE_MBR &&
                entry.Info.Mbr.PartitionType is 0x05 or 0x0F)
            {
                continue;
            }

            partitions.Add(new RawPartition(
                Number: (int)entry.PartitionNumber,
                StartingOffset: entry.StartingOffset,
                Length: entry.PartitionLength,
                GptType: entry.PartitionStyle == PARTITION_STYLE.PARTITION_STYLE_GPT
                    ? entry.Info.Gpt.PartitionType
                    : null,
                MbrType: entry.PartitionStyle == PARTITION_STYLE.PARTITION_STYLE_MBR
                    ? entry.Info.Mbr.PartitionType
                    : null,
                IsActive: entry.PartitionStyle == PARTITION_STYLE.PARTITION_STYLE_MBR &&
                          entry.Info.Mbr.BootIndicator != 0));
        }

        return new DriveLayout(style, partitions.OrderBy(p => p.StartingOffset).ToList());
    }
}
