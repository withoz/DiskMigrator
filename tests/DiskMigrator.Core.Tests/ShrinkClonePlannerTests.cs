using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 축소 클론 판정(대상이 원본보다 작을 때 자동 축소 가능 여부, 순수 계산)을 검증합니다.
/// </summary>
public class ShrinkClonePlannerTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    private static PartitionInfo Part(
        int number, long start, long length, string? fs = null, long? free = null) =>
        new()
        {
            Number = number, StartingOffset = start, LengthBytes = length,
            FileSystem = fs, FreeSpaceBytes = free,
        };

    /// <summary>[ESP 100MB][C: NTFS 18GB, 실사용 4GB] — 원본 20GB.</summary>
    private static List<PartitionInfo> Layout(long used = 4 * Gb)
    {
        long cLen = 18 * Gb;
        return
        [
            Part(1, 1 * Mb, 100 * Mb, "FAT32"),
            Part(2, 101 * Mb, cLen, "NTFS", free: cLen - used),
        ];
    }

    [Fact]
    public void 대상이_충분히_크면_축소가_필요없다()
    {
        var d = ShrinkClonePlanner.Evaluate(Layout(), 30 * Gb, out string? blocked);

        Assert.Null(d);
        Assert.Null(blocked);   // 사유 없음 = 축소 불필요(맞춤/일반 클론 경로)
    }

    [Fact]
    public void 실사용이_들어가면_축소_클론을_허용한다()
    {
        // 파티션 끝 ~18.1GB, 대상 10GB, 실사용 4GB → 축소하면 들어감.
        var d = ShrinkClonePlanner.Evaluate(Layout(), 10 * Gb, out string? blocked);

        Assert.NotNull(d);
        Assert.Null(blocked);
        Assert.Equal(2, d!.PartitionNumber);            // 가장 큰 NTFS
        Assert.Equal(18 * Gb, d.CurrentBytes);
        Assert.True(d.NewBytes < 10 * Gb);              // 대상 안에 들어가는 크기
        Assert.True(d.NewBytes >= d.EstimatedUsedBytes + ShrinkClonePlanner.UsedHeadroomBytes);
        Assert.Equal(4 * Gb, d.EstimatedUsedBytes);
    }

    [Fact]
    public void 목표가_대상_한계에_정렬돼_들어간다()
    {
        var src = Layout();
        long target = 10 * Gb;
        var d = ShrinkClonePlanner.Evaluate(src, target, out _)!;

        // 축소 후 파티션 끝이 대상 한계(정렬+예약) 안이어야 한다.
        long cStart = src.Single(p => p.Number == 2).StartingOffset;
        long newEnd = cStart + d.NewBytes;
        long maxEnd = target - target % ResizePlanner.Alignment - ResizePlanner.EndReserve;
        Assert.True(newEnd <= maxEnd);
    }

    [Fact]
    public void NTFS가_없으면_사유와_함께_거부한다()
    {
        var src = new List<PartitionInfo>
        {
            Part(1, 1 * Mb, 100 * Mb, "FAT32"),
            Part(2, 101 * Mb, 18 * Gb, "exFAT"),
        };

        var d = ShrinkClonePlanner.Evaluate(src, 10 * Gb, out string? blocked);

        Assert.Null(d);
        Assert.NotNull(blocked);
    }

    [Fact]
    public void 실사용이_너무_많으면_사유와_함께_거부한다()
    {
        // 실사용 15GB인데 대상 10GB → 줄여도 못 들어감.
        var d = ShrinkClonePlanner.Evaluate(Layout(used: 15 * Gb), 10 * Gb, out string? blocked);

        Assert.Null(d);
        Assert.NotNull(blocked);
    }

    [Fact]
    public void 사용량을_모르면_실행시_검증에_맡기고_허용한다()
    {
        // FreeSpaceBytes 미상(마운트 안 됨) — 시작은 허용하고 diskpart가 실제 한계를 강제.
        var src = new List<PartitionInfo>
        {
            Part(1, 1 * Mb, 100 * Mb, "FAT32"),
            Part(2, 101 * Mb, 18 * Gb, "NTFS"),
        };

        var d = ShrinkClonePlanner.Evaluate(src, 10 * Gb, out string? blocked);

        Assert.NotNull(d);
        Assert.Null(blocked);
        Assert.Equal(-1, d!.EstimatedUsedBytes);
    }

    [Fact]
    public void 대상이_지나치게_작으면_거부한다()
    {
        // 1GB 밑으로 줄여야 들어가는 대상.
        var d = ShrinkClonePlanner.Evaluate(Layout(used: 100 * Mb), 1 * Gb, out string? blocked);

        Assert.Null(d);
        Assert.NotNull(blocked);
    }
}
