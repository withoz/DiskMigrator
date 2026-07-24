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

    // --- 맞춤 클론(PlanFit) — 대상이 작아도 파티션이 들어가면 제자리 복제 -----------

    [Fact]
    public void 최소_대상_크기는_디스크가_아니라_파티션_끝을_기준으로_한다()
    {
        // 파티션은 1MB~2GB만 차지. 원본 디스크가 1TB든 상관없어야 한다.
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 2 * Gb) };

        long min = ResizePlanner.MinimumTargetSize(src);

        Assert.Equal(101 * Mb + 2 * Gb + ResizePlanner.EndReserve, min);
        Assert.True(min < 3 * Gb);
    }

    [Fact]
    public void 뒤가_비어있는_디스크는_더_작은_대상에_들어간다()
    {
        // 원본 20GB짜리 배치지만 실제 파티션은 앞쪽 ~2.1GB만 차지.
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 2 * Gb) };

        Assert.True(ResizePlanner.LayoutFitsIn(src, 4 * Gb));
        Assert.False(ResizePlanner.LayoutFitsIn(src, 2 * Gb));
    }

    [Fact]
    public void 맞춤_클론은_파티션을_하나도_옮기지_않는다()
    {
        var src = WindowsLayout();          // 20GB 디스크를 채우는 배치
        long lastEnd = src.Max(p => p.EndOffset);
        long target = lastEnd + 8 * Mb;     // 딱 들어갈 만큼만 큰 대상

        var layout = ResizePlanner.PlanFit(src, target);

        Assert.Equal(src.Count, layout.Partitions.Count);
        Assert.Null(layout.GrownPartition);
        foreach (var original in src)
        {
            var placed = layout.Partitions.Single(p => p.SourceNumber == original.Number);
            Assert.Equal(original.StartingOffset, placed.StartingOffset);
            Assert.Equal(original.LengthBytes, placed.LengthBytes);
            Assert.False(placed.Grown);
        }
    }

    [Fact]
    public void 맞춤_클론은_파티션이_안_들어가면_거부한다()
    {
        var src = WindowsLayout();
        long lastEnd = src.Max(p => p.EndOffset);

        // 마지막 파티션 끝보다 작은 대상 — 잘라내면 데이터가 사라지므로 반드시 거부해야 한다.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ResizePlanner.PlanFit(src, lastEnd - 1 * Mb));

        Assert.Contains("들어가지 않습니다", ex.Message);
    }

    [Fact]
    public void 맞춤_클론은_백업GPT_자리를_남긴다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 1 * Gb) };
        long lastEnd = 1 * Mb + 1 * Gb;

        // 파티션 끝과 정확히 같은 크기의 대상은 백업 GPT 자리가 없어 거부돼야 한다.
        Assert.Throws<InvalidOperationException>(() => ResizePlanner.PlanFit(src, lastEnd));

        // 예약분을 더하면 들어간다.
        var layout = ResizePlanner.PlanFit(src, ResizePlanner.MinimumTargetSize(src));
        Assert.Single(layout.Partitions);
    }

    // --- 축소(PlanShrink) — 파티션을 줄이고 뒤 파티션을 왼쪽으로 당김 ----------------

    [Fact]
    public void 가운데_파티션_축소는_뒤_파티션을_당긴다()
    {
        var src = WindowsLayout();                       // 20GB, C:(3)가 대부분
        long cLenBefore = src.Single(p => p.Number == 3).LengthBytes;
        long recStartBefore = src.Single(p => p.Number == 4).StartingOffset;

        var layout = ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var c = layout.Partitions.Single(p => p.SourceNumber == 3);
        var rec = layout.Partitions.Single(p => p.SourceNumber == 4);

        Assert.True(c.Shrunk);
        Assert.False(c.Grown);
        Assert.True(c.LengthBytes < cLenBefore);
        Assert.True(c.LengthBytes >= 5 * Gb);            // 요청 이상(정렬 내림이라 안 작아짐)
        Assert.Equal(src.Single(p => p.Number == 3).StartingOffset, c.StartingOffset); // 시작 불변

        // 복구 파티션은 왼쪽으로 당겨졌고 크기는 그대로.
        Assert.True(rec.StartingOffset < recStartBefore);
        Assert.Equal(500 * Mb, rec.LengthBytes);
        Assert.False(rec.Shrunk);

        // 축소량(delta)만큼 정확히 당겨졌다.
        long delta = cLenBefore - c.LengthBytes;
        Assert.Equal(recStartBefore - delta, rec.StartingOffset);
    }

    [Fact]
    public void 축소량은_1MB_정렬된다()
    {
        var src = WindowsLayout();
        // 정렬 경계에 안 맞는 크기를 요청해도 delta는 1MB 배수여야 한다.
        var layout = ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(3, 5 * Gb + 123_456));

        var rec = layout.Partitions.Single(p => p.SourceNumber == 4);
        long recStartBefore = src.Single(p => p.Number == 4).StartingOffset;
        long delta = recStartBefore - rec.StartingOffset;
        Assert.Equal(0, delta % ResizePlanner.Alignment);
    }

    [Fact]
    public void 축소하면_더_작은_대상에_들어간다()
    {
        var src = WindowsLayout();                        // 원본 20GB 배치
        // C:를 5GB로 줄이면 전체가 ~6GB 안으로 들어와, 8GB 대상에 맞는다.
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        long lastEnd = layout.Partitions.Max(p => p.EndOffset);
        Assert.True(lastEnd <= 8L * Gb - ResizePlanner.EndReserve);
    }

    [Fact]
    public void 마지막_파티션_축소는_뒤에_아무것도_안_당긴다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 10 * Gb) };

        var layout = ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(2, 3 * Gb));

        var p2 = layout.Partitions.Single(p => p.SourceNumber == 2);
        Assert.True(p2.Shrunk);
        Assert.Equal(101 * Mb, p2.StartingOffset);        // 시작 불변
        Assert.True(p2.LengthBytes >= 3 * Gb && p2.LengthBytes < 10 * Gb);
        // 앞 파티션 불변.
        Assert.Equal(1 * Mb, layout.Partitions.Single(p => p.SourceNumber == 1).StartingOffset);
    }

    [Fact]
    public void 앞_파티션은_축소에_영향받지_않는다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(3, 4 * Gb));

        foreach (int n in new[] { 1, 2 })   // ESP, MSR — C:(3) 앞
        {
            var before = src.Single(p => p.Number == n);
            var after = layout.Partitions.Single(p => p.SourceNumber == n);
            Assert.Equal(before.StartingOffset, after.StartingOffset);
            Assert.Equal(before.LengthBytes, after.LengthBytes);
            Assert.False(after.Shrunk);
        }
    }

    [Fact]
    public void 축소_파티션은_ShrunkPartition으로_노출된다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(3, 6 * Gb));

        Assert.NotNull(layout.ShrunkPartition);
        Assert.Equal(3, layout.ShrunkPartition!.SourceNumber);
        Assert.Null(layout.GrownPartition);
    }

    [Fact]
    public void 현재보다_큰_크기는_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 5 * Gb) };

        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(2, 8 * Gb)));
    }

    [Fact]
    public void 축소량이_1MB_미만이면_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 5 * Gb) };

        // 5GB에서 겨우 몇백 KB만 줄이려 하면 1MB 정렬 내림으로 delta=0 → 거부.
        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(2, 5 * Gb - 500_000)));
    }

    [Fact]
    public void 축소_후에도_대상에_안_들어가면_거부된다()
    {
        var src = WindowsLayout();   // 복구 파티션 끝이 ~20GB 근처

        // C:를 18GB로 살짝만 줄여선 6GB 대상에 못 들어간다 → 거부(더 줄이라고 안내).
        Assert.Throws<InvalidOperationException>(() =>
            ResizePlanner.PlanShrink(src, 6 * Gb, new PartitionShrinkRequest(3, 18 * Gb)));
    }

    [Fact]
    public void 없는_파티션_번호는_축소에서도_거부된다()
    {
        var src = new List<PartitionInfo> { Part(1, 1 * Mb, 5 * Gb) };

        Assert.Throws<ArgumentException>(() =>
            ResizePlanner.PlanShrink(src, 20 * Gb, new PartitionShrinkRequest(99, 1 * Gb)));
    }

    [Fact]
    public void 축소_배치도_겹치지_않고_대상_안에_있다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 10 * Gb, new PartitionShrinkRequest(3, 4 * Gb));

        var ps = layout.Partitions.OrderBy(p => p.StartingOffset).ToList();
        for (int i = 1; i < ps.Count; i++)
            Assert.True(ps[i - 1].EndOffset <= ps[i].StartingOffset, "겹치면 안 됨");

        Assert.True(ps[0].StartingOffset >= 0);
        Assert.True(ps[^1].EndOffset <= 10L * Gb - ResizePlanner.EndReserve);
    }
}
