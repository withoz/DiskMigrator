using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// GPT 재작성(리사이즈)은 파티션 GUID를 보존하면서 위치만 옮기고 CRC를 다시 맞춰야 합니다.
/// 틀리면 Windows가 디스크를 손상으로 표시하거나 클론이 부팅되지 않으므로 합성 이미지로
/// 오프셋·GUID·검사합을 정확히 확인합니다.
/// </summary>
public class GptRewriterTests
{
    private const int Sector = GptImageBuilder.SectorSize;

    private static readonly Guid G1 = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid G2 = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid G3 = new("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// [P1 34..99][P2 100..199][P3 200..299] 3파티션 원본을 더 큰 대상에 섹터 복제한 상태를 만듭니다.
    /// </summary>
    private static FaultyBlockDevice CloneThreePartitions(long sourceSize, long targetSize)
    {
        var source = GptImageBuilder.Build(sourceSize,
            new GptImageBuilder.PartitionSpec(34, 99, G1, Name: "ESP"),
            new GptImageBuilder.PartitionSpec(100, 199, G2, Name: "C"),
            new GptImageBuilder.PartitionSpec(200, 299, G3, Name: "Recovery"));

        var target = new FaultyBlockDevice(targetSize, Sector, id: "target");
        source.CopyTo(target.Data, 0);   // 섹터 단위 전체 복제(백업 헤더는 대상 중간에 놓임)
        return target;
    }

    [Fact]
    public void 가운데_확대와_뒤_시프트를_반영한다()
    {
        long sourceSize = 4096 * Sector;   // 2MB
        long targetSize = 8192 * Sector;   // 4MB
        var target = CloneThreePartitions(sourceSize, targetSize);

        // P2를 200섹터 확대, P3를 200섹터 뒤로.
        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),     // P1 그대로
            new PartitionRemap(100, 100, 399),  // P2: 100..199 → 100..399
            new PartitionRemap(200, 400, 499),  // P3: 200..299 → 400..499
        };

        var result = new GptRewriter().Rewrite(target, remaps);
        Assert.True(result.Rewritten);

        Assert.Equal(34, GptImageBuilder.ReadEntryStartLba(target.Data, 2, 0));
        Assert.Equal(99, GptImageBuilder.ReadEntryEndLba(target.Data, 2, 0));
        Assert.Equal(100, GptImageBuilder.ReadEntryStartLba(target.Data, 2, 1));
        Assert.Equal(399, GptImageBuilder.ReadEntryEndLba(target.Data, 2, 1));
        Assert.Equal(400, GptImageBuilder.ReadEntryStartLba(target.Data, 2, 2));
        Assert.Equal(499, GptImageBuilder.ReadEntryEndLba(target.Data, 2, 2));
    }

    [Fact]
    public void 고유_GUID와_이름을_보존한다()
    {
        var target = CloneThreePartitions(4096 * Sector, 8192 * Sector);
        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
            new PartitionRemap(200, 400, 499),
        };

        new GptRewriter().Rewrite(target, remaps);

        Assert.Equal(G1, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 0));
        Assert.Equal(G2, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 1));
        Assert.Equal(G3, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 2));
        Assert.Equal("ESP", GptImageBuilder.ReadEntryName(target.Data, 2, 0));
        Assert.Equal("C", GptImageBuilder.ReadEntryName(target.Data, 2, 1));
        Assert.Equal("Recovery", GptImageBuilder.ReadEntryName(target.Data, 2, 2));
    }

    [Fact]
    public void 주_헤더와_엔트리_CRC가_유효하다()
    {
        long targetSize = 8192 * Sector;
        var target = CloneThreePartitions(4096 * Sector, targetSize);
        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
            new PartitionRemap(200, 400, 499),
        };

        new GptRewriter().Rewrite(target, remaps);

        var primary = target.Data.AsSpan(Sector, Sector);
        Assert.True(GptImageBuilder.IsHeaderCrcValid(primary));
        Assert.True(GptImageBuilder.IsEntriesCrcValid(target.Data, primary));
        Assert.Equal(1, GptImageBuilder.ReadMyLba(primary));
    }

    [Fact]
    public void 백업_헤더를_디스크_끝으로_옮기고_유효하다()
    {
        long targetSize = 8192 * Sector;
        var target = CloneThreePartitions(4096 * Sector, targetSize);
        long expectedLastLba = (targetSize / Sector) - 1;

        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
            new PartitionRemap(200, 400, 499),
        };

        new GptRewriter().Rewrite(target, remaps);

        var backup = target.Data.AsSpan((int)(expectedLastLba * Sector), Sector);
        Assert.True(GptImageBuilder.HasGptSignature(backup));
        Assert.True(GptImageBuilder.IsHeaderCrcValid(backup));
        Assert.True(GptImageBuilder.IsEntriesCrcValid(target.Data, backup));
        Assert.Equal(expectedLastLba, GptImageBuilder.ReadMyLba(backup));
        Assert.Equal(1, GptImageBuilder.ReadAlternateLba(backup));
    }

    [Fact]
    public void 마지막_사용가능_LBA가_대상_끝에_맞춰_갱신된다()
    {
        long targetSize = 8192 * Sector;
        var target = CloneThreePartitions(4096 * Sector, targetSize);
        long lastLba = (targetSize / Sector) - 1;
        long expectedLastUsable = (lastLba - GptImageBuilder.EntryArrayLbaCount) - 1;

        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
            new PartitionRemap(200, 400, 499),
        };

        new GptRewriter().Rewrite(target, remaps);

        var primary = target.Data.AsSpan(Sector, Sector);
        Assert.Equal(expectedLastUsable, GptImageBuilder.ReadLastUsableLba(primary));
    }

    [Fact]
    public void 대응_remap이_없는_사용중_파티션은_거부된다()
    {
        var target = CloneThreePartitions(4096 * Sector, 8192 * Sector);

        // P3(200)에 대한 remap을 뺐다 — 사용 중인데 배치에 없으니 막아야 한다.
        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
        };

        Assert.Throws<InvalidOperationException>(() => new GptRewriter().Rewrite(target, remaps));
    }

    [Fact]
    public void 대응_파티션이_없는_remap은_거부된다()
    {
        var target = CloneThreePartitions(4096 * Sector, 8192 * Sector);

        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 399),
            new PartitionRemap(200, 400, 499),
            new PartitionRemap(9999, 600, 699),  // 이런 파티션 없음
        };

        Assert.Throws<InvalidOperationException>(() => new GptRewriter().Rewrite(target, remaps));
    }

    [Fact]
    public void 새_배치가_사용가능_영역을_넘으면_거부된다()
    {
        long targetSize = 8192 * Sector;
        var target = CloneThreePartitions(4096 * Sector, targetSize);

        // P3 끝을 대상 백업 GPT 영역 너머로 밀어버린다.
        var remaps = new[]
        {
            new PartitionRemap(34, 34, 99),
            new PartitionRemap(100, 100, 199),
            new PartitionRemap(200, 200, targetSize / Sector - 1),  // 마지막 LBA까지 = 백업 GPT와 충돌
        };

        Assert.Throws<InvalidOperationException>(() => new GptRewriter().Rewrite(target, remaps));
    }

    [Fact]
    public void GPT가_아니면_거부된다()
    {
        var target = new FaultyBlockDevice(4096 * Sector, Sector, id: "blank");

        Assert.Throws<InvalidOperationException>(() =>
            new GptRewriter().Rewrite(target, [new PartitionRemap(34, 34, 99)]));
    }
}
