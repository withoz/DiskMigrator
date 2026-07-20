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

    // --- 맞춤 클론: 더 작은 대상 (v0.4.0) ------------------------------------
    //
    // 원본보다 작은 대상에 "파티션만" 복제한 상태를 만듭니다. 실제 맞춤 클론이 만드는 대상과
    // 같습니다 — 파티션 테이블과 각 파티션은 제자리에 복사되고, 원본 끝에 있던 백업 GPT는
    // 대상 크기를 넘으므로 아예 복사되지 않습니다. 그 상태에서 재작성이 백업 헤더를 줄어든
    // 끝에 새로 만들고 경계를 맞춰야 합니다.

    /// <summary>파티션이 앞쪽에만 있는 원본을, 더 작은 대상에 앞부분만 복사한 상태를 만듭니다.</summary>
    private static FaultyBlockDevice FitCloneToSmallerTarget(long sourceSize, long targetSize)
    {
        var source = GptImageBuilder.Build(sourceSize,
            new GptImageBuilder.PartitionSpec(34, 99, G1, Name: "ESP"),
            new GptImageBuilder.PartitionSpec(100, 199, G2, Name: "C"),
            new GptImageBuilder.PartitionSpec(200, 299, G3, Name: "Recovery"));

        var target = new FaultyBlockDevice(targetSize, Sector, id: "target");
        // 대상 크기만큼만 복사 — 원본 끝의 백업 GPT는 넘어오지 않습니다.
        source.AsSpan(0, (int)targetSize).CopyTo(target.Data);
        return target;
    }

    /// <summary>파티션을 옮기지 않는 항등 remap(맞춤 클론이 쓰는 배치).</summary>
    private static PartitionRemap[] IdentityRemaps() =>
    [
        new PartitionRemap(34, 34, 99),
        new PartitionRemap(100, 100, 199),
        new PartitionRemap(200, 200, 299),
    ];

    [Fact]
    public void 작은_대상에서도_파티션_위치와_GUID가_그대로다()
    {
        // 원본 4MB → 대상 2MB. 파티션은 앞쪽 300섹터뿐이라 그대로 들어간다.
        var target = FitCloneToSmallerTarget(8192 * Sector, 4096 * Sector);

        var result = new GptRewriter().Rewrite(target, IdentityRemaps());

        Assert.True(result.Rewritten);
        Assert.Equal(34, GptImageBuilder.ReadEntryStartLba(target.Data, 2, 0));
        Assert.Equal(99, GptImageBuilder.ReadEntryEndLba(target.Data, 2, 0));
        Assert.Equal(200, GptImageBuilder.ReadEntryStartLba(target.Data, 2, 2));
        Assert.Equal(299, GptImageBuilder.ReadEntryEndLba(target.Data, 2, 2));
        // BCD가 GUID로 파티션을 참조하므로 보존이 핵심.
        Assert.Equal(G1, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 0));
        Assert.Equal(G2, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 1));
        Assert.Equal(G3, GptImageBuilder.ReadEntryUniqueGuid(target.Data, 2, 2));
    }

    [Fact]
    public void 작은_대상의_백업_헤더가_새_끝에_생기고_유효하다()
    {
        long targetSize = 4096 * Sector;
        var target = FitCloneToSmallerTarget(8192 * Sector, targetSize);
        long expectedLastLba = (targetSize / Sector) - 1;

        new GptRewriter().Rewrite(target, IdentityRemaps());

        var backup = target.Data.AsSpan((int)(expectedLastLba * Sector), Sector);
        Assert.True(GptImageBuilder.HasGptSignature(backup));
        Assert.True(GptImageBuilder.IsHeaderCrcValid(backup));
        Assert.True(GptImageBuilder.IsEntriesCrcValid(target.Data, backup));
        Assert.Equal(expectedLastLba, GptImageBuilder.ReadMyLba(backup));
        Assert.Equal(1, GptImageBuilder.ReadAlternateLba(backup));

        // 주 헤더도 줄어든 끝을 가리켜야 한다 — 아니면 Windows가 디스크를 손상으로 본다.
        var primary = target.Data.AsSpan(Sector, Sector);
        Assert.True(GptImageBuilder.IsHeaderCrcValid(primary));
        Assert.Equal(expectedLastLba, GptImageBuilder.ReadAlternateLba(primary));
        Assert.Equal(expectedLastLba - GptImageBuilder.EntryArrayLbaCount - 1,
                     GptImageBuilder.ReadLastUsableLba(primary));
    }

    [Fact]
    public void 파티션이_작은_대상을_넘으면_재작성이_거부된다()
    {
        // 대상을 파티션 끝(299섹터)조차 못 담을 만큼 작게 — 조용히 잘라내면 안 된다.
        var target = FitCloneToSmallerTarget(8192 * Sector, 320 * Sector);

        Assert.Throws<InvalidOperationException>(
            () => new GptRewriter().Rewrite(target, IdentityRemaps()));
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
