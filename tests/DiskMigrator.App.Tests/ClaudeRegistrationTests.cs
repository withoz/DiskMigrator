using System.IO;
using System.Text.Json.Nodes;
using DiskMigrator.App;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// [Claude에 연결하기]가 <b>남의 설정 파일</b>에 하는 일.
/// </summary>
/// <remarks>
/// 이 코드는 사용자의 Claude 설정을 고칩니다. 잘못 쓰면 이미 등록해 둔 다른 서버들이
/// 사라지고, 사용자는 우리 앱 때문에 잃었다는 것도 모릅니다. 눈으로 맞출 수 있는 자리가
/// 아닙니다.
/// </remarks>
public class ClaudeRegistrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "dmx-reg-" + Guid.NewGuid().ToString("N")[..8]);

    private string ConfigPath => Path.Combine(_folder, "claude_desktop_config.json");

    public ClaudeRegistrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* 임시 폴더입니다. */ }
        GC.SuppressFinalize(this);
    }

    private JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath))!;

    [Fact]
    public void 설정_파일이_없으면_새로_만든다()
    {
        var result = ClaudeRegistration.RegisterDesktopApp(@"C:\x\DiskMigratorX.Bridge.exe", ConfigPath);

        Assert.Equal(ClaudeRegistrationStatus.Registered, result.Status);
        Assert.True(File.Exists(ConfigPath));

        var entry = ReadConfig()["mcpServers"]![ClaudeRegistration.ServerName]!;
        Assert.Equal(@"C:\x\DiskMigratorX.Bridge.exe", entry["command"]!.GetValue<string>());
    }

    [Fact]
    public void 이미_있던_다른_서버와_설정은_그대로_둔다()
    {
        // 사용자가 이미 등록해 둔 것들. 이것을 잃으면 우리 잘못입니다.
        File.WriteAllText(ConfigPath, """
        {
          "preferences": { "theme": "dark" },
          "mcpServers": {
            "someone-elses": { "command": "other.exe", "args": ["--x"] }
          }
        }
        """);

        ClaudeRegistration.RegisterDesktopApp(@"C:\x\bridge.exe", ConfigPath);

        var root = ReadConfig();
        Assert.Equal("dark", root["preferences"]!["theme"]!.GetValue<string>());
        Assert.Equal("other.exe", root["mcpServers"]!["someone-elses"]!["command"]!.GetValue<string>());
        Assert.Equal(@"C:\x\bridge.exe", root["mcpServers"]![ClaudeRegistration.ServerName]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void 덮어쓰기_전에_원본을_사본으로_남긴다()
    {
        const string before = """{"mcpServers":{"keep":{"command":"a.exe"}}}""";
        File.WriteAllText(ConfigPath, before);

        ClaudeRegistration.RegisterDesktopApp(@"C:\x\bridge.exe", ConfigPath);

        Assert.True(File.Exists(ConfigPath + ".bak"));
        Assert.Equal(before, File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void 다시_눌러도_항목이_늘어나지_않고_경로만_바뀐다()
    {
        ClaudeRegistration.RegisterDesktopApp(@"C:\old\bridge.exe", ConfigPath);
        ClaudeRegistration.RegisterDesktopApp(@"C:\new\bridge.exe", ConfigPath);

        var servers = (JsonObject)ReadConfig()["mcpServers"]!;
        Assert.Single(servers);
        Assert.Equal(@"C:\new\bridge.exe", servers[ClaudeRegistration.ServerName]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void 항목이_깨져_있어도_등록은_된다()
    {
        // mcpServers 자리에 엉뚱한 것이 들어 있는 경우. 여기서 예외가 나면 버튼이 죽습니다.
        File.WriteAllText(ConfigPath, """{"mcpServers": "이건 객체가 아닙니다"}""");

        var result = ClaudeRegistration.RegisterDesktopApp(@"C:\x\bridge.exe", ConfigPath);

        Assert.Equal(ClaudeRegistrationStatus.Registered, result.Status);
        Assert.Equal(@"C:\x\bridge.exe",
            ReadConfig()["mcpServers"]![ClaudeRegistration.ServerName]!["command"]!.GetValue<string>());
    }

    /// <summary>
    /// "이미 있습니다"를 <b>실패로 착각하지 않는지</b> — 실기에서 화면에 붉은 글로 나온 자리.
    /// </summary>
    /// <remarks>
    /// 두 번째로 버튼을 누른 사람은 멀쩡한 상태를 두고 "등록하지 못했습니다"를 봤습니다.
    /// 이제는 우리 이름 하나만 지우고 다시 넣습니다 — 앱을 다른 곳에 재설치했을 때 옛 등록이
    /// 없어진 중계기를 가리킨 채 남는 것도 이때 함께 풀립니다.
    /// </remarks>
    [Theory]
    [InlineData("MCP server diskmigrator-x already exists in user config", true)]
    [InlineData("MCP server DISKMIGRATOR-X ALREADY EXISTS in user config", true)]
    [InlineData("MCP server other-thing already exists in user config", false)]  // 남의 이름
    [InlineData("EACCES: permission denied", false)]
    [InlineData("", false)]
    public void 이미_있다는_거절만_골라낸다(string message, bool expected)
    {
        Assert.Equal(expected, ClaudeRegistration.IsAlreadyExists(message));
    }

    [Fact]
    public void 토큰은_설정_파일에_적지_않는다()
    {
        ClaudeRegistration.RegisterDesktopApp(@"C:\x\bridge.exe", ConfigPath);

        // 중계기가 스스로 꺼내 씁니다. 평문 파일에 열쇠가 남으면 안 됩니다.
        string text = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
    }
}
