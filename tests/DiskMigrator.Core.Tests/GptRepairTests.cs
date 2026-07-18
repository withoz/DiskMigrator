using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// GPT 보정은 틀리면 Windows가 클론한 디스크를 "손상됨"으로 표시하고 남는 공간을
/// 쓸 수 없게 되므로, 합성 이미지로 오프셋과 검사합까지 정확히 확인합니다.
/// </summary>
public class GptRepairTests
{
    private const int Sector = GptImageBuilder.SectorSize;

    /// <summary>원본 GPT 이미지를 더 큰 대상에 섹터 복제한 상태를 만듭니다.</summary>
    private static FaultyBlockDevice CloneToLargerDevice(long sourceSize, long targetSize)
    {
        var sourceImage = GptImageBuilder.Build(sourceSize);
        var target = new FaultyBlockDevice(targetSize, Sector, id: "target");

        // 섹터 단위 전체 복제 결과 — 백업 헤더가 대상 디스크 "중간"에 놓입니다.
        sourceImage.CopyTo(target.Data, 0);

        return target;
    }

    [Fact]
    public void 대상이_더_크면_백업_헤더를_디스크_끝으로_옮긴다()
    {
        long sourceSize = 1024 * Sector;  // 512KB
        long targetSize = 2048 * Sector;  // 1MB
        var target = CloneToLargerDevice(sourceSize, targetSize);

        long expectedLastLba = (targetSize / Sector) - 1;

        var result = new GptRepair().RepairIfNeeded(target);

        Assert.True(result.WasRepaired);

        // 백업 헤더가 진짜 마지막 LBA에 있어야 합니다.
        var backupHeader = target.Data.AsSpan((int)(expectedLastLba * Sector), Sector);

        Assert.True(GptImageBuilder.HasGptSignature(backupHeader));
        Assert.True(GptImageBuilder.IsHeaderCrcValid(backupHeader));
        Assert.Equal(expectedLastLba, GptImageBuilder.ReadMyLba(backupHeader));
        Assert.Equal(1, GptImageBuilder.ReadAlternateLba(backupHeader));
    }

    [Fact]
    public void 주_헤더가_새_백업_위치를_가리키고_검사합이_유효하다()
    {
        long targetSize = 2048 * Sector;
        var target = CloneToLargerDevice(1024 * Sector, targetSize);
        long expectedLastLba = (targetSize / Sector) - 1;

        new GptRepair().RepairIfNeeded(target);

        var primary = target.Data.AsSpan(Sector, Sector);

        Assert.True(GptImageBuilder.IsHeaderCrcValid(primary));
        Assert.Equal(1, GptImageBuilder.ReadMyLba(primary));
        Assert.Equal(expectedLastLba, GptImageBuilder.ReadAlternateLba(primary));
    }

    [Fact]
    public void 마지막_사용가능_LBA가_백업_항목_배열_앞까지_확장된다()
    {
        long targetSize = 2048 * Sector;
        var target = CloneToLargerDevice(1024 * Sector, targetSize);

        long lastLba = (targetSize / Sector) - 1;
        long expectedLastUsable = lastLba - GptImageBuilder.EntryArrayLbaCount - 1;

        new GptRepair().RepairIfNeeded(target);

        var primary = target.Data.AsSpan(Sector, Sector);
        var backup = target.Data.AsSpan((int)(lastLba * Sector), Sector);

        Assert.Equal(expectedLastUsable, GptImageBuilder.ReadLastUsableLba(primary));

        // 주 헤더와 백업 헤더의 LastUsableLBA는 반드시 일치해야 합니다.
        Assert.Equal(expectedLastUsable, GptImageBuilder.ReadLastUsableLba(backup));
    }

    [Fact]
    public void 백업_항목_배열이_백업_헤더_바로_앞에_복사된다()
    {
        long targetSize = 2048 * Sector;
        var target = CloneToLargerDevice(1024 * Sector, targetSize);

        long lastLba = (targetSize / Sector) - 1;
        long expectedBackupEntryLba = lastLba - GptImageBuilder.EntryArrayLbaCount;

        new GptRepair().RepairIfNeeded(target);

        var backup = target.Data.AsSpan((int)(lastLba * Sector), Sector);
        Assert.Equal(expectedBackupEntryLba, GptImageBuilder.ReadPartitionEntryLba(backup));

        // 백업 항목 배열의 내용이 주 항목 배열과 같아야 합니다.
        var primaryEntries = target.Data.AsSpan(2 * Sector, GptImageBuilder.EntryCount * GptImageBuilder.EntrySize);
        var backupEntries = target.Data.AsSpan(
            (int)(expectedBackupEntryLba * Sector), GptImageBuilder.EntryCount * GptImageBuilder.EntrySize);

        Assert.True(primaryEntries.SequenceEqual(backupEntries));
    }

