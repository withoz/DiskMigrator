using DiskMigrator.Core.Engine;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 할당 비트맵 → 복사 런 변환(스마트 클론의 핵심)을 검증합니다.
/// </summary>
public class AllocationBitmapTests
{
    private const long Bpc = 4096;   // 클러스터 크기
    private const long NoCap = long.MaxValue;

    /// <summary>주어진 클러스터 인덱스들을 할당(비트 켬)으로 표시한 비트맵을 만듭니다.</summary>
    private static byte[] Bitmap(int clusterCount, params int[] allocated)
    {
        var bytes = new byte[(clusterCount + 7) / 8];
        foreach (int c in allocated) bytes[c / 8] |= (byte)(1 << (c % 8));
        return bytes;
    }

    [Fact]
    public void 빈_비트맵은_런이_없다()
    {
        var runs = AllocationBitmap.ExtractRuns(Bitmap(64), 64, Bpc, 0, NoCap);
        Assert.Empty(runs);
    }

    [Fact]
    public void 전부_할당이면_하나의_런()
    {
        var all = Enumerable.Range(0, 64).ToArray();
        var runs = AllocationBitmap.ExtractRuns(Bitmap(64, all), 64, Bpc, 0, NoCap);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].OffsetBytes);
        Assert.Equal(64 * Bpc, runs[0].LengthBytes);
    }

    [Fact]
    public void 큰_빈틈은_별도_런으로_나뉜다()
    {
        // 클러스터 0~1 할당, 큰 빈틈, 클러스터 50~51 할당. 병합 없음(gap=0).
        var runs = AllocationBitmap.ExtractRuns(Bitmap(64, 0, 1, 50, 51), 64, Bpc, 0, NoCap);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new AllocationBitmap.Run(0, 2 * Bpc), runs[0]);
        Assert.Equal(new AllocationBitmap.Run(50 * Bpc, 2 * Bpc), runs[1]);
    }

    [Fact]
    public void 작은_빈틈은_병합된다()
    {
        // 클러스터 0, 3 할당 (사이에 클러스터 1~2 빈틈 = 2클러스터).
        // 병합 한도를 4클러스터(4*Bpc 바이트)로 주면 하나의 런으로 합쳐진다.
        var runs = AllocationBitmap.ExtractRuns(Bitmap(16, 0, 3), 16, Bpc, 4 * Bpc, NoCap);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].OffsetBytes);
        Assert.Equal(4 * Bpc, runs[0].LengthBytes); // 클러스터 0..3 (빈틈 포함)
    }

    [Fact]
    public void 병합_한도보다_큰_빈틈은_안_합친다()
    {
        // 클러스터 0, 3 할당, 빈틈 2클러스터. 병합 한도 1클러스터 → 나뉜다.
        var runs = AllocationBitmap.ExtractRuns(Bitmap(16, 0, 3), 16, Bpc, 1 * Bpc, NoCap);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new AllocationBitmap.Run(0, Bpc), runs[0]);
        Assert.Equal(new AllocationBitmap.Run(3 * Bpc, Bpc), runs[1]);
    }

    [Fact]
    public void 상한을_넘는_런은_잘린다()
    {
        // 클러스터 0~9 전부 할당, 상한을 5클러스터로.
        var all = Enumerable.Range(0, 10).ToArray();
        var runs = AllocationBitmap.ExtractRuns(Bitmap(10, all), 10, Bpc, 0, 5 * Bpc);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].OffsetBytes);
        Assert.Equal(5 * Bpc, runs[0].LengthBytes);
    }

    [Fact]
    public void 비트_순서는_LSB_우선이다()
    {
        // byte 0 = 0b0000_0101 → 클러스터 0과 2 할당.
        var bytes = new byte[] { 0b0000_0101 };
        var runs = AllocationBitmap.ExtractRuns(bytes, 8, Bpc, 0, NoCap);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new AllocationBitmap.Run(0, Bpc), runs[0]);
        Assert.Equal(new AllocationBitmap.Run(2 * Bpc, Bpc), runs[1]);
    }

    [Fact]
    public void clusterCount로_비트맵_꼬리_패딩을_무시한다()
    {
        // 비트맵은 1바이트(8비트)지만 유효 클러스터는 3개뿐. 4~7 비트가 켜져 있어도 무시.
        var bytes = new byte[] { 0b1111_1111 };
        var runs = AllocationBitmap.ExtractRuns(bytes, 3, Bpc, 0, NoCap);

        Assert.Single(runs);
        Assert.Equal(new AllocationBitmap.Run(0, 3 * Bpc), runs[0]);
    }
}
