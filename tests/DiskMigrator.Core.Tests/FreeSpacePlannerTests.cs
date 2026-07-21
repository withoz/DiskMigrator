using DiskMigrator.Core.Models;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 남는 공간 선택이 클론 옵션으로 옮겨지는 배선.
/// </summary>
/// <remarks>
/// 미리보기 막대·시작 버튼·실제 실행이 모두 <see cref="FreeSpacePlanner.Resolve"/> 하나를
/// 부르므로, 여기서 고정하면 셋이 갈라질 수 없습니다. 예전에는 화면 코드 두 곳에서 따로
/// 계산해 크기 입력이 잘못됐을 때 미리보기와 실행이 서로 다른 결론을 냈습니다.
/// </remarks>
public class FreeSpacePlannerTests
{
    private static FreeSpacePlan Resolve(
        bool hasFreeSpace = true,
        bool canResize = true,
        bool expandLast = false,
        bool growPartition = false,
        int? number = 2,
        bool fillRemaining = true,
        string? sizeText = null)
        => FreeSpacePlanner.Resolve(
            hasFreeSpace, canResize, expandLast, growPartition, number, fillRemaining, sizeText);

    [Fact]
    public void 아무것도_안_고르면_그대로_둔다()
    {
        var plan = Resolve();

        Assert.Equal(FreeSpaceMode.Leave, plan.Mode);
        Assert.Null(plan.Grow);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void 남는_공간이_없으면_어떤_선택도_무시된다()
    {
        // 남는 공간이 없는데 "마지막 파티션에 합치기"가 켜져 있으면 합칠 것이 없습니다.
        var plan = Resolve(hasFreeSpace: false, expandLast: true, growPartition: true);

        Assert.Equal(FreeSpaceMode.Leave, plan.Mode);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void 확대할_수_없는_디스크에서는_넓히기가_무시된다()
    {
        // MBR 원본 등 리사이즈가 불가능한 경우. 조용히 아무 일도 안 하는 대신 Leave로 떨어집니다.
        var plan = Resolve(canResize: false, growPartition: true);

        Assert.Equal(FreeSpaceMode.Leave, plan.Mode);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void 넓히기가_합치기보다_우선한다()
    {
        var plan = Resolve(expandLast: true, growPartition: true);

        Assert.Equal(FreeSpaceMode.GrowPartition, plan.Mode);
    }

    [Fact]
    public void 마지막_파티션에_합치기는_넓힐_파티션을_요구하지_않는다()
    {
        var plan = Resolve(expandLast: true, number: null);

        Assert.Equal(FreeSpaceMode.ExpandLast, plan.Mode);
        Assert.Null(plan.Grow);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void 넓히기인데_파티션을_안_고르면_막는다()
    {
        // 그냥 두면 아무 일도 안 하면서 사용자는 넓혀졌다고 믿게 됩니다.
        var plan = Resolve(growPartition: true, number: null);

        Assert.NotNull(plan.Error);
        Assert.Null(plan.Grow);
    }

    [Fact]
    public void 남는_공간_전부는_크기를_비워_보낸다()
    {
        var plan = Resolve(growPartition: true, number: 3, fillRemaining: true);

        Assert.Equal(FreeSpaceMode.GrowPartition, plan.Mode);
        Assert.Equal(3, plan.Grow!.PartitionNumber);
        Assert.Null(plan.Grow.NewLengthBytes);
        Assert.Null(plan.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void 새_총_크기가_올바르지_않으면_막는다(string input)
    {
        // 여기서 막지 않으면 크기가 null이 되어 "남는 공간 전부"로 조용히 바뀝니다.
        // 사용자가 지정한 것과 전혀 다른 크기로 파티션이 커집니다.
        var plan = Resolve(growPartition: true, fillRemaining: false, sizeText: input);

        Assert.NotNull(plan.Error);
        Assert.Null(plan.Grow);
    }

    [Fact]
    public void 새_총_크기는_화면과_같은_1024_기준으로_읽는다()
    {
        // 화면의 모든 크기가 SizeFormatter(1024 기준)입니다. 1000 기준으로 읽으면 범례의
        // "930.49 GB"를 그대로 입력한 사용자가 7% 작은 파티션을 얻습니다.
        var plan = Resolve(growPartition: true, number: 2, fillRemaining: false, sizeText: "100");

        Assert.Null(plan.Error);
        Assert.Equal(100L * 1024 * 1024 * 1024, plan.Grow!.NewLengthBytes);
    }

    [Fact]
    public void 소수점_크기도_읽는다()
    {
        var plan = Resolve(growPartition: true, fillRemaining: false, sizeText: "1.5");

        Assert.Null(plan.Error);
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), plan.Grow!.NewLengthBytes);
    }

    [Fact]
    public void 남는_공간_전부를_고르면_크기_입력이_잘못돼도_막지_않는다()
    {
        // 쓰지 않는 칸의 내용 때문에 시작이 막히면 사용자는 이유를 알 수 없습니다.
        var plan = Resolve(growPartition: true, fillRemaining: true, sizeText: "쓰레기");

        Assert.Null(plan.Error);
        Assert.Null(plan.Grow!.NewLengthBytes);
    }

    // --- 실행 가능성까지 같은 자리에서 --------------------------------------
    //
    // 숫자로 읽히기만 하면 통과시키면, 930 GB 파티션에 "600"을 넣은 요청이 그대로 흘러가
    // 미리보기만 소리 없이 사라지고 이유는 '클론 시작'을 눌러야 나옵니다.

    /// <summary>전형적인 Windows 배치 — [ESP][MSR][C: 930GB][복구]. C:는 마지막이 아닙니다.</summary>
    private static List<PartitionInfo> Windows930()
    {
        const long mb = 1L << 20;
        long esp = mb, espLen = 200 * mb;
        long msr = esp + espLen, msrLen = 16 * mb;
        long c = msr + msrLen, cLen = 930L << 30;
        long rec = c + cLen, recLen = 826 * mb;

        return
        [
            new() { Number = 1, StartingOffset = esp, LengthBytes = espLen, FileSystem = "FAT32" },
            new() { Number = 2, StartingOffset = msr, LengthBytes = msrLen },
            new() { Number = 3, StartingOffset = c, LengthBytes = cLen, FileSystem = "NTFS" },
            new() { Number = 4, StartingOffset = rec, LengthBytes = recLen, FileSystem = "NTFS" },
        ];
    }

    private static long TargetWithSlack(List<PartitionInfo> source, long slackBytes) =>
        source[^1].StartingOffset + source[^1].LengthBytes + slackBytes;

    [Fact]
    public void 현재보다_작은_크기를_넣으면_현재_크기로_올려_맞춘다()
    {
        var source = Windows930();
        long target = TargetWithSlack(source, 10L << 30);

        var plan = FreeSpacePlanner.Resolve(
            hasFreeSpace: true, canResize: true, expandLast: false, growPartition: true,
            growPartitionNumber: 3, fillRemaining: false, sizeText: "600",
            source: source, targetSizeBytes: target);

        // 축소는 지원하지 않습니다. 현재 크기(930 GiB)보다 작은 입력은 오류 대신 현재 크기로
        // 올려 맞춰, 데이터(현재 파티션) 용량 아래로는 내려가지 않게 합니다.
        Assert.Null(plan.Error);
        Assert.NotNull(plan.Grow);
        Assert.Equal(930L << 30, plan.Grow!.NewLengthBytes);
    }

    [Fact]
    public void 늘릴_수_있는_크기는_그대로_통과한다()
    {
        var source = Windows930();
        long target = TargetWithSlack(source, 100L << 30);

        var plan = FreeSpacePlanner.Resolve(
            hasFreeSpace: true, canResize: true, expandLast: false, growPartition: true,
            growPartitionNumber: 3, fillRemaining: false, sizeText: "1000",
            source: source, targetSizeBytes: target);

        Assert.Null(plan.Error);
        Assert.Equal(1000L << 30, plan.Grow!.NewLengthBytes);
    }

    [Fact]
    public void 원본_배치를_주지_않으면_실행_가능성은_검사하지_않는다()
    {
        // 디스크를 아직 고르지 않은 화면에서도 파싱 검사는 돌아야 합니다.
        var plan = Resolve(growPartition: true, fillRemaining: false, sizeText: "600");

        Assert.Null(plan.Error);
    }
}
