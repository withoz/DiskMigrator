using System.Text.Json;
using DiskMigrator.App;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 앱 안에서 바로 물어볼 때 <b>실제로 넘기는 인자들</b>.
/// </summary>
/// <remarks>
/// 이 목록은 2026-08-10에 실기로 확인한 것입니다 — 진짜 서버를 띄운 채 이 조합으로 부르니
/// <c>diskmigrator-x:connected</c>가 뜨고 <c>list_disks</c>가 실제로 불렸습니다.
///
/// <para>여기서 지키는 것은 그 확인이 <b>조용히 무너지지 않게</b> 하는 것입니다. 인자 하나가
/// 빠지면 실패하지 않고 <i>더 나쁜 일</i>이 일어납니다 — Claude가 도구 없이 그럴듯한 추측으로
/// 답합니다. 디스크를 지우는 도구에서 추측은 최악입니다.</para>
/// </remarks>
public class ClaudeRunnerTests
{
    private static IReadOnlyList<string> Args(bool korean = true) =>
        ClaudeRunner.BuildArguments("왜 부팅이 안 되지?", @"C:\x\DiskMigratorX.Bridge.exe", korean);

    /// <summary>어떤 인자 바로 뒤에 오는 값.</summary>
    private static string? ValueAfter(string flag)
    {
        var args = Args();
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void 질문은_대화형이_아니라_한_번_실행으로_넘긴다()
    {
        // -p가 없으면 대화형으로 떠서 답을 주지 않고 그대로 멈춰 있습니다.
        Assert.Equal("왜 부팅이 안 되지?", ValueAfter("-p"));
    }

    [Fact]
    public void 진행_상황을_받을_수_있는_형식으로_받는다()
    {
        // 몇 분 걸리는 일입니다. 다 끝난 뒤에만 나오면 사용자는 멈춘 줄 알고 앱을 닫습니다.
        Assert.Equal("stream-json", ValueAfter("--output-format"));
        Assert.Contains("--verbose", Args());
    }

    [Fact]
    public void 우리_중계기를_그_자리에서_넘긴다()
    {
        // 이래야 Claude 설정에 아무것도 등록돼 있지 않아도 동작합니다.
        string config = ValueAfter("--mcp-config")!;

        using var doc = JsonDocument.Parse(config);
        var server = doc.RootElement
            .GetProperty("mcpServers")
            .GetProperty(ClaudeRegistration.ServerName);

        Assert.Equal(@"C:\x\DiskMigratorX.Bridge.exe", server.GetProperty("command").GetString());
    }

    [Fact]
    public void 경로에_따옴표가_있어도_설정이_깨지지_않는다()
    {
        // 폴더 이름에 이상한 문자가 들어갈 수 있습니다. 그대로 이어 붙이면 JSON이 깨져
        // 도구가 하나도 안 붙고, Claude는 추측으로 답하게 됩니다.
        string config = ClaudeRunner.McpConfigJson(@"C:\어떤 폴더\a""b\bridge.exe");

        using var doc = JsonDocument.Parse(config);
        Assert.Equal(@"C:\어떤 폴더\a""b\bridge.exe",
            doc.RootElement.GetProperty("mcpServers")
               .GetProperty(ClaudeRegistration.ServerName)
               .GetProperty("command").GetString());
    }

    [Fact]
    public void 우리_도구는_묻지_않고_쓰되_그_밖의_것은_막는다()
    {
        // 화면 없이 도는 실행이라, 허락을 물으면 답할 사람이 없어 그대로 멈춥니다.
        Assert.Equal($"mcp__{ClaudeRegistration.ServerName}", ValueAfter("--allowedTools"));

        // 디스크를 봐 달라고 부른 것이지, 이 컴퓨터의 파일을 뒤져 달라고 부른 것이 아닙니다.
        Assert.Equal("dontAsk", ValueAfter("--permission-mode"));
    }

    [Fact]
    public void 구독_로그인을_쓰는_모드로_부른다()
    {
        // --bare는 로그인을 읽지 않고 API 열쇠를 요구합니다. 그 한 줄이면 "사용자가 이미
        // 쓰는 구독으로 동작한다"는 이 설계의 전제가 통째로 무너집니다.
        Assert.DoesNotContain("--bare", Args());
    }

    [Fact]
    public void 답하는_언어가_화면_언어를_따른다()
    {
        Assert.Contains("한국어", ClaudeRunner.FocusPrompt(korean: true));
        Assert.DoesNotContain("한국어", ClaudeRunner.FocusPrompt(korean: false));
    }

    [Fact]
    public void 확인하지_못한_것을_원인으로_말하지_말라고_일러둔다()
    {
        // 이 제품이 실기에서 배운 것입니다 — "확인 못 함"은 "문제 없음"이 아닙니다.
        Assert.Contains("확인하지 못했다", ClaudeRunner.FocusPrompt(korean: true));
        Assert.Contains("could not verify", ClaudeRunner.FocusPrompt(korean: false));
    }
}
