using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Tests;

public class PartitionInfoTests
{
    private static PartitionInfo Make(Guid? gptType = null, byte? mbrType = null) =>
        new()
        {
            Number = 2,
            StartingOffset = 1L << 20,
            LengthBytes = 500L << 20,
            GptPartitionType = gptType,
            MbrPartitionType = mbrType,
        };

    /// <summary>
    /// 복구 파티션 판별은 두 형식을 모두 알아야 합니다. 한쪽만 보면 그 형식의 디스크에서
    /// WinRE 안내가 조용히 빠집니다 — 사용자는 복구가 필요한 순간에야 없다는 걸 알게 됩니다.
    /// </summary>
    [Fact]
    public void GPT_복구_타입을_알아본다()
    {
        var p = Make(gptType: new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac"));

        Assert.True(p.IsWindowsRecovery);
    }

    [Fact]
    public void MBR_복구_타입_0x27을_알아본다()
    {
        // 실기 N: 디스크의 두 번째 파티션이 이 타입입니다.
        var p = Make(mbrType: 0x27);

        Assert.True(p.IsWindowsRecovery);
    }

    [Theory]
    [InlineData((byte)0x07)]   // 보통 NTFS
    [InlineData((byte)0x0C)]   // FAT32 LBA
    public void 보통_파티션은_복구가_아니다(byte mbrType)
    {
        Assert.False(Make(mbrType: mbrType).IsWindowsRecovery);
    }

    [Fact]
    public void 타입_정보가_없으면_복구가_아니다()
    {
        Assert.False(Make().IsWindowsRecovery);
    }
}
