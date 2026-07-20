using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 배치 막대의 구간 계산을 검증합니다. GUI는 관리자 권한 앱이라 클릭-스루 자동화가 불가능하므로,
/// 화면에 그려질 값이 맞는지는 이 순수 계산 단계에서 확인해야 합니다.
/// </summary>
public class DiskLayoutMapTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    private static PartitionInfo Part(int number, long start, long length, string? letter = null,
                                      long? free = null) =>
        new()
        {
            Number = number,
            StartingOffset = start,
            LengthBytes = length,
            DriveLetter = letter,
            FileSystem = letter is null ? null : "NTFS",
            FreeSpaceBytes = free,
        };

    private static DiskInfo Disk(long size, params PartitionInfo[] parts) => new()
    {
        DeviceNumber = 0,
        Model = "Test",
        SizeBytes = size,
        LogicalSectorSize = 512,
        PartitionStyle = PartitionStyle.Gpt,
        Partitions = parts,
    };

    [Fact]
    public void 표시_비율의_합은_항상_1이다()
    {
        // 막대가 컨테이너에 딱 맞으려면 합이 정확히 1이어야 한다.
        var disk = Disk(1 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 300 * Mb));

        var spans = DiskLayoutMap.Build(disk);

        Assert.Equal(1.0, spans.Sum(s => s.DisplayFraction), precision: 9);
    }

    [Fact]
    public void 마지막_파티션_뒤_빈_공간을_구간으로_만든다()
    {
        // 1GB 디스크에 파티션은 앞쪽 ~400MB만 — 뒤쪽 빈 공간이 보여야 한다(맞춤 클론 판단의 핵심).
        var disk = Disk(1 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 300 * Mb));

        var spans = DiskLayoutMap.Build(disk);

        var tail = spans.Last();
        Assert.Equal(DiskSpanKind.Unallocated, tail.Kind);
        Assert.Equal(401 * Mb, tail.StartOffset);
        Assert.Equal(1 * Gb - 401 * Mb, tail.LengthBytes);
        Assert.Equal(1 * Gb - 401 * Mb, DiskLayoutMap.TrailingFreeBytes(disk));
    }

    [Fact]
    public void 파티션_사이의_큰_틈은_미할당으로_보인다()
    {
        var disk = Disk(2 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 500 * Mb, 100 * Mb));

        var spans = DiskLayoutMap.Build(disk);

        var middle = spans.Single(s =>
            s.Kind == DiskSpanKind.Unallocated && s.StartOffset == 101 * Mb);
        Assert.Equal(399 * Mb, middle.LengthBytes);
    }

    [Fact]
    public void 정렬_여백_수준의_작은_틈은_무시한다()
    {
        // 첫 파티션 앞 1MB(GPT 헤더 영역)까지 '미할당'으로 그리면 노이즈만 된다.
        var disk = Disk(1 * Gb, Part(1, 1 * Mb, 500 * Mb));

        var spans = DiskLayoutMap.Build(disk);

        Assert.DoesNotContain(spans, s => s.Kind == DiskSpanKind.Unallocated && s.StartOffset == 0);
        Assert.Equal(DiskSpanKind.Partition, spans[0].Kind);
    }

    [Fact]
    public void 아주_작은_파티션도_보이는_최소_폭을_받는다()
    {
        // 1TB의 100MB ESP는 0.01% — 그대로 그리면 한 픽셀도 안 된다.
        var disk = Disk(1000 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 900 * Gb));

        var spans = DiskLayoutMap.Build(disk);

        var esp = spans.First(s => s.Partition?.Number == 1);
        Assert.True(esp.TrueFraction < 0.001, "실제 비율은 아주 작아야 한다");
        Assert.True(esp.DisplayFraction >= 0.015, $"표시 비율이 너무 작다: {esp.DisplayFraction}");
    }

    [Fact]
    public void 진짜_비율은_보정과_무관하게_실제_크기를_반영한다()
    {
        var disk = Disk(1000 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 500 * Gb));

        var spans = DiskLayoutMap.Build(disk);

        var big = spans.First(s => s.Partition?.Number == 2);
        Assert.Equal(0.5, big.TrueFraction, precision: 2);
    }

    [Fact]
    public void 파티션이_없으면_디스크_전체가_미할당_한_조각이다()
    {
        var disk = Disk(500 * Gb);

        var spans = DiskLayoutMap.Build(disk);

        var only = Assert.Single(spans);
        Assert.Equal(DiskSpanKind.Unallocated, only.Kind);
        Assert.Equal(500 * Gb, only.LengthBytes);
        Assert.Equal(1.0, only.DisplayFraction, precision: 9);
        Assert.Equal(0, DiskLayoutMap.OccupiedEnd(disk));
    }

    [Fact]
    public void 차지한_끝은_마지막_파티션의_끝이다()
    {
        var disk = Disk(1 * Gb, Part(1, 1 * Mb, 100 * Mb), Part(2, 101 * Mb, 300 * Mb));

        Assert.Equal(401 * Mb, DiskLayoutMap.OccupiedEnd(disk));
    }

    [Fact]
    public void 디스크_끝을_넘는_파티션은_잘라_맞춘다()
    {
        // 열거가 이상해도 막대가 깨지거나 비율이 1을 넘으면 안 된다.
        var disk = Disk(1 * Gb, Part(1, 900 * Mb, 500 * Mb));

        var spans = DiskLayoutMap.Build(disk);

        Assert.All(spans, s => Assert.True(s.EndOffset <= 1 * Gb));
        Assert.Equal(1.0, spans.Sum(s => s.DisplayFraction), precision: 9);
    }

    [Fact]
    public void 크기가_0인_디스크는_빈_목록을_돌려준다()
    {
        Assert.Empty(DiskLayoutMap.Build(Disk(0)));
    }

    [Fact]
    public void 사용량_정보가_구간에_실려_온다()
    {
        // 200GB 중 50GB 여유 → 화면에서 사용 150GB로 그려져야 한다.
        var disk = Disk(500 * Gb, Part(1, 1 * Mb, 200 * Gb, letter: "C", free: 50 * Gb));

        var span = DiskLayoutMap.Build(disk).First(s => s.Partition is not null);

        Assert.Equal(50 * Gb, span.Partition!.FreeSpaceBytes);
        Assert.Equal(200 * Gb, span.LengthBytes);
    }
}
