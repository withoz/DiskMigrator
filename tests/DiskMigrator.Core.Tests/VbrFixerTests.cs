using System.Buffers.Binary;
using System.Text;
using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 옮겨진 파티션의 볼륨 부트 레코드에 적힌 시작 위치 갱신.
/// </summary>
/// <remarks>
/// 실기 MBR 리사이즈 클론에서 발견했습니다. 뒤로 밀린 복구 파티션의 부트 섹터가 여전히
/// 옛 시작 LBA(453,838,848)를 가리키고 있었습니다 — 실제 위치는 1,918,963,712.
/// </remarks>
public class VbrFixerTests
{
    private const int SectorSize = 512;
    private const int HiddenSectorsOffset = 0x1C;

    /// <summary>여러 섹터를 담는 페이크(VBR은 파티션 시작 위치에 있습니다).</summary>
    private sealed class RamDisk(long length, int sectorSize = SectorSize) : Core.Abstractions.IBlockDevice
    {
        private readonly Dictionary<long, byte[]> _sectors = [];

        public string Id => "ram";
        public long Length { get; } = length;
        public int SectorSize { get; } = sectorSize;
        public bool CanWrite => true;

        public int Read(long offset, Span<byte> buffer)
        {
            if (_sectors.TryGetValue(offset, out var s)) s.AsSpan(0, buffer.Length).CopyTo(buffer);
            else buffer.Clear();
            return buffer.Length;
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            var s = new byte[buffer.Length];
            buffer.CopyTo(s);
            _sectors[offset] = s;
        }

        public byte[] SectorAt(long offset) => _sectors[offset];
        public void Flush() { }
        public void Dispose() { }
    }

    private static byte[] Vbr(string oem, uint hiddenSectors, string? fsType = null)
    {
        var v = new byte[SectorSize];
        Encoding.ASCII.GetBytes(oem.PadRight(8)).CopyTo(v, 3);
        if (fsType is not null) Encoding.ASCII.GetBytes(fsType.PadRight(8)).CopyTo(v, 0x52);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(HiddenSectorsOffset, 4), hiddenSectors);
        v[510] = 0x55;
        v[511] = 0xAA;
        return v;
    }

    private static uint HiddenOf(byte[] sector) =>
        BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(HiddenSectorsOffset, 4));

    [Fact]
    public void 옮겨진_NTFS_볼륨의_시작_위치를_고친다()
    {
        // 실기에서 본 값 그대로.
        const long oldStart = 453_838_848, newStart = 1_918_963_712;
        var dev = new RamDisk(2_000_000_000_000L);
        dev.Write(newStart * SectorSize, Vbr("NTFS", (uint)oldStart));

        int n = VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(oldStart, newStart, newStart + 1000)]);

        Assert.Equal(1, n);
        Assert.Equal((uint)newStart, HiddenOf(dev.SectorAt(newStart * SectorSize)));
    }

    [Fact]
    public void 제자리_파티션은_건드리지_않는다()
    {
        // 확대 대상은 시작 위치가 그대로라 고칠 것이 없습니다.
        const long start = 2048;
        var dev = new RamDisk(1_000_000_000L);
        dev.Write(start * SectorSize, Vbr("NTFS", (uint)start));

        int n = VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(start, start, start + 1_000_000)]);

        Assert.Equal(0, n);
    }

    [Fact]
    public void FAT32도_고친다()
    {
        // ESP가 옮겨지는 배치도 있습니다.
        const long oldStart = 2048, newStart = 500_000;
        var dev = new RamDisk(1_000_000_000L);
        dev.Write(newStart * SectorSize, Vbr("MSDOS5.0", (uint)oldStart, fsType: "FAT32"));

        int n = VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(oldStart, newStart, newStart + 100)]);

        Assert.Equal(1, n);
        Assert.Equal((uint)newStart, HiddenOf(dev.SectorAt(newStart * SectorSize)));
    }

    [Fact]
    public void 알아보지_못한_볼륨은_건드리지_않는다()
    {
        // 0x1C가 무엇인지 모르는 파일시스템에 4바이트를 덮어쓰면 망가뜨립니다.
        const long oldStart = 2048, newStart = 500_000;
        var dev = new RamDisk(1_000_000_000L);
        dev.Write(newStart * SectorSize, Vbr("EXT4????", (uint)oldStart));

        int n = VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(oldStart, newStart, newStart + 100)]);

        Assert.Equal(0, n);
        Assert.Equal((uint)oldStart, HiddenOf(dev.SectorAt(newStart * SectorSize)));
    }

    [Fact]
    public void 부트_서명이_없으면_건드리지_않는다()
    {
        const long oldStart = 2048, newStart = 500_000;
        var dev = new RamDisk(1_000_000_000L);
        var v = Vbr("NTFS", (uint)oldStart);
        v[510] = 0;
        dev.Write(newStart * SectorSize, v);

        Assert.Equal(0, VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(oldStart, newStart, newStart + 100)]));
    }

    [Fact]
    public void 이미_맞는_값이면_다시_쓰지_않는다()
    {
        const long oldStart = 2048, newStart = 500_000;
        var dev = new RamDisk(1_000_000_000L);
        dev.Write(newStart * SectorSize, Vbr("NTFS", (uint)newStart));

        Assert.Equal(0, VbrFixer.FixMovedPartitions(dev, [new PartitionRemap(oldStart, newStart, newStart + 100)]));
    }
}
