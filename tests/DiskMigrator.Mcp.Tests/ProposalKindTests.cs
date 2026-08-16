using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using DiskMigrator.Mcp.Dto;
using DiskMigrator.Mcp.Proposals;
using DiskMigrator.Mcp.Tools;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 복제 말고도 백업·복원·부팅 복구를 제안할 수 있게 넓힌 부분 — 계획서 §6.3.
/// </summary>
/// <remarks>
/// 넓힌 만큼 위험도 넓어졌습니다. 특히 <b>복원은 대상을 통째로 지우면서 원본이 파일</b>이라,
/// 복제에 걸어 둔 <c>SafetyGuard</c> 규칙이 그대로 적용되지 않습니다. 그 자리에 세운 금지선이
/// 실제로 서 있는지를 여기서 확인합니다.
/// </remarks>
public class ProposalKindTests
{
    private static DiskInfo Disk(
        int n, string model, bool system = false, bool boot = false,
        bool pageFile = false, bool readOnly = false, bool hasData = true) => new()
    {
        DeviceNumber = n,
        Model = model,
        SerialNumber = $"S{n}",
        SizeBytes = 500_000_000_000,
        LogicalSectorSize = 512,
        PartitionStyle = PartitionStyle.Gpt,
        IsSystemDisk = system,
        IsBootDisk = boot,
        HasPageFile = pageFile,
        IsReadOnly = readOnly,

        // HasExistingData는 파티션에서 계산됩니다 — 값을 직접 넣을 수 없으므로 파티션으로 만듭니다.
        Partitions = hasData
            ? [new PartitionInfo { Number = 1, StartingOffset = 1 << 20, LengthBytes = 100L << 30 }]
            : [],
    };

    private sealed class FakeReader(params DiskInfo[] disks) : IDiskReader
    {
        public bool IsElevated { get; init; } = true;

        public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>(disks);
    }

    private sealed class IdleAppState : IAppState
    {
        public bool IsBusy { get; init; }
        public bool UseSnapshot => true;
        public OperationProgress GetProgress() => new(IsBusy, null, 0, null, null, null, null);
        public void RequestCancel() { }
    }

    private static (ProposalTools Tools, ProposalStore Store) Setup(
        IDiskReader reader, bool busy = false)
    {
        var store = new ProposalStore();
        return (new ProposalTools(reader, store, new IdleAppState { IsBusy = busy }), store);
    }

    private static string Vhdx()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dmx-test-{Guid.NewGuid():N}.vhdx");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    // --- 복원: 가장 위험한 제안 -------------------------------------------------

    /// <summary>
    /// 실행 중인 Windows 디스크에는 <b>카드조차 뜨지 않아야</b> 합니다.
    /// </summary>
    /// <remarks>
    /// "적용해도 어차피 막힌다"로는 부족합니다. 카드가 뜬 순간 사용자는 그것이 할 수 있는
    /// 일이라고 읽으며, 지금 쓰고 있는 시스템을 지우자는 제안이 화면에 오르는 것 자체가 잘못입니다.
    /// </remarks>
    [Fact]
    public async Task 복원은_시스템_디스크에_카드조차_띄우지_않는다()
    {
        string image = Vhdx();
        try
        {
            var (tools, store) = Setup(new FakeReader(Disk(0, "System", system: true, boot: true)));

            var r = await tools.ProposeRestoreAsync(image, 0, "이유");

            Assert.False(r.Ok);
            Assert.Equal(ToolErrorCodes.Blocked, r.Error!.Code);
            Assert.Null(store.Current);   // 카드는 없습니다
        }
        finally { File.Delete(image); }
    }

    [Theory]
    [InlineData(true, false, false)]   // 페이지파일
    [InlineData(false, true, false)]   // 읽기 전용
    [InlineData(false, false, true)]   // 부팅 디스크
    public async Task 복원은_금지된_대상을_모두_거절한다(bool pageFile, bool readOnly, bool boot)
    {
        string image = Vhdx();
        try
        {
            var (tools, store) = Setup(new FakeReader(
                Disk(1, "T", pageFile: pageFile, readOnly: readOnly, boot: boot)));

            var r = await tools.ProposeRestoreAsync(image, 1, "이유");

            Assert.False(r.Ok);
            Assert.Equal(ToolErrorCodes.Blocked, r.Error!.Code);
            Assert.Null(store.Current);
        }
        finally { File.Delete(image); }
    }

