using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 축소 복원 복사 계획(축소된 이미지 → 더 작은 대상, 순수 좌표 계산)을 검증합니다.
/// </summary>
public class ShrinkRestorePlannerTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;
    private const int Sector = 512;

    private static PartitionInfo Part(int number, long start, long length) =>
        new() { Number = number, StartingOffset = start, LengthBytes = length };

    /// <summary>[ESP 100MB][MSR 16MB][C: 나머지][복구 500MB], 원본 20GB.</summary>
    private static List<PartitionInfo> WindowsLayout()
    {
        long esp = 1 * Mb, espLen = 100 * Mb;
        long msr = esp + espLen, msrLen = 16 * Mb;
        long c = msr + msrLen;
        long recLen = 500 * Mb;
        long recStart = 20 * Gb - recLen - 2 * Mb;
        long cLen = recStart - c;
        return [Part(1, esp, espLen), Part(2, msr, msrLen), Part(3, c, cLen), Part(4, recStart, recLen)];
    }

    [Fact]
    public void 맨_앞에_GPT_영역_구간이_있다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);

        var gpt = plan.Regions[0];
        Assert.Equal(0, gpt.SourceOffset);
        Assert.Equal(0, gpt.TargetOffset);
        Assert.Equal(1 * Mb, gpt.Length);   // 첫 파티션이 1MB에서 시작 → [0,1MB) 복사
    }

    [Fact]
    public void 구간_수는_GPT_하나_더하기_파티션_수()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);

        Assert.Equal(1 + src.Count, plan.Regions.Count);
        Assert.Equal(src.Count, plan.Remaps.Count);
    }

    [Fact]
    public void 축소_파티션은_제자리에서_읽고_쓰며_길이는_줄어든다()
    {
        var src = WindowsLayout();
        long cLenBefore = src.Single(p => p.Number == 3).LengthBytes;
        long cStart = src.Single(p => p.Number == 3).StartingOffset;
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);
        var cRegion = plan.Regions.Single(r => r.Description.Contains("파티션 3"));

        Assert.Equal(cStart, cRegion.SourceOffset);   // 시작 불변
        Assert.Equal(cStart, cRegion.TargetOffset);
        Assert.True(cRegion.Length < cLenBefore);       // 줄어듦
        Assert.True(cRegion.Length >= 5 * Gb);          // 요청 이상
    }

    [Fact]
    public void 뒤_파티션은_원래_위치에서_읽어_왼쪽으로_옮겨_쓴다()
    {
        var src = WindowsLayout();
        long recStart = src.Single(p => p.Number == 4).StartingOffset;
        long cLenBefore = src.Single(p => p.Number == 3).LengthBytes;
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);
        var recRegion = plan.Regions.Single(r => r.Description.Contains("파티션 4"));
        long cLenAfter = plan.Regions.Single(r => r.Description.Contains("파티션 3")).Length;
        long delta = cLenBefore - cLenAfter;

        Assert.Equal(recStart, recRegion.SourceOffset);            // 자식에서는 원래 위치
        Assert.Equal(recStart - delta, recRegion.TargetOffset);    // 대상에서는 delta만큼 앞으로
        Assert.Equal(500 * Mb, recRegion.Length);                  // 크기 불변
    }

    [Fact]
    public void 앞_파티션은_그대로_복사된다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);

        foreach (int n in new[] { 1, 2 })
        {
            var before = src.Single(p => p.Number == n);
            var region = plan.Regions.Single(r => r.Description.Contains($"파티션 {n}"));
            Assert.Equal(before.StartingOffset, region.SourceOffset);
            Assert.Equal(before.StartingOffset, region.TargetOffset);
            Assert.Equal(before.LengthBytes, region.Length);
        }
    }

    [Fact]
    public void remap의_LBA가_배치와_섹터로_정확히_계산된다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);

        foreach (var tp in layout.Partitions)
        {
            var srcPart = src.Single(p => p.Number == tp.SourceNumber);
            var remap = plan.Remaps.Single(r => r.OldStartLba == srcPart.StartingOffset / Sector);
            Assert.Equal(tp.StartingOffset / Sector, remap.NewStartLba);
            Assert.Equal((tp.StartingOffset + tp.LengthBytes) / Sector - 1, remap.NewEndLba);
        }
    }

    [Fact]
    public void 뒤_파티션의_remap은_왼쪽으로_이동한다()
    {
        var src = WindowsLayout();
        long recStartLba = src.Single(p => p.Number == 4).StartingOffset / Sector;
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        var plan = ShrinkRestorePlanner.Build(src, layout, Sector);
        var recRemap = plan.Remaps.Single(r => r.OldStartLba == recStartLba);

        Assert.True(recRemap.NewStartLba < recRemap.OldStartLba);   // 왼쪽으로
    }

    [Fact]
    public void 빈_원본은_거부된다()
    {
        var layout = new ResizeLayout { Partitions = [] };
        Assert.Throws<ArgumentException>(() =>
            ShrinkRestorePlanner.Build([], layout, Sector));
    }

    [Fact]
    public void 잘못된_섹터_크기는_거부된다()
    {
        var src = WindowsLayout();
        var layout = ResizePlanner.PlanShrink(src, 8 * Gb, new PartitionShrinkRequest(3, 5 * Gb));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShrinkRestorePlanner.Build(src, layout, 0));
    }
}
