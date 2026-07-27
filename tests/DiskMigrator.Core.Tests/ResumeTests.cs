using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 중단 후 이어하기의 두 토대를 검증합니다: 엔진의 앞부분 쓰기 생략과 진행 저널.
/// </summary>
public class ResumeTests
{
    private const int Sector = 512;
    private const int DeviceSize = 1024 * Sector; // 512KB
    private const int Buffer = 64 * Sector;       // 32KB → 총 16청크

    private static CloneOptions Options(long resumeFrom = 0, bool verify = true) => new()
    {
        BufferSize = Buffer,
        VerifyAfterClone = verify,
        ResumeFromBytes = resumeFrom,
        ReadRetryCount = 0,
        RetryDelay = TimeSpan.Zero,
        ProgressInterval = TimeSpan.Zero,
        FlushInterval = Buffer * 4, // 4청크마다 플러시 → 체크포인트
    };

    private static ClonePlan Plan(FaultyBlockDevice source, FaultyBlockDevice target,
        Action<long>? checkpoint = null) => new()
    {
        Target = target,
        Name = "이어하기 테스트",
        FlushCheckpoint = checkpoint,
        Regions =
        [
            new CopyRegion
            {
                Source = source, SourceOffset = 0, TargetOffset = 0,
                Length = source.Length, Description = "전체",
            },
        ],
    };

    [Fact]
    public async Task 이어하기_앞부분은_쓰지_않고_뒷부분만_쓴다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        // 이전 실행이 절반까지 써 둔 상황을 재현.
        source.Data.AsSpan(0, DeviceSize / 2).CopyTo(target.Data);

        var result = await new CloneEngine().RunAsync(
            Plan(source, target), Options(resumeFrom: DeviceSize / 2));

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.True(result.VerificationPassed);            // 건너뛴 앞부분까지 검증 통과
        Assert.Equal(DeviceSize / 2 / Buffer, target.WriteCount); // 뒷 8청크만 씀
        Assert.Equal(source.Data, target.Data);
    }

    [Fact]
    public async Task 이어하기_앞부분이_오염됐으면_검증이_잡아낸다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        source.Data.AsSpan(0, DeviceSize / 2).CopyTo(target.Data);
        target.Data[100] ^= 0xFF; // "이미 기록됨"이라던 앞부분이 사실은 오염

        var result = await new CloneEngine().RunAsync(
            Plan(source, target), Options(resumeFrom: DeviceSize / 2));

        // 이어하기는 앞부분을 다시 쓰진 않지만, 해시는 원본 전체에서 채우므로 검증이 잡아냅니다.
        Assert.False(result.VerificationPassed);
    }

    [Fact]
    public async Task 체크포인트는_플러시_후에만_단조증가로_불린다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        var checkpoints = new List<long>();

        await new CloneEngine().RunAsync(
            Plan(source, target, checkpoints.Add), Options(verify: false));

        Assert.NotEmpty(checkpoints);
        Assert.Equal(checkpoints.OrderBy(x => x), checkpoints); // 단조 증가
        Assert.All(checkpoints, c => Assert.True(c > 0 && c <= DeviceSize));
    }

    [Fact]
    public async Task 취소돼도_마지막_체크포인트가_남는다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        var checkpoints = new List<long>();

        using var cts = new CancellationTokenSource();
        var progress = new Progress<CloneProgress>(_ => cts.Cancel()); // 첫 보고에서 취소

        var result = await new CloneEngine().RunAsync(
            Plan(source, target, checkpoints.Add),
            Options(verify: false), progress, pause: null, cts.Token);

        Assert.Equal(CloneOutcome.Cancelled, result.Outcome);
        Assert.NotEmpty(checkpoints); // 취소 경로에서도 플러시+체크포인트가 남음
    }

    // --- ResumeJournal -----------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("dm-resume-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void 저널_저장하고_같은_지문으로_읽으면_지점이_나온다()
    {
        using var dir = new TempDir();
        string fp = ResumeJournal.MakeFingerprint("restore", "img.vhdx", 1000L, "disk", 2000L);

        ResumeJournal.Save(dir.Path, fp, completedBytes: 700, totalBytes: 2000);

        Assert.Equal(700, ResumeJournal.TryLoad(dir.Path, fp, totalBytes: 2000));
    }

    [Fact]
    public void 저널_지문이나_총량이_다르면_무시된다()
    {
        using var dir = new TempDir();
        string fp = ResumeJournal.MakeFingerprint("restore", "img.vhdx", 1000L);
        ResumeJournal.Save(dir.Path, fp, 700, 2000);

        string otherFp = ResumeJournal.MakeFingerprint("restore", "other.vhdx", 1000L);
        Assert.Equal(0, ResumeJournal.TryLoad(dir.Path, otherFp, 2000)); // 다른 지문
        Assert.Equal(0, ResumeJournal.TryLoad(dir.Path, fp, 3000));      // 총량 불일치
    }

    [Fact]
    public void 저널_삭제하면_처음부터가_된다()
    {
        using var dir = new TempDir();
        string fp = ResumeJournal.MakeFingerprint("x");
        ResumeJournal.Save(dir.Path, fp, 700, 2000);

        ResumeJournal.Delete(dir.Path, fp);

        Assert.Equal(0, ResumeJournal.TryLoad(dir.Path, fp, 2000));
    }

    [Fact]
    public void 저널_없는_폴더는_그냥_0이다()
    {
        Assert.Equal(0, ResumeJournal.TryLoad(
            Path.Combine(Path.GetTempPath(), "dm-none-" + Guid.NewGuid()), "ABCD1234", 100));
    }
}
