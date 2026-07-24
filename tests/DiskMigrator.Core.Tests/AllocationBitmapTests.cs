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

    // --- 사용량 측정(MeasureUsage) — 축소 한계 산정 ---------------------------

    [Fact]
    public void 빈_볼륨은_사용_0이고_전체는_클러스터수로_계산된다()
    {
        var u = AllocationBitmap.MeasureUsage(Bitmap(64), 64, Bpc);

        Assert.Equal(64 * Bpc, u.TotalBytes);
        Assert.Equal(0, u.UsedBytes);
        Assert.Equal(0, u.HighestUsedByte);
        Assert.Equal(64 * Bpc, u.FreeBytes);
    }

    [Fact]
    public void 사용_클러스터_수를_바이트로_센다()
    {
        // 클러스터 0,1,2,10 할당 = 4개.
        var u = AllocationBitmap.MeasureUsage(Bitmap(64, 0, 1, 2, 10), 64, Bpc);

        Assert.Equal(4 * Bpc, u.UsedBytes);
        Assert.Equal(60 * Bpc, u.FreeBytes);
    }

    [Fact]
    public void 마지막_사용_클러스터의_끝이_축소_하한이다()
    {
        // 사용은 4클러스터지만 마지막이 클러스터 40 → 하한은 (40+1)*Bpc.
        // 조각난 볼륨: 하한(41)이 사용량(4)보다 훨씬 크다 — 파일을 안 옮기면 여기까지만 줄어든다.
        var u = AllocationBitmap.MeasureUsage(Bitmap(64, 0, 1, 2, 40), 64, Bpc);

        Assert.Equal(41 * Bpc, u.HighestUsedByte);
        Assert.Equal(4 * Bpc, u.UsedBytes);
        Assert.True(u.HighestUsedByte > u.UsedBytes);
    }

    [Fact]
    public void 하한은_사용량_이상이다()
    {
        var u = AllocationBitmap.MeasureUsage(Bitmap(100, 0, 1, 2, 3, 99), 100, Bpc);

        Assert.True(u.HighestUsedByte >= u.UsedBytes);
        Assert.Equal(100 * Bpc, u.HighestUsedByte);   // 클러스터 99가 마지막 → 100*Bpc
        Assert.Equal(5 * Bpc, u.UsedBytes);
    }

    [Fact]
    public void 꼬리_패딩_비트는_사용량에서_무시된다()
    {
        // 1바이트 전부 1이지만 유효 클러스터는 3개 → 사용 3, 하한 3*Bpc.
        var u = AllocationBitmap.MeasureUsage(new byte[] { 0b1111_1111 }, 3, Bpc);

        Assert.Equal(3 * Bpc, u.UsedBytes);
        Assert.Equal(3 * Bpc, u.HighestUsedByte);
    }

    [Fact]
    public void 최고_비트_계산은_바이트_경계를_넘어_정확하다()
    {
        // 클러스터 8(두 번째 바이트의 bit0)만 할당 → 하한은 9*Bpc, 사용 1.
        var u = AllocationBitmap.MeasureUsage(Bitmap(64, 8), 64, Bpc);

        Assert.Equal(1 * Bpc, u.UsedBytes);
        Assert.Equal(9 * Bpc, u.HighestUsedByte);
    }

    [Fact]
    public void 제안_최소_크기는_마지막_사용_끝에_여유를_더한다()
    {
        long gb = 1L << 30;
        // 마지막 사용 끝 100GB, 여유 15% → 115GB (전체 200GB 이내).
        var u = new AllocationBitmap.NtfsUsage(200 * gb, 40 * gb, 100 * gb);

        Assert.Equal(115 * gb, u.SuggestedMinShrinkBytes(0.15, minHeadroomBytes: 0));
    }

    [Fact]
    public void 제안_최소_크기는_전체를_넘지_않는다()
    {
        long gb = 1L << 30;
        // 거의 꽉 찬 볼륨: 190GB + 15% = 218GB > 200GB → 전체로 클램프.
        var u = new AllocationBitmap.NtfsUsage(200 * gb, 180 * gb, 190 * gb);

        Assert.Equal(200 * gb, u.SuggestedMinShrinkBytes(0.15, minHeadroomBytes: 0));
    }

    [Fact]
    public void 제안_최소_크기는_최소_여유분을_보장한다()
    {
        long gb = 1L << 30;
        // 마지막 끝 10GB, 15%=1.5GB지만 최소 여유 2GB가 더 크다 → 12GB.
        var u = new AllocationBitmap.NtfsUsage(200 * gb, 5 * gb, 10 * gb);

        Assert.Equal(12 * gb, u.SuggestedMinShrinkBytes(0.15, minHeadroomBytes: 2L << 30));
    }
}