    /// <summary>없는 이미지로는 제안할 수 없습니다 — 복원하다 중간에 실패하면 대상은 이미 지워진 뒤입니다.</summary>
    [Fact]
    public async Task 복원은_없는_이미지를_거절한다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(1, "T")));

        var r = await tools.ProposeRestoreAsync(@"X:\없는파일.vhdx", 1, "이유");

        Assert.False(r.Ok);
        Assert.Equal(ToolErrorCodes.FileNotFound, r.Error!.Code);
        Assert.Null(store.Current);
    }

    /// <summary>정상 대상이면 카드가 뜨고, 데이터가 있으므로 모델명 입력이 남습니다.</summary>
    [Fact]
    public async Task 복원은_데이터가_있는_대상에_모델명_입력을_남긴다()
    {
        string image = Vhdx();
        try
        {
            var (tools, store) = Setup(new FakeReader(Disk(2, "빈 디스크", hasData: true)));

            var r = await tools.ProposeRestoreAsync(image, 2, "이유");

            Assert.True(r.Ok);
            Assert.True(r.Data!.NeedsTypedConfirmation);
            Assert.True(r.Data.IsDestructive);
            Assert.Equal(ProposalKind.Restore, store.Current!.Kind);
            Assert.Null(store.Current.Source);           // 원본은 파일입니다
            Assert.Equal(image, store.Current.ImagePath);
        }
        finally { File.Delete(image); }
    }

    // --- 백업: 읽기만 하는 제안 -------------------------------------------------

    [Fact]
    public async Task 백업은_대상_디스크가_없고_지우지_않는다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(0, "원본")));

        var r = await tools.ProposeBackupAsync(0, @"E:\backup.vhdx", "이유");

        Assert.True(r.Ok);
        Assert.False(r.Data!.IsDestructive);
        Assert.False(r.Data.NeedsTypedConfirmation);
        Assert.Equal(ProposalKind.Backup, store.Current!.Kind);
        Assert.Null(store.Current.Target);
    }

    /// <summary>
    /// 이미 있는 파일을 가리키면 증분 사슬로 이어진다는 사실을 안내에 담아야 합니다.
    /// </summary>
    /// <remarks>
    /// 사용자가 "새로 백업한다"고 생각한 자리에서 기존 사슬에 붙으면, 원본이 망가졌을 때
    /// 사슬 전체가 못 쓰게 됩니다. 이것은 Claude가 반드시 사용자에게 옮겨야 할 사실입니다.
    /// </remarks>
    [Fact]
    public async Task 백업은_기존_파일이면_증분임을_알린다()
    {
        string image = Vhdx();
        try
        {
            var (tools, _) = Setup(new FakeReader(Disk(0, "원본")));

            var r = await tools.ProposeBackupAsync(0, image, "이유");

            Assert.True(r.Ok);
            Assert.Contains("incremental", r.Data!.Note, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task 백업은_경로가_비면_거절한다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(0, "원본")));

        var r = await tools.ProposeBackupAsync(0, "   ", "이유");

        Assert.False(r.Ok);
        Assert.Equal(ToolErrorCodes.InvalidArgument, r.Error!.Code);
        Assert.Null(store.Current);
    }

    // --- 부팅 복구 --------------------------------------------------------------

    /// <summary>
    /// 지금 이 앱이 돌아가고 있는 디스크는 고칠 대상이 아닙니다 — 이미 부팅에 성공했습니다.
    /// </summary>
    [Fact]
    public async Task 부팅복구는_실행중인_시스템_디스크를_거절한다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(0, "System", system: true, boot: true)));

        var r = await tools.ProposeBootRepairAsync(0, "이유");

        Assert.False(r.Ok);
        Assert.Equal(ToolErrorCodes.LiveSystemDisk, r.Error!.Code);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task 부팅복구는_다른_디스크에_카드를_띄운다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(3, "복제본")));

        var r = await tools.ProposeBootRepairAsync(3, "부팅 흔적이 로더에서 멈춰 있습니다");

        Assert.True(r.Ok);
        Assert.Equal(ProposalKind.BootRepair, store.Current!.Kind);
        Assert.False(r.Data!.IsDestructive);          // 데이터를 지우지는 않습니다
        Assert.Equal(3, r.Data.TargetDeviceNumber);
        Assert.Null(r.Data.SourceDeviceNumber);
    }

    // --- 공통 관문 --------------------------------------------------------------

    /// <summary>
    /// 이유 없는 제안은 어느 종류든 거절합니다 — 카드에 근거가 없으면 사용자가 판단할 수 없습니다.
    /// </summary>
    [Fact]
    public async Task 이유가_없으면_어느_종류든_거절한다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(0, "A"), Disk(1, "B")));

        Assert.False((await tools.ProposeCloneAsync(0, 1, "  ")).Ok);
        Assert.False((await tools.ProposeBackupAsync(0, @"E:\b.vhdx", "")).Ok);
        Assert.False((await tools.ProposeBootRepairAsync(1, "\t")).Ok);
        Assert.Null(store.Current);
    }

    /// <summary>작업이 도는 중에는 제안을 받지 않습니다 — 적용하면 지금 하는 일이 흐트러집니다.</summary>
    [Fact]
    public async Task 작업_중에는_어느_종류든_거절한다()
    {
        var (tools, store) = Setup(new FakeReader(Disk(0, "A"), Disk(1, "B")), busy: true);

        Assert.Equal(ToolErrorCodes.Busy, (await tools.ProposeCloneAsync(0, 1, "이유")).Error!.Code);
        Assert.Equal(ToolErrorCodes.Busy, (await tools.ProposeBackupAsync(0, @"E:\b.vhdx", "이유")).Error!.Code);
        Assert.Equal(ToolErrorCodes.Busy, (await tools.ProposeBootRepairAsync(1, "이유")).Error!.Code);
        Assert.Null(store.Current);
    }

    /// <summary>관리자가 아니면 읽는 것부터 안 됩니다.</summary>
    [Fact]
    public async Task 권한이_없으면_거절한다()
    {
        var (tools, _) = Setup(new FakeReader(Disk(0, "A")) { IsElevated = false });

        var r = await tools.ProposeBackupAsync(0, @"E:\b.vhdx", "이유");

        Assert.Equal(ToolErrorCodes.NotElevated, r.Error!.Code);
    }

    // --- 카드에 실리는 문구 -----------------------------------------------------

    /// <summary>
    /// 종류마다 카드의 양 끝이 달라야 합니다. 복제 전용 바인딩을 그대로 두면 백업·복원 카드에서
    /// 한쪽이 빈칸이 되고, <b>어느 디스크가 지워지는지 안 보이는 카드</b>가 됩니다.
    /// </summary>
    [Fact]
    public void 카드_문구는_종류마다_양_끝이_다르다()
    {
        var store = new ProposalStore();
        var src = Disk(0, "원본SSD");
        var tgt = Disk(2, "대상SSD");

        var clone = store.Propose(ProposalKind.Clone, src, tgt, null, "r", true, true, false);
        Assert.Contains("#0 원본SSD", clone.Endpoints);
        Assert.Contains("#2 대상SSD", clone.Endpoints);

        var backup = store.Propose(ProposalKind.Backup, src, null, @"E:\my.vhdx", "r", true, false, false);
        Assert.Contains("#0 원본SSD", backup.Endpoints);
        Assert.Contains("my.vhdx", backup.Endpoints);

        var restore = store.Propose(ProposalKind.Restore, null, tgt, @"E:\my.vhdx", "r", false, true, true);
        Assert.Contains("my.vhdx", restore.Endpoints);
        Assert.Contains("#2 대상SSD", restore.Endpoints);
        Assert.DoesNotContain("?", restore.Endpoints);   // 없는 쪽이 물음표로 새지 않아야 합니다

        var repair = store.Propose(ProposalKind.BootRepair, null, tgt, null, "r", false, false, false);
        Assert.Equal("#2 대상SSD", repair.Endpoints);
    }

    /// <summary>지우는 제안과 그렇지 않은 제안을 카드가 구분할 수 있어야 합니다.</summary>
    [Fact]
    public void 지우는_제안만_파괴적으로_표시된다()
    {
        var d = Disk(1, "T");
        var store = new ProposalStore();

        Assert.True(store.Propose(ProposalKind.Clone, d, d, null, "r", true, true, false).IsDestructive);
        Assert.True(store.Propose(ProposalKind.Restore, null, d, "p", "r", false, true, true).IsDestructive);
        Assert.False(store.Propose(ProposalKind.Backup, d, null, "p", "r", true, false, false).IsDestructive);
        Assert.False(store.Propose(ProposalKind.BootRepair, null, d, null, "r", false, false, false).IsDestructive);
    }
}
