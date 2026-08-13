using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
using Xunit;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 남는 공간이 생겼을 때 <b>어느 파티션을 넓힐지</b>.
/// </summary>
/// <remarks>
/// 이 규칙이 틀리면 사용자는 <b>엉뚱한 파티션이 커진 디스크</b>를 갖게 됩니다. 그것도 조용히 —
/// 클론은 성공으로 끝나고, C:는 그대로이며, 복구 파티션이 460 GB가 되어 있습니다.
///
/// <para>실제로 있었던 일이라 시험으로 고정합니다. 2026-08-13, 만든 사람이 이 화면에서
/// <c>시스템 예약 479 MB</c>가 선택된 채로 시작할 뻔했습니다 — 목록의 첫 항목을 고르고
/// 있었기 때문입니다.</para>
/// </remarks>
public class GrowTargetPickerTests
{
    private static PartitionInfo P(
        int number, double gb, string? letter = null, string? label = null) =>
        new()
        {
            Number = number,
            StartingOffset = number * 1024L * 1024,
            LengthBytes = (long)(gb * 1024 * 1024 * 1024),
            DriveLetter = letter,
            VolumeLabel = label,
            FileSystem = "NTFS",
        };

    /// <summary>실기에서 쓴 그 디스크 — Samsung 860 EVO 250GB.</summary>
    private static PartitionInfo[] RealDisk() =>
    [
        P(1, 0.47, label: "시스템 예약"),
        P(3, 231.32, letter: "C"),
        P(4, 1.0),                          // 복구
    ];

    [Fact]
    public void 첫_항목이_아니라_Windows_파티션을_고른다()
    {
        var picked = GrowTargetPicker.Preferred(RealDisk());

        // 첫 항목(시스템 예약)을 고르면 넓히기를 켜도 아무 일이 일어나지 않습니다.
        Assert.Equal(3, picked!.Number);
        Assert.Equal("C", picked.DriveLetter);
    }

    [Fact]
    public void 목록_순서가_뒤바뀌어도_같은_것을_고른다()
    {
        // 후보를 담는 순서에 기대지 않습니다 — 그것이 원래 결함이었습니다.
        var reversed = RealDisk().Reverse().ToList();
        Assert.Equal(3, GrowTargetPicker.Preferred(reversed)!.Number);
    }

    [Fact]
    public void 드라이브_문자가_없으면_가장_큰_것을_고른다()
    {
        // 실행 중이 아닌 디스크를 복제할 때는 Windows 파티션에 문자가 안 붙습니다.
        PartitionInfo[] offline = [P(1, 0.47), P(2, 0.1), P(3, 231.32), P(4, 1.0)];

        Assert.Equal(3, GrowTargetPicker.Preferred(offline)!.Number);
    }

    [Fact]
    public void C가_가장_크지_않아도_C를_고른다()
    {
        // 자료용 파티션이 Windows보다 큰 경우. 사람이 넓히려는 것은 여전히 C:입니다.
        PartitionInfo[] disk = [P(1, 120, letter: "C"), P(2, 800, letter: "D", label: "자료")];

        Assert.Equal(1, GrowTargetPicker.Preferred(disk)!.Number);
    }

    [Fact]
    public void 소문자_c도_같은_것으로_본다()
    {
        PartitionInfo[] disk = [P(1, 0.5), P(2, 100, letter: "c")];
        Assert.Equal(2, GrowTargetPicker.Preferred(disk)!.Number);
    }

    [Fact]
    public void 후보가_없으면_아무것도_고르지_않는다()
    {
        // 넓힐 것이 없는데 하나를 골라 주면, 화면은 "넓힙니다"라고 말하면서 아무 일도 안 합니다.
        Assert.Null(GrowTargetPicker.Preferred([]));
    }

    [Fact]
    public void 후보가_하나면_그것을_고른다()
    {
        Assert.Equal(7, GrowTargetPicker.Preferred([P(7, 40)])!.Number);
    }
}
