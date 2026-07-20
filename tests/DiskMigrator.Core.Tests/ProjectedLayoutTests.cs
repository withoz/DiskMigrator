using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// "복제가 끝나면 이렇게 됩니다" 미리보기 계산을 검증합니다.
/// 화면에 그려질 값이라 GUI 없이 확인할 수 있는 유일한 지점입니다.
/// </summary>
public class ProjectedLayoutTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    private static PartitionInfo Part(int number, long start, long length,
                                      string? letter = null, long? free = null) =>
        new()
        {
            Number = number,
            StartingOffset = start,
            LengthBytes = length,
            DriveLetter = letter,
            FileSystem = letter is null ? null : "NTFS",
            FreeSpaceBytes = free,
        };

    /// <summary>전형적 Windows 배치 [ESP 100MB][C: 800MB][복구 100MB], 원본 1GB.</summary>
    private static DiskInfo Windows1Gb() => new()
    {
        DeviceNumber = 0,
        Model = "Src",
        SizeBytes = 1 * Gb,
        LogicalSectorSize = 512,
        PartitionStyle = PartitionStyle.Gpt,
        Partitions =
        [
            Part(1, 1 * Mb, 100 * Mb),
            Part(2, 101 * Mb, 800 * Mb, letter: "C", free: 500 * Mb),
            Part(3, 901 * Mb, 100 * Mb),
        ],
    };

    [Fact]
    public void 그대로_두면_배치가_바뀌지_않는다()
    {
        var src = Windows1Gb();

        var after = ProjectedLayout.After(src, 4 * Gb, FreeSpaceMode.Leave);

        Assert.NotNull(after);
        Assert.Equal(3, after!.Count);
        foreach (var original in src.Partitions)
        {
            var p = after.Single(x => x.Number == original.Number);
            Assert.Equal(original.StartingOffset, p.StartingOffset);
            Assert.Equal(original.LengthBytes, p.LengthBytes);
        }
    }

    [Fact]
    public void 마지막_파티션에_합치면_맨_뒤가_커진다()
    {
        var src = Windows1Gb();

        var after = ProjectedLayout.After(src, 4 * Gb, FreeSpaceMode.ExpandLast)!;

        // 앞 두 개는 그대로, 마지막(복구)만 커진다 — 사용자가 원한 게 C: 였다면 이게 함정이다.
        Assert.Equal(100 * Mb, after.Single(p => p.Number == 1).LengthBytes);
        Assert.Equal(800 * Mb, after.Single(p => p.Number == 2).LengthBytes);

        var last = after.Single(p => p.Number == 3);
        Assert.True(last.LengthBytes > 3 * Gb, $"복구가 커져야 한다: {last.LengthBytes}");
        Assert.Equal(901 * Mb, last.StartingOffset);   // 시작은 그대로
    }

    [Fact]
    public void 고른_파티션을_넓히면_뒤_파티션이_밀린다()
    {
        var src = Windows1Gb();

        var after = ProjectedLayout.After(
            src, 4 * Gb, FreeSpaceMode.GrowPartition, new PartitionGrowRequest(2, null))!;

        var c = after.Single(p => p.Number == 2);
        var recovery = after.Single(p => p.Number == 3);

        Assert.True(c.LengthBytes > 3 * Gb, $"C: 가 커져야 한다: {c.LengthBytes}");
        Assert.Equal(101 * Mb, c.StartingOffset);              // 시작은 그대로
        Assert.Equal(100 * Mb, recovery.LengthBytes);          // 복구는 크기 유지
        Assert.True(recovery.StartingOffset > 901 * Mb, "복구는 오른쪽으로 밀린다");
    }

    [Fact]
    public void 넓힌_파티션의_여유_공간이_다시_계산된다()
    {
        // 800MB 중 500MB 여유(=사용 300MB)인 C: 를 넓히면, 늘어난 만큼이 여유가 되어야 한다.
        // 그대로 두면 미리보기 막대가 "넓혔는데 꽉 차 보이는" 이상한 그림이 된다.
        var src = Windows1Gb();

        var after = ProjectedLayout.After(
            src, 4 * Gb, FreeSpaceMode.GrowPartition, new PartitionGrowRequest(2, null))!;

        var c = after.Single(p => p.Number == 2);
        long used = c.LengthBytes - c.FreeSpaceBytes!.Value;

        Assert.Equal(300 * Mb, used);                                   // 사용량은 그대로
        Assert.True(c.FreeSpaceBytes > 2 * Gb, "여유가 늘어야 한다");
    }

    [Fact]
    public void 볼륨_정보는_유지된다()
    {
        var src = Windows1Gb();

        var after = ProjectedLayout.After(
            src, 4 * Gb, FreeSpaceMode.GrowPartition, new PartitionGrowRequest(2, null))!;

        var c = after.Single(p => p.Number == 2);
        Assert.Equal("C", c.DriveLetter);
        Assert.Equal("NTFS", c.FileSystem);
    }

    [Fact]
    public void 파티션이_대상에_안_들어가면_미리보기가_없다()
    {
        var src = Windows1Gb();

        // 대상이 파티션 끝(1GB)보다 작다.
        Assert.Null(ProjectedLayout.After(src, 512 * Mb, FreeSpaceMode.Leave));
    }

    [Fact]
    public void 원본이_없으면_미리보기가_없다()
    {
        Assert.Null(ProjectedLayout.After(null, 4 * Gb, FreeSpaceMode.Leave));
    }

    /// <summary>
    /// 파티션이 사용 가능 영역 끝(디스크 - 백업GPT 예약)까지 정확히 차 있는 1GB 디스크.
    /// </summary>
    /// <remarks>
    /// "원본과 대상이 같은 크기면 여유가 없다"는 틀린 가정입니다 — 디스크 끝과 마지막 파티션
    /// 끝 사이에는 보통 자투리가 남습니다. 진짜 여유 0을 만들려면 끝까지 채워야 합니다.
    /// </remarks>
    private static DiskInfo Full1Gb() => new()
    {
        DeviceNumber = 0,
        Model = "Full",
        SizeBytes = 1 * Gb,
        LogicalSectorSize = 512,
        PartitionStyle = PartitionStyle.Gpt,
        Partitions =
        [
            Part(1, 1 * Mb, 100 * Mb),
            Part(2, 101 * Mb, 1 * Gb - 1 * Mb - 101 * Mb, letter: "C", free: 100 * Mb),
        ],
    };

    [Fact]
    public void 여유가_없으면_넓히기_미리보기가_없다()
    {
        var src = Full1Gb();

        Assert.Null(ProjectedLayout.After(
            src, 1 * Gb, FreeSpaceMode.GrowPartition, new PartitionGrowRequest(2, null)));
    }

    [Fact]
    public void 여유가_없으면_합치기가_아무것도_바꾸지_않는다()
    {
        var src = Full1Gb();
        long before = src.Partitions.Single(p => p.Number == 2).LengthBytes;

        var after = ProjectedLayout.After(src, 1 * Gb, FreeSpaceMode.ExpandLast)!;

        Assert.Equal(before, after.Single(p => p.Number == 2).LengthBytes);
    }

    [Fact]
    public void 디스크_끝의_자투리도_합치기에_쓰인다()
    {
        // 파티션이 1001MB에서 끝나고 디스크는 1024MB — 백업GPT 예약을 뺀 1023MB까지 늘어난다.
        // "같은 크기면 늘릴 게 없다"는 직관이 틀리는 지점이라 명시해 둔다.
        var src = Windows1Gb();

        var after = ProjectedLayout.After(src, 1 * Gb, FreeSpaceMode.ExpandLast)!;

        var last = after.Single(p => p.Number == 3);
        Assert.Equal(1 * Gb - 1 * Mb, last.EndOffset);
    }
}
