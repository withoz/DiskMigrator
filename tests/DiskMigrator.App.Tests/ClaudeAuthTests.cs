using DiskMigrator.App;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// Claude 로그인 상태 읽기.
/// </summary>
/// <remarks>
/// 이 값 하나로 머리말이 셋 중 하나로 갈립니다 — 계정 표시 · [로그인] · [Claude Code 없음].
/// 잘못 읽으면 <b>이미 로그인한 사람에게 로그인하라고</b> 하거나, 반대로 로그인해야 할
/// 사람에게 아무 안내도 안 하게 됩니다.
///
/// <para>형식은 남이 정합니다. 바뀌더라도 <b>앱이 죽지 않고 "모른다"로 떨어지는 것</b>이
/// 여기서 지켜야 할 선입니다.</para>
/// </remarks>
public class ClaudeAuthTests
{
    /// <summary>2026-08-13 실기에서 받은 실제 출력.</summary>
    private const string RealOutput = """
    {
      "loggedIn": true,
      "authMethod": "claude.ai",
      "apiProvider": "firstParty",
      "email": "someone@example.com",
      "orgId": "5200feac-6a15-4960-bbf4-3c30143f3727",
      "orgName": "someone@example.com's Organization",
      "subscriptionType": "max"
    }
    """;

    [Fact]
    public void 로그인된_계정을_읽는다()
    {
        var a = ClaudeAuth.Parse(RealOutput);

        Assert.True(a.Installed);
        Assert.True(a.LoggedIn);
        Assert.Equal("someone@example.com", a.Email);
        Assert.Equal("max", a.Plan);
    }

    [Fact]
    public void 로그아웃_상태를_로그인으로_읽지_않는다()
    {
        var a = ClaudeAuth.Parse("""{"loggedIn": false}""");

        Assert.True(a.Installed);
        Assert.False(a.LoggedIn);
    }

    [Fact]
    public void 항목이_없으면_로그인으로_보지_않는다()
    {
        // 있는지 없는지 모를 때 "로그인됨"으로 넘기면, 사용자는 왜 안 되는지 모른 채 막힙니다.
        Assert.False(ClaudeAuth.Parse("{}").LoggedIn);
    }

    [Fact]
    public void 형식이_달라져도_앱이_죽지_않는다()
    {
        // 출력 형식은 남이 정합니다. 바뀌는 날 앱이 시작조차 못 하면 안 됩니다.
        string[] broken = ["", "not json", "[]", "null", "{\"loggedIn\":\"yes\"}"];
        foreach (string bad in broken)
        {
            var a = ClaudeAuth.Parse(bad);
            Assert.True(a.Installed);     // 부르는 데는 성공했으므로 '있음'
            Assert.False(a.LoggedIn);     // 다만 로그인 여부는 모릅니다
        }
    }

    [Fact]
    public void 없는_것과_로그아웃은_다르다()
    {
        // 화면이 [Claude Code 없음]과 [로그인]으로 갈리므로 섞이면 안 됩니다.
        Assert.False(ClaudeAccount.NotInstalled.Installed);
        Assert.True(ClaudeAuth.Parse("""{"loggedIn":false}""").Installed);
    }
}