    [Fact]
    public void 보호_MBR의_크기가_대상_디스크_전체를_덮도록_갱신된다()
    {
        long targetSize = 2048 * Sector;
        var target = CloneToLargerDevice(1024 * Sector, targetSize);

        new GptRepair().RepairIfNeeded(target);

        uint sizeInSectors = BitConverter.ToUInt32(target.Data, 446 + 12);

        Assert.Equal((uint)((targetSize / Sector) - 1), sizeInSectors);
    }

    [Fact]
    public void 크기가_같으면_아무것도_바꾸지_않는다()
    {
        long size = 1024 * Sector;
        var target = new FaultyBlockDevice(size, Sector, id: "target");
        GptImageBuilder.Build(size).CopyTo(target.Data, 0);

        var before = target.Data.ToArray();

        var result = new GptRepair().RepairIfNeeded(target);

        Assert.False(result.WasRepaired);
        Assert.Equal(before, target.Data);
    }

    [Fact]
    public void GPT가_아닌_디스크는_건드리지_않는다()
    {
        var target = new FaultyBlockDevice(1024 * Sector, Sector, id: "target").FillWithPattern();
        var before = target.Data.ToArray();

        var result = new GptRepair().RepairIfNeeded(target);

        Assert.False(result.WasRepaired);
        Assert.Contains("GPT 디스크가 아니므로", result.Description);
        Assert.Equal(before, target.Data);
    }

    [Fact]
    public void 보정_후에도_파티션_데이터_영역은_그대로다()
    {
        long sourceSize = 1024 * Sector;
        long targetSize = 2048 * Sector;
        var target = CloneToLargerDevice(sourceSize, targetSize);

        // 파티션 데이터 영역에 표식을 남깁니다.
        int dataStart = (int)(GptImageBuilder.FirstUsableLba * Sector);
        var marker = new byte[Sector];
        new Random(999).NextBytes(marker);
        marker.CopyTo(target.Data, dataStart);

        new GptRepair().RepairIfNeeded(target);

        Assert.Equal(marker, target.Data.AsSpan(dataStart, Sector).ToArray());
    }

    [Fact]
    public void 읽기_전용_장치에는_보정을_시도하지_않는다()
    {
        var target = new FaultyBlockDevice(1024 * Sector, Sector, canWrite: false, id: "ro");

        Assert.Throws<InvalidOperationException>(() => new GptRepair().RepairIfNeeded(target));
    }

    [Fact]
    public void 합성_이미지_자체가_유효한_GPT다()
    {
        // 빌더가 틀렸다면 위의 모든 테스트가 의미를 잃으므로 빌더부터 검증합니다.
        long size = 1024 * Sector;
        var image = GptImageBuilder.Build(size);
        long lastLba = (size / Sector) - 1;

        var primary = image.AsSpan(Sector, Sector);
        var backup = image.AsSpan((int)(lastLba * Sector), Sector);

        Assert.True(GptImageBuilder.HasGptSignature(primary));
        Assert.True(GptImageBuilder.IsHeaderCrcValid(primary));
        Assert.True(GptImageBuilder.HasGptSignature(backup));
        Assert.True(GptImageBuilder.IsHeaderCrcValid(backup));
        Assert.Equal(lastLba, GptImageBuilder.ReadAlternateLba(primary));
        Assert.Equal(1, GptImageBuilder.ReadAlternateLba(backup));
    }
}

public class Crc32Tests
{
    // CRC-32/IEEE의 널리 알려진 검증 벡터입니다. 이 값이 맞아야 GPT 검사합도 맞습니다.
    [Fact]
    public void 표준_검증_벡터와_일치한다()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void 빈_입력은_0이다()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void 한_바이트만_달라도_값이_달라진다()
    {
        Assert.NotEqual(Crc32.Compute("hello"u8), Crc32.Compute("hellp"u8));
    }
}
