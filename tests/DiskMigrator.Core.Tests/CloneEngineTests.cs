using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Tests.Fakes;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 클론 엔진을 실제 디스크 없이 전부 검증합니다. IBlockDevice 추상화를 둔 이유가 바로 이것입니다.
/// </summary>
public class CloneEngineTests
{
    private const int Sector = 512;
    private const int DeviceSize = 1024 * Sector; // 512KB

    private static CloneOptions Options(
        BadSectorPolicy policy = BadSectorPolicy.Abort,
        bool verify = true,
        int bufferSize = 64 * Sector,
        int retries = 3) =>
        new()
        {
            BufferSize = bufferSize,
            BadSectorPolicy = policy,
            VerifyAfterClone = verify,
            ReadRetryCount = retries,
            RetryDelay = TimeSpan.Zero, // 테스트에서 실제로 기다릴 이유가 없습니다.
            ProgressInterval = TimeSpan.Zero,
        };

    private static ClonePlan FullDiskPlan(FaultyBlockDevice source, FaultyBlockDevice target) => new()
    {
        Target = target,
        Name = "테스트 클론",
        Regions =
        [
            new CopyRegion
            {
                Source = source,
                SourceOffset = 0,
                TargetOffset = 0,
                Length = source.Length,
                Description = "전체 디스크",
            },
        ],
    };

