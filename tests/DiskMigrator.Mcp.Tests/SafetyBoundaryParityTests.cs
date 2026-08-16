using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Safety;
using DiskMigrator.Windows.Jobs;
using DiskMigrator.Mcp;
using DiskMigrator.Mcp.Tools;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 경계에 놓인 조합들에서 <c>evaluate_safety</c>가 <b>화면과 같은 답</b>을 내는지 — 계획서 2단계 완료 조건.
/// </summary>
/// <remarks>
/// <b>왜 경계인가.</b> 대상이 원본보다 작을 때 이 앱은 세 갈래로 갈립니다 — 파티션이 제자리로
/// 들어가거나(맞춤 클론), 가장 큰 NTFS를 줄이면 들어가거나(축소 클론), 아예 안 들어가거나
/// (차단). 갈림길이 있는 곳이 답이 어긋나기 쉬운 곳이고, 여기가 정확히 사용자가
/// "이 디스크로 옮겨도 되나요"라고 묻는 자리입니다.
///
/// <para><b>맞대는 방식.</b> 화면이 부르는 그 줄(<c>MainViewModel</c>의 <see cref="SafetyGuard.Evaluate"/>
/// 호출)을 여기서 그대로 부르고, 같은 디스크로 도구를 통과시킨 결과와 <b>항목 전체</b>를 비교합니다.
/// 진행 가능 여부만 같은 것으로는 부족합니다 — 화면은 "남는 공간 500GB"라고 알려 주는데 Claude는
/// 그것을 모른다면, 사용자는 물어본 것에 대해 두 개의 다른 답을 듣게 됩니다.</para>
/// </remarks>
public class SafetyBoundaryParityTests
{
    private const long Gb = 1_000_000_000L;
    private const long Tb = 1_000_000_000_000L;

    // --- 경계 조합 ------------------------------------------------------------
    //
    // 이름은 사용자가 실제로 묻는 말에 가깝게 둡니다 — 실패했을 때 무엇이 깨졌는지
    // 코드가 아니라 상황으로 읽히도록.

    public static TheoryData<string, DiskInfo, DiskInfo> Combinations() => new()
    {
        {
            "같은 크기, 대상은 빈 디스크",
            Gpt(1, "SOURCE 500G", 500 * Gb, partitions: Ntfs(1, 1L << 20, 400 * Gb)),
            Blank(2, "TARGET 500G", 500 * Gb)
        },
        {
            "대상이 더 크다 — 남는 공간이 생긴다",
            Gpt(1, "SOURCE 500G", 500 * Gb, partitions: Ntfs(1, 1L << 20, 400 * Gb)),
            Blank(2, "TARGET 1T", Tb)
        },
        {
            "대상이 더 작지만 파티션이 그대로 들어간다",
            Gpt(1, "SOURCE 1T", Tb, partitions: Ntfs(1, 1L << 20, 200 * Gb)),
            Blank(2, "TARGET 500G", 500 * Gb)
        },
        {
            "대상이 더 작고 가장 큰 NTFS를 줄여야 들어간다",
            Gpt(1, "SOURCE 1T", Tb, partitions: Ntfs(1, 1L << 20, 990 * Gb, freeSpace: 800 * Gb)),
            Blank(2, "TARGET 500G", 500 * Gb)
        },
        {
            "대상이 더 작고 MBR이라 줄일 길이 없다",
            Mbr(1, "SOURCE 1T", Tb, Ntfs(1, 1L << 20, 990 * Gb, active: true)),
            Blank(2, "TARGET 500G", 500 * Gb)
        },
        {
            "섹터 크기가 다르다",
            Gpt(1, "SOURCE 4K", 500 * Gb, sectorSize: 4096, partitions: Ntfs(1, 1L << 20, 400 * Gb)),
            Blank(2, "TARGET 512", 500 * Gb)
        },
        {
            "대상에 이미 데이터가 있다",
            Gpt(1, "SOURCE 500G", 500 * Gb, partitions: Ntfs(1, 1L << 20, 400 * Gb)),
            Gpt(2, "TARGET 500G", 500 * Gb, partitions: Ntfs(1, 1L << 20, 300 * Gb))
        },
        {
            "MBR 원본을 GPT 대상으로 — 복제는 되지만 부팅이 걸린다",
            Mbr(1, "SOURCE MBR", 500 * Gb, Ntfs(1, 1L << 20, 400 * Gb, active: true)),
            Blank(2, "TARGET 1T", Tb)
        },
        {
            "양쪽 모두 USB로 물려 있다",
            Gpt(1, "SOURCE USB", 500 * Gb, bus: DiskBusType.Usb, partitions: Ntfs(1, 1L << 20, 400 * Gb)),
            Blank(2, "TARGET USB", 500 * Gb, bus: DiskBusType.Usb)
        },
    };

