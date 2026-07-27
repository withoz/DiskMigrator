using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Tests.Fakes;
using DiskMigrator.Core.Util;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 증분 백업의 두 토대를 검증합니다: 엔진의 "변경 블록만 쓰기"와 파일 이름 사슬 해석.
/// </summary>
public class IncrementalBackupTests
{
    private const int Sector = 512;
    private const int DeviceSize = 1024 * Sector; // 512KB
    private const int Buffer = 64 * Sector;       // 32KB — 청크 경계를 시험하기 위해 장치보다 작게

    private static CloneOptions IncrementalOptions() => new()
    {
        BufferSize = Buffer,
        VerifyAfterClone = true,
        WriteOnlyChangedBlocks = true,
        ReadRetryCount = 0,
        RetryDelay = TimeSpan.Zero,
        ProgressInterval = TimeSpan.Zero,
    };

    private static ClonePlan Plan(FaultyBlockDevice source, FaultyBlockDevice target) => new()
    {
        Target = target,
        Name = "증분 테스트",
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
    public async Task 증분_대상이_이미_같으면_아무것도_쓰지_않는다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        source.Data.CopyTo(target.Data, 0); // 부모 백업과 동일한 상태

        var result = await new CloneEngine().RunAsync(Plan(source, target), IncrementalOptions());

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.True(result.VerificationPassed);
        Assert.Equal(0, target.WriteCount);          // 변경이 없으니 쓰기 0
        Assert.Equal(source.Data, target.Data);
    }

    [Fact]
    public async Task 증분_변경된_청크만_쓴다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        source.Data.CopyTo(target.Data, 0);

        // 청크(32KB) 하나 안에서만 원본이 바뀐 상황 — 그 청크만 쓰여야 합니다.
        source.Data[3 * Buffer + 100] ^= 0xFF;
        source.Data[3 * Buffer + 5000] ^= 0xFF;

        var result = await new CloneEngine().RunAsync(Plan(source, target), IncrementalOptions());

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.True(result.VerificationPassed);
        Assert.Equal(1, target.WriteCount);          // 바뀐 청크 하나만
        Assert.Equal(source.Data, target.Data);      // 최종 상태는 완전 일치
    }

    [Fact]
    public async Task 증분_꺼져_있으면_기존처럼_전부_쓴다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");
        source.Data.CopyTo(target.Data, 0);

        var options = new CloneOptions
        {
            BufferSize = Buffer,
            VerifyAfterClone = true,
            WriteOnlyChangedBlocks = false,
            ReadRetryCount = 0,
            RetryDelay = TimeSpan.Zero,
            ProgressInterval = TimeSpan.Zero,
        };
        await new CloneEngine().RunAsync(Plan(source, target), options);

        Assert.Equal(DeviceSize / Buffer, target.WriteCount); // 모든 청크를 씀
    }

    // --- BackupChain -------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("dm-chain-").FullName;
        public string File(string name)
        {
            string p = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(p, "x");
            return p;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void 사슬_기본_이미지만_있으면_01이_자식이_된다()
    {
        using var dir = new TempDir();
        string basePath = dir.File("backup.vhdx");

        var r = BackupChain.Resolve(basePath);

        Assert.NotNull(r);
        Assert.Equal(basePath, r!.ParentPath);
        Assert.Equal(Path.Combine(dir.Path, "backup-01.vhdx"), r.ChildPath);
    }

    [Fact]
    public void 사슬_가장_높은_번호가_부모가_된다()
    {
        using var dir = new TempDir();
        string basePath = dir.File("backup.vhdx");
        dir.File("backup-01.vhdx");
        string latest = dir.File("backup-03.vhdx"); // 02가 없어도 최고 번호를 따름

        var r = BackupChain.Resolve(basePath);

        Assert.NotNull(r);
        Assert.Equal(latest, r!.ParentPath);
        Assert.Equal(Path.Combine(dir.Path, "backup-04.vhdx"), r.ChildPath);
    }

    [Fact]
    public void 사슬_자식을_골라도_같은_답이_나온다()
    {
        using var dir = new TempDir();
        dir.File("backup.vhdx");
        string child1 = dir.File("backup-01.vhdx");

        var r = BackupChain.Resolve(child1);

        Assert.NotNull(r);
        Assert.Equal(child1, r!.ParentPath);
        Assert.Equal(Path.Combine(dir.Path, "backup-02.vhdx"), r.ChildPath);
    }

    [Fact]
    public void 사슬_기본_이미지가_없으면_null()
    {
        using var dir = new TempDir();
        string orphan = dir.File("backup-05.vhdx"); // base(backup.vhdx)가 없음

        Assert.Null(BackupChain.Resolve(orphan));
    }

    [Fact]
    public void 사슬_이름에_하이픈이_있어도_안전하다()
    {
        using var dir = new TempDir();
        string basePath = dir.File("my-pc-2026.vhdx"); // 끝이 -NN 형태가 아니므로 그대로 base

        var r = BackupChain.Resolve(basePath);

        Assert.NotNull(r);
        Assert.Equal(basePath, r!.ParentPath);
        Assert.Equal(Path.Combine(dir.Path, "my-pc-2026-01.vhdx"), r.ChildPath);
    }
}