    [Fact]
    public async Task 전체_복제가_바이트_단위로_정확히_일치한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector, id: "src").FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector, id: "dst");

        var result = await new CloneEngine().RunAsync(FullDiskPlan(source, target), Options());

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.Equal(DeviceSize, result.BytesCopied);
        Assert.True(result.VerificationPassed);
        Assert.Equal(source.Data, target.Data);
    }

    [Fact]
    public async Task 대상이_더_크면_원본_크기만큼만_복사하고_나머지는_건드리지_않는다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize * 2, Sector);

        // 대상 뒤쪽을 표시해 두고, 복제가 이 영역을 건드리지 않는지 확인합니다.
        target.Data.AsSpan(DeviceSize).Fill(0xAB);

        var result = await new CloneEngine().RunAsync(FullDiskPlan(source, target), Options());

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.Equal(source.Data, target.Data[..DeviceSize]);
        Assert.All(target.Data[DeviceSize..].ToArray(), b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public async Task 일시적_읽기_실패는_재시도로_복구되고_불량섹터로_기록되지_않는다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        source.WithTransientFailure(offset: 10 * Sector, times: 2); // 재시도 3회 안에 성공

        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var result = await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(BadSectorPolicy.ZeroFillAndContinue));

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.Empty(result.BadSectors);
        Assert.Equal(source.Data, target.Data);
    }

    [Fact]
    public async Task 불량_섹터에서_Abort_정책이면_작업이_실패한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        source.WithPermanentBadSector(10 * Sector);

        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var result = await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(BadSectorPolicy.Abort));

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task 불량_섹터에서_ZeroFill_정책이면_해당_섹터만_0으로_채우고_계속한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        long badOffset = 10 * Sector;
        source.WithPermanentBadSector(badOffset);

        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var result = await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(BadSectorPolicy.ZeroFillAndContinue));

        Assert.Equal(CloneOutcome.CompletedWithBadSectors, result.Outcome);
        Assert.Single(result.BadSectors);
        Assert.Equal(badOffset, result.BadSectors[0].Offset);

        // 불량 섹터는 0으로 채워집니다.
        Assert.All(target.Data.AsSpan((int)badOffset, Sector).ToArray(), b => Assert.Equal(0, b));

        // 나머지는 살아 있어야 합니다 — 블록 전체를 버리면 안 됩니다.
        Assert.Equal(source.Data.AsSpan(0, (int)badOffset).ToArray(),
                     target.Data.AsSpan(0, (int)badOffset).ToArray());

        int afterBad = (int)badOffset + Sector;
        Assert.Equal(source.Data.AsSpan(afterBad).ToArray(),
                     target.Data.AsSpan(afterBad).ToArray());
    }

    [Fact]
    public async Task 불량_섹터가_있어도_검증은_통과한다()
    {
        // 0으로 채운 섹터는 원본을 읽을 수 없으므로 비교 대상에서 빠져야 합니다.
        // 이걸 빼먹으면 정상적인 작업이 "검증 실패"로 보고됩니다.
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        source.WithPermanentBadSector(10 * Sector);

        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var result = await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(BadSectorPolicy.ZeroFillAndContinue, verify: true));

        Assert.Equal(CloneOutcome.CompletedWithBadSectors, result.Outcome);
        Assert.True(result.VerificationPassed);
    }

    [Fact]
    public async Task 불량_섹터가_한도를_넘으면_중단한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        for (int i = 0; i < 20; i++) source.WithPermanentBadSector(i * Sector);

        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var options = new CloneOptions
        {
            BufferSize = 64 * Sector,
            BadSectorPolicy = BadSectorPolicy.ZeroFillAndContinue,
            MaxBadSectors = 5,
            RetryDelay = TimeSpan.Zero,
            ReadRetryCount = 0,
            VerifyAfterClone = false,
        };

        var result = await new CloneEngine().RunAsync(FullDiskPlan(source, target), options);

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("심각하게", result.ErrorMessage);
    }

    [Fact]
    public async Task 검증이_대상_변조를_잡아낸다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new TamperingBlockDevice(DeviceSize, Sector, tamperAtOffset: 20 * Sector);

        var plan = new ClonePlan
        {
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = source,
                    SourceOffset = 0,
                    TargetOffset = 0,
                    Length = source.Length,
                    Description = "전체 디스크",
                },
            ],
        };

        var result = await new CloneEngine().RunAsync(plan, Options(verify: true));

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.False(result.VerificationPassed);
        Assert.NotEmpty(result.VerificationMismatches);
        Assert.Contains("신뢰하지 마십시오", result.ErrorMessage);
    }

    [Fact]
    public async Task 불안정_구간은_검증에서_제외되어_불일치를_보고하지_않는다()
    {
        // 라이브 소스(EFI 등)를 흉내냅니다: 복제 후 소스가 바뀌어도, 그 구간이
        // IsStableForVerification=false면 검증이 재비교하지 않아 실패로 보고되면 안 됩니다.
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var plan = new ClonePlan
        {
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = source,
                    SourceOffset = 0,
                    TargetOffset = 0,
                    Length = source.Length,
                    Description = "라이브 소스 구간",
                    IsStableForVerification = false,
                },
            ],
        };

        // 먼저 복제합니다 (검증 없이).
        var copyResult = await new CloneEngine().RunAsync(plan, Options(verify: false));
        Assert.Equal(CloneOutcome.Completed, copyResult.Outcome);

        // 이제 소스를 바꿉니다 — 라이브 디스크가 복제~검증 사이에 변한 상황.
        source.Data[100] ^= 0xFF;

        // 검증을 켜고 다시 실행하면, 이 구간은 검증에서 제외되므로 통과해야 합니다.
        var verifyResult = await new CloneEngine().RunAsync(plan, Options(verify: true));

        Assert.NotEqual(CloneOutcome.Failed, verifyResult.Outcome);
        Assert.Empty(verifyResult.VerificationMismatches);
    }

    [Fact]
    public async Task 안정_구간과_불안정_구간이_섞이면_안정_구간만_검증한다()
    {
        var stableSource = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern(seed: 1);
        var liveSource = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern(seed: 2);
        var target = new FaultyBlockDevice(DeviceSize * 2, Sector);

        var plan = new ClonePlan
        {
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = stableSource, SourceOffset = 0, TargetOffset = 0,
                    Length = stableSource.Length, Description = "스냅샷 구간",
                    IsStableForVerification = true,
                },
                new CopyRegion
                {
                    Source = liveSource, SourceOffset = 0, TargetOffset = DeviceSize,
                    Length = liveSource.Length, Description = "라이브 구간",
                    IsStableForVerification = false,
                },
            ],
        };

        await new CloneEngine().RunAsync(plan, Options(verify: false));

        // 라이브 소스만 바꿉니다. 안정 구간은 그대로.
        liveSource.Data[50] ^= 0xFF;

        var result = await new CloneEngine().RunAsync(plan, Options(verify: true));

        // 안정 구간은 여전히 일치하고, 라이브 구간은 검증 제외이므로 전체가 통과해야 합니다.
        Assert.True(result.VerificationPassed);
        Assert.NotEqual(CloneOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task 복제_후_원본이_드리프트해도_검증은_통과한다()
    {
        // 이것이 해시 기반 검증의 핵심입니다. 실기에서 VSS 스냅샷은 복제 후 시간이 지나면
        // (섀도 저장소 드리프트로) 값이 바뀝니다. 검증이 원본을 다시 읽으면 가짜 불일치가
        // 나지만, 우리는 "복제 때 쓴 데이터"의 해시와 대상을 비교하므로 원본 드리프트에
        // 영향받지 않아야 합니다.
        var source = new DriftingBlockDevice(DeviceSize, Sector);
        source.FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var plan = new ClonePlan
        {
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = source, SourceOffset = 0, TargetOffset = 0,
                    Length = source.Length, Description = "스냅샷 흉내",
                },
            ],
        };

        // 복제가 끝난 뒤 원본을 바꾸도록 설정 (스냅샷 드리프트 시뮬레이션).
        source.DriftAfterReads = source.Length / Sector; // 전체를 한 번 읽고 나면 드리프트

        var result = await new CloneEngine().RunAsync(plan, Options(verify: true));

        // 원본이 검증 시점에 달라졌어도, 대상은 복제 때 쓴 그대로이므로 검증은 통과해야 합니다.
        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.True(result.VerificationPassed);
        Assert.Empty(result.VerificationMismatches);
    }

    [Fact]
    public async Task 취소하면_Cancelled로_보고한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        using var cts = new CancellationTokenSource();

        // 원본을 몇 번 읽고 나면 취소합니다. Progress 콜백으로 취소를 걸면 Progress<T>가
        // 비동기로 디스패치되는 사이에 512KB 복사가 끝나 버려 테스트가 간헐적으로 실패합니다.
        var cancellingSource = new CallbackBlockDevice(source, readCount =>
        {
            if (readCount == 3) cts.Cancel();
        });

        var plan = new ClonePlan
        {
            Target = target,
            Regions =
            [
                new CopyRegion
                {
                    Source = cancellingSource,
                    SourceOffset = 0,
                    TargetOffset = 0,
                    Length = source.Length,
                    Description = "전체 디스크",
                },
            ],
        };

        var result = await new CloneEngine().RunAsync(
            plan, Options(bufferSize: Sector), progress: null, pause: null, cts.Token);

        Assert.Equal(CloneOutcome.Cancelled, result.Outcome);
        Assert.Contains("사용하지 마십시오", result.ErrorMessage);

        // 취소 시점까지 복사한 만큼만 보고해야 합니다.
        Assert.InRange(result.BytesCopied, 0, DeviceSize - 1);
    }

    [Fact]
    public async Task 진행률이_0에서_100까지_보고된다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        // Progress<T>가 아니라 동기 수집기를 씁니다. Progress<T>는 캡처한 동기화 컨텍스트로
        // 콜백을 비동기 게시하므로, 작업이 끝난 시점에 마지막 보고가 아직 도착하지 않을 수 있습니다.
        var reports = new SynchronousProgress();

        var result = await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(verify: false, bufferSize: 8 * Sector), reports);

        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.NotEmpty(reports.Items);
        Assert.All(reports.Items, p => Assert.InRange(p.Percent, 0, 100));
        Assert.Equal(100, reports.Items[^1].Percent, precision: 3);
        Assert.Equal("복제", reports.Items[^1].Phase);
    }

    [Fact]
    public async Task 검증_단계도_진행률을_보고한다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        var reports = new SynchronousProgress();

        await new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(verify: true, bufferSize: 8 * Sector), reports);

        Assert.Contains(reports.Items, p => p.Phase == "복제");
        Assert.Contains(reports.Items, p => p.Phase == "검증");
    }

    /// <summary>동기적으로 진행 보고를 모읍니다 — 디스패치 지연이 없어 테스트가 결정적입니다.</summary>
    private sealed class SynchronousProgress : IProgress<CloneProgress>
    {
        private readonly List<CloneProgress> _items = [];

        public IReadOnlyList<CloneProgress> Items
        {
            get { lock (_items) return _items.ToList(); }
        }

        public void Report(CloneProgress value)
        {
            lock (_items) _items.Add(value);
        }
    }

    /// <summary>읽기 횟수마다 콜백을 불러, 특정 시점에 결정적으로 개입할 수 있게 합니다.</summary>
    private sealed class CallbackBlockDevice(FaultyBlockDevice inner, Action<int> onRead)
        : Core.Abstractions.IBlockDevice
    {
        private int _readCount;

        public string Id => inner.Id;
        public long Length => inner.Length;
        public int SectorSize => inner.SectorSize;
        public bool CanWrite => inner.CanWrite;

        public int Read(long offset, Span<byte> buffer)
        {
            onRead(++_readCount);
            return inner.Read(offset, buffer);
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer) => inner.Write(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose() { }
    }

    [Fact]
    public async Task 일시정지_후_재개하면_정상적으로_끝난다()
    {
        var source = new FaultyBlockDevice(DeviceSize, Sector).FillWithPattern();
        var target = new FaultyBlockDevice(DeviceSize, Sector);

        using var pause = new PauseController();
        pause.Pause();

        var task = new CloneEngine().RunAsync(
            FullDiskPlan(source, target), Options(bufferSize: Sector), pause: pause);

        // 멈춘 상태에서는 끝나지 않아야 합니다.
        var finished = await Task.WhenAny(task, Task.Delay(150));
        Assert.NotSame(task, finished);
        Assert.True(pause.IsPaused);

        pause.Resume();

        var result = await task;
        Assert.Equal(CloneOutcome.Completed, result.Outcome);
        Assert.Equal(source.Data, target.Data);
    }

    /// <summary>
    /// 일정 횟수만큼 읽힌 뒤부터는 다른 데이터를 반환하는 장치.
    /// VSS 스냅샷이 복제 후 드리프트하는 상황을 흉내 냅니다.
    /// </summary>
    private sealed class DriftingBlockDevice(long length, int sectorSize)
        : FaultyBlockDevice(length, sectorSize)
    {
        private long _reads;

        /// <summary>이 횟수를 넘어선 읽기부터는 데이터를 뒤집어 반환합니다.</summary>
        public long DriftAfterReads { get; set; } = long.MaxValue;

        public override int Read(long offset, Span<byte> buffer)
        {
            int read = base.Read(offset, buffer);
            if (_reads++ >= DriftAfterReads)
            {
                // 드리프트: 모든 바이트를 뒤집어, 복제 시점과 다른 값을 반환.
                for (int i = 0; i < read; i++) buffer[i] ^= 0xFF;
            }
            return read;
        }
    }

    /// <summary>쓴 내용을 몰래 바꿔서, 검증이 실제로 다시 읽어 비교하는지 확인합니다.</summary>
    private sealed class TamperingBlockDevice(long length, int sectorSize, long tamperAtOffset)
        : FaultyBlockDevice(length, sectorSize)
    {
        public override void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            base.Write(offset, buffer);

            // 이 구간이 쓰인 직후 한 바이트를 뒤집습니다 — 매체가 조용히 데이터를 잃는 상황.
            if (offset <= tamperAtOffset && tamperAtOffset < offset + buffer.Length)
            {
                Data[(int)tamperAtOffset] ^= 0xFF;
            }
        }
    }
}