    /// <summary>
    /// 도구가 돌려준 항목이 화면이 보여 줄 항목과 <b>하나도 빠짐없이</b> 같은지.
    /// </summary>
    /// <remarks>
    /// 심각도까지 함께 봅니다. 같은 사유를 화면은 "확인이 필요합니다"로, 도구는 "참고하세요"로
    /// 말하면 사용자가 받는 무게가 달라집니다.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task 도구와_화면이_같은_항목을_내놓는다(string _, DiskInfo source, DiskInfo target)
    {
        var (tools, appState) = Setup(source, target);

        var result = await tools.EvaluateSafetyAsync(source.DeviceNumber, target.DeviceNumber);
        Assert.True(result.Ok, result.Error?.Message);

        // 화면이 부르는 그 줄 그대로 (MainViewModel.RefreshSafety).
        var onScreen = SafetyGuard.Evaluate(
            source, target, isElevated: true, appState.UseSnapshot,
            sourceHibernated: Core.Registry.HibernationImage.IsPresent(source));

        var dto = result.Data!;

        Assert.Equal(onScreen.CanProceed, dto.CanProceed);
        Assert.Equal(onScreen.NeedsTypedConfirmation, dto.NeedsTypedConfirmation);

        var expected = onScreen.Issues
            .Select(i => $"{i.Severity}:{i.Code}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var actual = dto.Blockers.Concat(dto.Confirmations).Concat(dto.Warnings).Concat(dto.Notes)
            .Select(i => $"{i.Severity}:{i.Code}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 매트릭스가 실제로 <b>갈림길을 다 밟았는지</b> — 시험이 조용히 쉬워지는 것을 막습니다.
    /// </summary>
    /// <remarks>
    /// 위 시험은 양쪽이 <b>똑같이 비어 있어도</b> 통과합니다. 언젠가 규칙이 바뀌어 경계 조합이
    /// 아무 항목도 만들지 않게 되면, 시험은 초록인데 아무것도 확인하지 않는 상태가 됩니다.
    /// 그래서 밟아야 할 코드를 이름으로 못 박습니다.
    /// </remarks>
    [Fact]
    public async Task 매트릭스가_세_갈래와_경계_사유를_모두_밟는다()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Combinations())
        {
            var source = (DiskInfo)row[1];
            var target = (DiskInfo)row[2];

            var (tools, _) = Setup(source, target);
            var result = await tools.EvaluateSafetyAsync(source.DeviceNumber, target.DeviceNumber);

            foreach (var issue in result.Data!.Blockers
                .Concat(result.Data.Confirmations)
                .Concat(result.Data.Warnings)
                .Concat(result.Data.Notes))
            {
                seen.Add(issue.Code);
            }
        }

        Assert.Contains(SafetyGuard.CodeTargetSmallerLayoutFits, seen);  // 맞춤 클론
        Assert.Contains(SafetyGuard.CodeTargetSmallerShrink, seen);      // 축소 클론
        Assert.Contains(SafetyGuard.CodeTargetTooSmall, seen);           // 차단
        Assert.Contains(SafetyGuard.CodeTargetLarger, seen);
        Assert.Contains(SafetyGuard.CodeSectorSizeMismatch, seen);
        Assert.Contains(SafetyGuard.CodeTargetHasData, seen);
        Assert.Contains(SafetyGuard.CodeSourceBiosOnly, seen);
        Assert.Contains(SafetyGuard.CodeSourceOverUsb, seen);
        Assert.Contains(SafetyGuard.CodeTargetOverUsb, seen);
    }

    // --- 만들기 도구 ----------------------------------------------------------

    private static PartitionInfo Ntfs(
        int number, long start, long length, long? freeSpace = null, bool active = false) => new()
    {
        Number = number,
        StartingOffset = start,
        LengthBytes = length,
        FileSystem = "NTFS",
        FreeSpaceBytes = freeSpace,
        IsActive = active,
    };

    private static DiskInfo Disk(
        int n, string model, long size, PartitionStyle style,
        int sectorSize, DiskBusType bus, PartitionInfo[] partitions) => new()
    {
        DeviceNumber = n,
        Model = model,
        SerialNumber = $"SN-{n}-{model}",
        SizeBytes = size,
        LogicalSectorSize = sectorSize,
        PartitionStyle = style,
        BusType = bus,
        Partitions = partitions,
    };

    private static DiskInfo Gpt(
        int n, string model, long size,
        int sectorSize = 512, DiskBusType bus = DiskBusType.Nvme,
        params PartitionInfo[] partitions) =>
        Disk(n, model, size, PartitionStyle.Gpt, sectorSize, bus, partitions);

    private static DiskInfo Mbr(int n, string model, long size, params PartitionInfo[] partitions) =>
        Disk(n, model, size, PartitionStyle.Mbr, 512, DiskBusType.Nvme, partitions);

    /// <summary>파티션 테이블조차 없는 새 디스크 — 대상 쪽 잡음을 없애기 위한 것.</summary>
    private static DiskInfo Blank(int n, string model, long size, DiskBusType bus = DiskBusType.Nvme) =>
        Disk(n, model, size, PartitionStyle.Raw, 512, bus, []);

    private static (PlanningTools Tools, IAppState AppState) Setup(DiskInfo source, DiskInfo target)
    {
        var appState = new ScreenState();
        var reader = new FakeReader(source, target);

        var tools = new PlanningTools(
            reader,
            new UnusedPlanner(),
            new Mapping(),
            new Windows.Jobs.ImageInspector(new UnusedDiskService()),
            appState);

        return (tools, appState);
    }

    private sealed class FakeReader(params DiskInfo[] disks) : IDiskReader
    {
        public bool IsElevated => true;
        public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>(disks);
    }

    /// <summary>화면이 서 있는 상태. 스냅샷을 <b>꺼 둔</b> 채로 둡니다.</summary>
    /// <remarks>
    /// 기본값(켜짐)으로 두면 도구가 예전처럼 true를 가정하고 있어도 시험이 통과합니다.
    /// 꺼 둬야 "화면에서 읽는다"가 실제로 확인됩니다.
    /// </remarks>
    private sealed class ScreenState : IAppState
    {
        public bool IsBusy => false;
        public bool UseSnapshot => false;
        public OperationProgress GetProgress() => new(false, null, 0, null, null, null, null);
        public void RequestCancel() { }
    }

    private sealed class UnusedPlanner : IClonePlanner
    {
        public Task<ClonePreview> PreviewAsync(DiskInfo source, bool useSnapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException("안전 판정은 계획기를 부르지 않습니다.");
    }

    private sealed class UnusedDiskService : Core.Abstractions.IDiskService
    {
        public bool IsElevated => true;
        public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>([]);
        public Core.Abstractions.IBlockDevice OpenRead(DiskInfo disk) => throw new InvalidOperationException();
        public Core.Abstractions.IBlockDevice OpenWriteExclusive(DiskInfo disk) => throw new InvalidOperationException();
        public void RefreshDiskProperties(DiskInfo disk) => throw new InvalidOperationException();
        public Task<SafeRemoveResult> SafeRemoveAsync(DiskInfo disk, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }
}
