using DiskMigrator.App.ViewModels;
using DiskMigrator.Core.Models;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 파티션을 <b>사람이 부르는 이름</b>으로 짓는 규칙.
/// </summary>
/// <remarks>
/// 이 화면에서 2026-08-13에 <b>만든 사람이 잘못된 파티션을 골랐습니다.</b> 칩에는
/// <c>파티션 1 NTFS "시스템 예약" — 479 MB</c>라고만 적혀 있었고, 그것이 넓혀도 소용없는
/// 칸이라는 것은 어디에도 없었습니다.
///
/// <para>이름이 틀리면 사용자는 <b>엉뚱한 칸이 커진 디스크</b>를 갖게 됩니다 — 그것도 조용히.
/// 복제는 성공으로 끝나고, C:는 그대로입니다.</para>
/// </remarks>
public class PartitionNamingTests
{
    private static readonly Guid Msr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    private static PartitionInfo P(
        int number, double gb, string? letter = null, string? label = null,
        bool efi = false, Guid? gptType = null, byte? mbrType = null) =>
        new()
        {
            Number = number,
            StartingOffset = number * 1024L * 1024,
            LengthBytes = (long)(gb * 1024 * 1024 * 1024),
            DriveLetter = letter,
            VolumeLabel = label,
            FileSystem = "NTFS",
            IsEfiSystemPartition = efi,
            GptPartitionType = gptType,
            MbrPartitionType = mbrType,
        };

    [Fact]
    public void C드라이브는_Windows라고_부른다()
    {
        // 사용자가 넓히려는 것은 "C 드라이브"가 아니라 "윈도우가 들어 있는 칸"입니다.
        Assert.Equal("Windows (C:)", PartitionNaming.ChipName(P(3, 231.32, letter: "C")));
    }

    [Fact]
    public void 시스템_예약은_그_이름으로_부른다()
    {
        // 예전 이름: 파티션 1 NTFS "시스템 예약" — 479 MB
        Assert.Equal("시스템 예약", PartitionNaming.ChipName(P(1, 0.47, label: "시스템 예약")));
    }

    [Fact]
    public void 파티션_번호는_이름에_넣지_않는다()
    {
        // 사람이 쓰지 않는 정보입니다. 막대 범례도 번호를 쓰지 않습니다.
        foreach (var p in new[] { P(3, 231, letter: "C"), P(1, 0.5, label: "시스템 예약"), P(9, 40, letter: "D") })
            Assert.DoesNotContain("파티션", PartitionNaming.ChipName(p));
    }

    [Fact]
    public void 자료_디스크는_문자와_이름으로_부른다()
    {
        Assert.Equal("D: 자료", PartitionNaming.ChipName(P(2, 800, letter: "D", label: "자료")));
    }

    // --- 넓혀도 Windows 공간이 안 늘어나는 칸 ------------------------------

    [Fact]
    public void EFI와_예약과_복구는_곁다리로_본다()
    {
        Assert.True(PartitionNaming.IsSideRole(P(1, 0.1, efi: true)));
        Assert.True(PartitionNaming.IsSideRole(P(2, 0.02, gptType: Msr)));

        // 복구 파티션 — MBR(0x27)도 같은 것으로 봐야 합니다.
        Assert.True(PartitionNaming.IsSideRole(P(4, 1.0, mbrType: 0x27)));
    }

    [Fact]
    public void 시스템_예약도_곁다리다()
    {
        // 문자가 없고 이름만 있는 칸 — 사용자가 여기에 자료를 넣지 않습니다.
        Assert.True(PartitionNaming.IsSideRole(P(1, 0.47, label: "시스템 예약")));
    }

    [Fact]
    public void Windows는_곁다리가_아니다()
    {
        // 여기에 경고가 뜨면, 정작 올바로 고른 사용자가 자기를 의심하게 됩니다.
        Assert.False(PartitionNaming.IsSideRole(P(3, 231.32, letter: "C")));
    }

    [Fact]
    public void 문자가_붙은_자료_디스크도_곁다리가_아니다()
    {
        Assert.False(PartitionNaming.IsSideRole(P(2, 800, letter: "D", label: "자료")));
    }
}
