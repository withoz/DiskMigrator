using System.Buffers.Binary;
using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// MBR 파티션 테이블 재작성.
/// </summary>
/// <remarks>
/// 이 코드가 틀리면 대상 디스크를 통째로 못 읽게 됩니다. 눈으로 확인할 수 없는 영역이라
/// 바이트 단위로 고정합니다. 특히 <b>디스크 서명(0x1B8)과 부트 코드를 건드리지 않는 것</b>이
/// 중요합니다 — Windows BCD가 "디스크 서명 + 파티션 오프셋"으로 부팅 볼륨을 찾기 때문입니다.
/// </remarks>
public class MbrRewriterTests
{
    private const int SectorSize = 512;
    private const int TableOffset = 446;
    private const int EntrySize = 16;

    /// <summary>
    /// 대상 디스크(931.51 GB)에 원본 N:의 MBR이 그대로 복사된 상태 — [NTFS 216GB][복구 16GB].
    /// 재작성은 언제나 <b>대상</b>에 일어나므로 크기는 대상 것입니다.
    /// </summary>
    private static SparseDisk BuildDisk(
        long diskBytes = 1_000_204_886_016L,
        byte secondType = 0x27,
        bool extendedInstead = false)
    {
        var dev = new SparseDisk(diskBytes, SectorSize);
        var mbr = new byte[SectorSize];

        // 부트 코드 자리를 알아볼 수 있는 값으로 채워, 재작성이 넘보지 않는지 확인합니다.
        for (int i = 0; i < 440; i++) mbr[i] = (byte)(i % 251);

        // NT 디스크 서명 (0x1B8) — 실기 N: 디스크의 값.
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1B8, 4), 812018231u);

        WriteEntry(mbr, 0, bootable: true, type: 0x07, startLba: 2048, sectorCount: 453_836_800);
        WriteEntry(mbr, 1, bootable: false,
            type: extendedInstead ? (byte)0x0F : secondType,
            startLba: 453_838_848, sectorCount: 34_558_733);

        mbr[510] = 0x55;
        mbr[511] = 0xAA;

        dev.Write(0, mbr);
        return dev;
    }

    private static void WriteEntry(
        byte[] mbr, int index, bool bootable, byte type, uint startLba, uint sectorCount)
    {
        var e = mbr.AsSpan(TableOffset + (index * EntrySize), EntrySize);
        e[0] = bootable ? (byte)0x80 : (byte)0x00;
        e[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(8, 4), startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(12, 4), sectorCount);
    }

    private static (uint Start, uint Count, byte Type, byte Boot) ReadEntry(byte[] mbr, int index)
    {
        var e = mbr.AsSpan(TableOffset + (index * EntrySize), EntrySize);
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12, 4)),
            e[4],
            e[0]);
    }

    /// <summary>1번을 넓히고 2번(복구)을 오른쪽으로 미는, 실제로 쓰는 배치.</summary>
    private static List<PartitionRemap> GrowFirstShiftSecond()
    {
        const long grownSectors = 1_800_000_000L;   // 약 858 GB
        long newSecondStart = 2048 + grownSectors;

        return
        [
            new PartitionRemap(2048, 2048, 2048 + grownSectors - 1),
            new PartitionRemap(453_838_848, newSecondStart, newSecondStart + 34_558_733 - 1),
        ];
    }

    [Fact]
    public void 파티션을_새_위치로_옮긴다()
    {
        var dev = BuildDisk();

        var result = new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond());

        Assert.True(result.Rewritten);

        var p1 = ReadEntry(dev.Sector0, 0);
        var p2 = ReadEntry(dev.Sector0, 1);

        Assert.Equal(2048u, p1.Start);
        Assert.Equal(1_800_000_000u, p1.Count);
        Assert.Equal(2048u + 1_800_000_000u, p2.Start);
        Assert.Equal(34_558_733u, p2.Count);
    }

    [Fact]
    public void 디스크_서명과_부트_코드를_건드리지_않는다()
    {
        // BCD가 디스크 서명으로 부팅 볼륨을 찾습니다. 여기가 바뀌면 클론이 부팅하지 못합니다.
        var dev = BuildDisk();
        byte[] before = dev.Sector0[..TableOffset].ToArray();

        new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond());

        Assert.Equal(before, dev.Sector0[..TableOffset].ToArray());
        Assert.Equal(812018231u, BinaryPrimitives.ReadUInt32LittleEndian(dev.Sector0.AsSpan(0x1B8, 4)));
    }

    [Fact]
    public void 부팅_표시와_파티션_타입을_보존한다()
    {
        var dev = BuildDisk();

        new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond());

        var p1 = ReadEntry(dev.Sector0, 0);
        var p2 = ReadEntry(dev.Sector0, 1);

        Assert.Equal(0x80, p1.Boot);      // 활성 파티션 표시
        Assert.Equal(0x07, p1.Type);      // NTFS
        Assert.Equal(0x00, p2.Boot);
        Assert.Equal(0x27, p2.Type);      // 복구 환경
    }

    [Fact]
    public void MBR_서명과_빈_슬롯은_그대로_둔다()
    {
        var dev = BuildDisk();

        new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond());

        Assert.Equal(0x55, dev.Sector0[510]);
        Assert.Equal(0xAA, dev.Sector0[511]);

        // 쓰지 않는 3·4번 슬롯은 0으로 남아야 합니다.
        Assert.Equal((0u, 0u, (byte)0, (byte)0), ReadEntry(dev.Sector0, 2));
        Assert.Equal((0u, 0u, (byte)0, (byte)0), ReadEntry(dev.Sector0, 3));
    }

    [Fact]
    public void CHS가_LBA와_함께_갱신된다()
    {
        // CHS 한계(약 8GB)를 넘는 위치는 관례대로 0xFE/0xFF/0xFF가 됩니다.
        var dev = BuildDisk();

        new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond());

        var e2 = dev.Sector0.AsSpan(TableOffset + EntrySize, EntrySize);
        Assert.Equal(0xFE, e2[1]);
        Assert.Equal(0xFF, e2[2]);
        Assert.Equal(0xFF, e2[3]);
    }

    [Fact]
    public void 확장_파티션이_있으면_거절한다()
    {
        // EBR 체인을 함께 고치지 않고 옮기면 논리 드라이브를 통째로 잃습니다.
        var dev = BuildDisk(extendedInstead: true);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond()));

        Assert.Contains("확장 파티션", ex.Message);
    }

    [Fact]
    public void 거절할_때는_아무것도_쓰지_않는다()
    {
        var dev = BuildDisk(extendedInstead: true);
        byte[] before = dev.Sector0[..SectorSize].ToArray();

        Assert.Throws<InvalidOperationException>(
            () => new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond()));

        Assert.Equal(before, dev.Sector0[..SectorSize].ToArray());
    }

    [Fact]
    public void 대응하는_재배치_정보가_없으면_거절한다()
    {
        // 원본 파티션 하나를 빠뜨린 배치로 쓰면, 빠진 파티션은 옛 위치를 가리킨 채 남습니다.
        var dev = BuildDisk();
        var partial = new List<PartitionRemap> { new(2048, 2048, 2048 + 1_800_000_000 - 1) };

        var ex = Assert.Throws<InvalidOperationException>(() => new MbrRewriter().Rewrite(dev, partial));

        Assert.Contains("재배치 정보가 없습니다", ex.Message);
    }

    [Fact]
    public void 새_배치가_디스크를_넘으면_거절한다()
    {
        var dev = BuildDisk();
        long tooFar = (dev.Length / SectorSize) + 1000;
        var remaps = new List<PartitionRemap>
        {
            new(2048, 2048, 2047 + 1000),
            new(453_838_848, tooFar, tooFar + 999),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new MbrRewriter().Rewrite(dev, remaps));

        Assert.Contains("대상 디스크를 넘습니다", ex.Message);
    }

    [Fact]
    public void 겹치는_배치는_거절한다()
    {
        // 겹친 배치를 쓰면 두 파일시스템이 같은 섹터를 자기 것으로 알고 서로를 덮어씁니다.
        var dev = BuildDisk();
        var overlapping = new List<PartitionRemap>
        {
            new(2048, 2048, 1_000_000),
            new(453_838_848, 900_000, 1_200_000),   // 앞 파티션 안에서 시작
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new MbrRewriter().Rewrite(dev, overlapping));

        Assert.Contains("겹칩니다", ex.Message);
    }

    [Fact]
    public void 겹침이_발견되면_아무것도_쓰지_않는다()
    {
        var dev = BuildDisk();
        byte[] before = dev.Sector0[..SectorSize].ToArray();
        var overlapping = new List<PartitionRemap>
        {
            new(2048, 2048, 1_000_000),
            new(453_838_848, 900_000, 1_200_000),
        };

        Assert.Throws<InvalidOperationException>(() => new MbrRewriter().Rewrite(dev, overlapping));

        Assert.Equal(before, dev.Sector0[..SectorSize].ToArray());
    }

    [Fact]
    public void 파티션이_0번_섹터에서_시작하면_거절한다()
    {
        // 0번 섹터는 MBR 자신입니다.
        var dev = BuildDisk();
        var remaps = new List<PartitionRemap>
        {
            new(2048, 0, 1_000_000),
            new(453_838_848, 2_000_000, 2_100_000),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new MbrRewriter().Rewrite(dev, remaps));

        Assert.Contains("0번 섹터", ex.Message);
    }

    [Fact]
    public void 보호_MBR이면_거절한다()
    {
        // GPT 디스크에 MBR 재작성을 적용하면 파티션 테이블을 잃습니다.
        var dev = BuildDisk();
        dev.Sector0[TableOffset + 4] = 0xEE;

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond()));

        Assert.Contains("GPT", ex.Message);
    }

    [Fact]
    public void MBR_서명이_없으면_거절한다()
    {
        var dev = BuildDisk();
        dev.Sector0[510] = 0x00;

        Assert.Throws<InvalidOperationException>(
            () => new MbrRewriter().Rewrite(dev, GrowFirstShiftSecond()));
    }
}
