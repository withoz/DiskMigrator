using DiskMigrator.Core.Registry;
using Xunit;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 부팅 흔적으로 "어디까지 갔는지"를 판정하는 규칙 — 진단의 핵심이라 실제 사례로 고정합니다.
/// </summary>
/// <remarks>
/// 특히 첫 테스트는 2026-08-04 부팅 조사에서 실제로 마주친 타임스탬프 배치입니다.
/// 그때는 눈으로 대조해 "커널이 시작조차 못 했다"를 알아냈고, 그 판단이 원인 후보를 좁혔습니다.
/// 같은 입력에서 같은 결론이 나오지 않으면 이 도구는 쓸모가 없습니다.
/// </remarks>
public class BootTraceAnalysisTests
{
    private static readonly DateTime VmBoot = new(2026, 8, 1, 12, 7, 0, DateTimeKind.Utc);
    private static readonly DateTime RealAttempt = new(2026, 8, 3, 10, 17, 0, DateTimeKind.Utc);

    private static BootTraceFile F(string name, BootProgress stage, DateTime? when) =>
        new(name, when is not null, when, when is null ? null : 1024, stage, "");

    /// <summary>
    /// 2026-08-04 조사의 실제 배치 — 부트로더 시각만 최신이고 나머지는 이틀 전 그대로.
    /// </summary>
    [Fact]
    public void 부트로더만_최신이면_커널은_시작하지_못한_것()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            F("SYSTEM hive", BootProgress.KernelStarted, VmBoot),
            F("System event log", BootProgress.KernelStarted, VmBoot),
            F("setupapi.dev.log", BootProgress.DevicesEnumerated, VmBoot),
            F("CBS.log", BootProgress.BootCompleted, VmBoot),
        };

        Assert.Equal(BootProgress.BootloaderOnly, BootTraceAnalysis.Judge(files));
        Assert.Equal(RealAttempt, BootTraceAnalysis.LastAttemptOf(files));
    }

    [Fact]
    public void 하이브까지_갱신되면_커널은_시작한_것()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            F("SYSTEM hive", BootProgress.KernelStarted, RealAttempt.AddMinutes(1)),
            F("setupapi.dev.log", BootProgress.DevicesEnumerated, VmBoot),
        };

        Assert.Equal(BootProgress.KernelStarted, BootTraceAnalysis.Judge(files));
    }

    [Fact]
    public void 장치_설치까지_갔으면_그_단계로_판정()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            F("SYSTEM hive", BootProgress.KernelStarted, RealAttempt.AddMinutes(1)),
            F("setupapi.dev.log", BootProgress.DevicesEnumerated, RealAttempt.AddMinutes(6)),
        };

        Assert.Equal(BootProgress.DevicesEnumerated, BootTraceAnalysis.Judge(files));
    }

    [Fact]
    public void 서비싱까지_돌았으면_부팅이_끝까지_간_것()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            F("SYSTEM hive", BootProgress.KernelStarted, RealAttempt.AddMinutes(1)),
            F("setupapi.dev.log", BootProgress.DevicesEnumerated, RealAttempt.AddMinutes(6)),
            F("CBS.log", BootProgress.BootCompleted, RealAttempt.AddMinutes(40)),
        };

        Assert.Equal(BootProgress.BootCompleted, BootTraceAnalysis.Judge(files));
    }

    /// <summary>
    /// 한참 전에 성공했던 부팅의 흔적을, 이번 시도의 성과로 오해하면 안 됩니다.
    /// </summary>
    [Fact]
    public void 오래된_성공_흔적은_이번_시도로_치지_않는다()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            // 이틀 전 VM에서 끝까지 부팅했던 기록 — 시간 창 밖입니다.
            F("CBS.log", BootProgress.BootCompleted, VmBoot),
        };

        Assert.Equal(BootProgress.BootloaderOnly, BootTraceAnalysis.Judge(files));
    }

    /// <summary>
    /// 2026-08-05 실물 M.2에서 읽은 배치 — 13:19에 부팅해 한 시간 쓰고 14:18에 정상 종료한 흔적.
    /// </summary>
    /// <remarks>
    /// 이 사례가 판정 로직의 결함을 드러냈습니다. <c>bootstat.dat</c>는 <b>종료할 때도</b> 갱신되므로
    /// 그 시각을 "부팅 시작"으로 잡으면, 부팅 도중에 쓰인 CBS.log가 한 시간 이전으로 보여
    /// 단계를 낮게 판정합니다. 기준을 '가장 최근 활동'으로 바꿔 고쳤습니다.
    /// 단위 테스트만으로는 못 찾았을 결함입니다 — 실물 검증이 잡아냈습니다.
    /// </remarks>
    [Fact]
    public void 한참_쓰고_종료한_부팅도_완료로_판정한다()
    {
        var shutdown = new DateTime(2026, 8, 4, 14, 18, 30, DateTimeKind.Utc);

        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, shutdown),                   // 종료 시에도 갱신
            F("SYSTEM hive", BootProgress.KernelStarted, shutdown),
            F("System event log", BootProgress.KernelStarted, shutdown.AddSeconds(-2)),
            F("setupapi.dev.log", BootProgress.DevicesEnumerated, shutdown.AddMinutes(-25)),
            F("CBS.log", BootProgress.BootCompleted, shutdown.AddMinutes(-59)),         // 부팅 중 서비싱
        };

        Assert.Equal(BootProgress.BootCompleted, BootTraceAnalysis.Judge(files));
    }

    /// <summary>
    /// 파일마다 기록 순서가 조금씩 달라 부트로더보다 살짝 이른 시각이 찍힐 수 있습니다.
    /// 그 정도는 같은 부팅으로 봅니다.
    /// </summary>
    [Fact]
    public void 몇_분_이른_시각은_같은_부팅으로_본다()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, RealAttempt),
            F("SYSTEM hive", BootProgress.KernelStarted, RealAttempt.AddMinutes(-2)),
        };

        Assert.Equal(BootProgress.KernelStarted, BootTraceAnalysis.Judge(files));
    }

    [Fact]
    public void 부트로더_흔적이_없으면_판정하지_않는다()
    {
        var files = new[]
        {
            F("bootstat.dat", BootProgress.BootloaderOnly, null),
            F("SYSTEM hive", BootProgress.KernelStarted, RealAttempt),
        };

        Assert.Equal(BootProgress.Unknown, BootTraceAnalysis.Judge(files));
        Assert.Null(BootTraceAnalysis.LastAttemptOf(files));
    }

    [Fact]
    public void 빈_목록도_안전하게_처리한다()
    {
        Assert.Equal(BootProgress.Unknown, BootTraceAnalysis.Judge([]));
        Assert.Throws<ArgumentNullException>(() => BootTraceAnalysis.Judge(null!));
    }
}
