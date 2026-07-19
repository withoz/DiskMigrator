using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 파티션 확대 배치 계산(v0.3.0 리사이즈의 핵심, 순수 로직)을 검증합니다.
/// </summary>
public class ResizePlannerTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    /// <summary>오프셋·크기(바이트)로 파티션을 만듭니다.</summary>
    private static PartitionInfo Part(int number, long start, long length) =>
        new() { Number = number, StartingOffset = start, LengthBytes = length };

    /// <summary>전형적 Windows 레이아웃 [ESP 100MB][MSR 16MB][C: 나머지][복구 500MB], 원본 20GB.</summary>
    private static List<PartitionInfo> WindowsLayout()
    {
        long esp = 1 * Mb;              // 1MB 정렬 시작
        long espLen = 100 * Mb;
        long msr = esp + espLen;        // 101MB
        long msrLen = 16 * Mb;
        long c = msr + msrLen;          // 117MB
        long recLen = 500 * Mb;
        long total = 20 * Gb;
        long recStart = total - recLen - 2 * Mb; // 복구는 디스크 끝 근처
        long cLen = recStart - c;
        return
        [
            Part(1, esp, espLen),
            Part(2, msr, msrLen),
            Part(3, c, cLen),
            Part(4, recStart, recLen),
        ];
    }

    [Fact]
    public void 마지막_파티션_채우기()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 1 * Gb) };
        long target = 4 * Gb;

        var layout = ResizePlanner.Plan(src, target, new PartitionGrowRequest(2, null));

        var p2 = layout.Partitions.Single(p => p.SourceNumber == 2);
        Assert.True(p2.Grown);
        Assert.Equal(101 * Mb, p2.StartingOffset);          // 시작은 그대로
        Assert.True(p2.EndOffset <= 4 * Gb - ResizePlanner.EndReserve);
        // 앞 파티션은 불변
        Assert.False(layout.Partitions.Single(p => p.SourceNumber == 1).Grown);
        Assert.Equal(1 * Mb, layout.Partitions.Single(p => p.SourceNumber == 1).StartingOffset);
    }

    [Fact]
    public void 가운데_파티션_확대는_뒤_파티션을_민다()
    {
        var src = WindowsLayout();
        long target = 60 * Gb;   // 원본 20GB → 40GB 늘어남
        long recStartBefore = src.Single(p => p.Number == 4).StartingOffset;
        long cLenBefore = src.Single(p => p.Number == 3).LengthBytes;

        var layout = ResizePlanner.Plan(src, target, new PartitionGrowRequest(3, null));

        var c = layout.Partitions.Single(p => p.SourceNumber == 3);
        var rec = layout.Partitions.Single(p => p.SourceNumber == 4);

        Assert.True(c.Grown);
        Assert.True(c.LengthBytes > cLenBefore);
        // 복구 파티션은 오른쪽으로 밀렸고 크기는 그대로.
        Assert.True(rec.StartingOffset > recStartBefore);
        Assert.Equal(500 * Mb, rec.LengthBytes);
        Assert.False(rec.Grown);
        // 확대량만큼 정확히 밀렸다.
        long delta = c.LengthBytes - cLenBefore;
        Assert.Equal(recStartBefore + delta, rec.StartingOffset);
    }

    [Fact]
    public void 확대량은_1MB_정렬된다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.Plan(src, 60 * Gb, new PartitionGrowRequest(3, null));

        var rec = layout.Partitions.Single(p => p.SourceNumber == 4);
        long recStartBefore = src.Single(p => p.Number == 4).StartingOffset;
        long delta = rec.StartingOffset - recStartBefore;
        Assert.Equal(0, delta % ResizePlanner.Alignment);
    }

    [Fact]
    public void 명시적_크기로_확대()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 2 * Gb) };
        long target = 20 * Gb;

        var layout = ResizePlanner.Plan(src, target, new PartitionGrowRequest(2, 5 * Gb));

        var p2 = layout.Partitions.Single(p => p.SourceNumber == 2);
        Assert.Equal(5 * Gb, p2.LengthBytes);
        Assert.True(p2.Grown);
    }

    [Fact]
    public void 현재보다_작은_크기는_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 5 * Gb) };

        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.Plan(src, 20 * Gb, new PartitionGrowRequest(2, 2 * Gb)));
    }

    [Fact]
    public void 요청_크기가_대상을_넘으면_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 1 * Gb) };

        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.Plan(src, 4 * Gb, new PartitionGrowRequest(2, 100 * Gb)));
    }

    [Fact]
    public void 여유가_없으면_거부된다()
    {
        // 대상이 원본과 사실상 같음.
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 1 * Gb) };
        long target = 101 * Mb + 1 * Gb + 512 * 1024; // 0.5MB만 남음 → 1MB 정렬 후 여유 0

        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.Plan(src, target, new PartitionGrowRequest(2, null)));
    }

    [Fact]
    public void 없는_파티션_번호는_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 1 * Gb) };

        Assert.Throws<ArgumentException>(() =>
            ResizePlanner.Plan(src, 4 * Gb, new PartitionGrowRequest(99, null)));
    }

    [Fact]
    public void 파티션은_겹치지_않고_대상_안에_있다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.Plan(src, 100 * Gb, new PartitionGrowRequest(3, null));

        var ps = layout.Partitions.OrderBy(p => p.StartingOffset).ToList();
        for (int i = 1; i < ps.Count; i++)
            Assert.True(ps[i - 1].EndOffset <= ps[i].StartingOffset, "겹치면 안 됨");

        Assert.True(ps[^1].EndOffset <= 100L * Gb - ResizePlanner.EndReserve);
    }

    [Fact]
    public void 채우기는_남는_공간을_최대한_쓴다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 1 * Gb) };
        long target = 10 * Gb;

        var layout = ResizePlanner.Plan(src, target, new PartitionGrowRequest(2, null));
        var p2 = layout.Partitions.Single(p => p.SourceNumber == 2);

        // 끝이 (정렬된 대상 - 예약) 바로 그 지점이어야 한다.
        long expectedEnd = (target - target % ResizePlanner.Alignment) - ResizePlanner.EndReserve;
        Assert.Equal(expectedEnd, p2.EndOffset);
    }
}
