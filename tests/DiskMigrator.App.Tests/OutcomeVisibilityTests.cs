using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using DiskMigrator.App.ViewModels;
using DiskMigrator.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 작업이 <b>끝난 뒤</b> Claude가 보는 것과 화면이 보여 주는 것이 같은지 — 계획서 2단계 완료 조건.
/// </summary>
/// <remarks>
/// <b>왜 끝난 뒤가 위험한가.</b> 예전에는 작업이 끝나면 통로에 "실행 중 아님"만 남았습니다.
/// 그래서 Claude는 그것이 <b>끝난 것인지 취소된 것인지 실패한 것인지 구분할 수 없었고</b>,
/// 물어보면 "복사가 끝난 것 같습니다"라고 답할 수밖에 없었습니다. 화면에는 "취소됨 — 대상은
/// 불완전한 사본입니다"라고 떠 있는데도요. 그 말을 믿고 사용자가 원본을 지우면 데이터가 사라집니다.
/// </remarks>
public class OutcomeVisibilityTests
{
    [Fact]
    public void 아무것도_끝나지_않았으면_지어내지_않는다()
    {
        var (vm, bridge) = New();
        vm.Stage = AppStage.Selecting;

        Assert.Null(bridge.GetProgress().LastOutcome);
    }

    [Fact]
    public void 취소로_끝난_것을_완료라고_말하지_않는다()
    {
        var (vm, bridge) = New();
        Finish(vm, success: false, cancelled: true, message: "취소했습니다. 만들던 이미지는 지웠습니다.");

        var progress = bridge.GetProgress();

        Assert.False(progress.Running);
        Assert.Equal(OperationOutcomes.Cancelled, progress.LastOutcome);

        // 화면이 사용자에게 하는 말을 그대로 넘겨야, 둘이 다른 소리를 하지 않습니다.
        Assert.Equal("취소했습니다. 만들던 이미지는 지웠습니다.", progress.LastMessage);
    }

    /// <remarks>
    /// 둘 다 "성공이 아님"이지만 사용자에게는 전혀 다른 일입니다 — 하나는 앱이 못 한 것이고
    /// 하나는 사용자가 스스로 멈춘 것입니다.
    /// </remarks>
    [Fact]
    public void 실패와_취소를_구분한다()
    {
        var (failed, failedBridge) = New();
        Finish(failed, success: false, cancelled: false, message: "장치를 열지 못했습니다.");

        var (cancelled, cancelledBridge) = New();
        Finish(cancelled, success: false, cancelled: true, message: "취소했습니다.");

        Assert.Equal(OperationOutcomes.Failed, failedBridge.GetProgress().LastOutcome);
        Assert.Equal(OperationOutcomes.Cancelled, cancelledBridge.GetProgress().LastOutcome);
    }

    [Fact]
    public void 성공은_성공으로_보인다()
    {
        var (vm, bridge) = New();
        Finish(vm, success: true, cancelled: false, message: "복제를 마쳤습니다.");

        Assert.Equal(OperationOutcomes.Completed, bridge.GetProgress().LastOutcome);
    }

    /// <summary>일시정지는 <b>멈춘 것이지 막힌 것이 아닙니다.</b></summary>
    /// <remarks>
    /// 이것이 안 보이면 Claude는 숫자가 안 움직이는 것만 보고 "멈춘 것 같습니다"라고 말합니다.
    /// 사용자가 방금 스스로 누른 것인데도요.
    /// </remarks>
    [Fact]
    public void 일시정지가_보인다()
    {
        var (vm, bridge) = New();
        vm.Stage = AppStage.Running;
        vm.IsPaused = true;

        var progress = bridge.GetProgress();

        Assert.True(progress.Running);
        Assert.True(progress.Paused);
    }

    /// <summary>끝맺음 값이 <b>다음 작업에 묻어 가지</b> 않는지.</summary>
    [Fact]
    public void 앞_작업의_끝맺음이_남지_않는다()
    {
        var (vm, bridge) = New();

        Finish(vm, success: false, cancelled: true, message: "취소했습니다.");
        Assert.Equal(OperationOutcomes.Cancelled, bridge.GetProgress().LastOutcome);

        Finish(vm, success: false, cancelled: false, message: "장치를 열지 못했습니다.");
        Assert.Equal(OperationOutcomes.Failed, bridge.GetProgress().LastOutcome);
    }

    /// <summary>
    /// 취소로 끝나는 자리가 <b>실제로 취소라고 표시하는지</b> — 배선을 소스에서 확인합니다.
    /// </summary>
    /// <remarks>
    /// 위 시험들은 다리(<see cref="AppStateBridge"/>)가 값을 어떻게 옮기는지만 봅니다. 정작
    /// 값을 세우는 곳은 뷰모델의 끝맺음 자리들이고, 거기서 <c>cancelled: true</c>를 빠뜨리면
    /// 취소가 조용히 "실패"로 보고됩니다 — 실기 없이 확인할 수 있는 부분이라 여기서 막습니다.
    ///
    /// <para>취소 결과 화면을 띄우는 자리는 제목으로 알 수 있습니다(<c>ResTitleCancelled</c>).
    /// 그 자리가 몇 군데인지도 함께 셉니다 — 새로 생긴 자리가 조용히 빠지지 않도록.</para>
    /// </remarks>
    [Fact]
    public void 취소로_끝나는_자리가_모두_취소라고_말한다()
    {
        string source = ViewModelSource();

        var calls = Regex.Matches(source, @"ShowFailure\((?:[^()]|\([^()]*\))*\)", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(call => call.Contains("ResTitleCancelled", StringComparison.Ordinal))
            .ToArray();

        // 백업과 복원 — 취소가 예외로 올라오는 두 경로.
        Assert.Equal(2, calls.Length);

        foreach (string call in calls)
            Assert.Contains("cancelled: true", call, StringComparison.Ordinal);

        // 클론은 예외가 아니라 결과(Outcome)로 취소가 돌아옵니다.
        Assert.Contains("ResultWasCancelled = result.Outcome is CloneOutcome.Cancelled", source,
            StringComparison.Ordinal);
    }

    // --- 만들기 도구 ----------------------------------------------------------

    private static (MainViewModel Vm, AppStateBridge Bridge) New()
    {
        var vm = new MainViewModel(NullLoggerFactory.Instance);
        return (vm, new AppStateBridge(vm));
    }

    /// <summary>결과 화면이 뜬 상태 — 앱의 끝맺음 자리들이 채우는 값 그대로.</summary>
    private static void Finish(MainViewModel vm, bool success, bool cancelled, string message)
    {
        vm.ResultIsSuccess = success;
        vm.ResultWasCancelled = cancelled;
        vm.ResultMessage = message;
        vm.Stage = AppStage.Finished;
    }

    private static string ViewModelSource()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "src", "DiskMigrator.App", "ViewModels", "MainViewModel.cs");
        Assert.True(File.Exists(path), $"뷰모델 소스를 찾지 못했습니다: {path}");

        return File.ReadAllText(path);
    }
}
