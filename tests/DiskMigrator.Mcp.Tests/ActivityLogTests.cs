using System.Reflection;
using DiskMigrator.Mcp;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// Claude 활동 기록 — 사용자가 "읽기만 했다"를 눈으로 확인하는 목록입니다.
/// </summary>
public class ActivityLogTests
{
    // --- 종류 판정 -----------------------------------------------------------

    [Theory]
    [InlineData("list_disks", McpActivityKind.Read)]
    [InlineData("inspect_disk", McpActivityKind.Read)]
    [InlineData("explain_boot_failure", McpActivityKind.Read)]
    [InlineData("plan_clone", McpActivityKind.Read)]
    [InlineData("propose_clone", McpActivityKind.Proposal)]
    [InlineData("propose_restore", McpActivityKind.Proposal)]
    [InlineData("propose_boot_repair", McpActivityKind.Proposal)]
    [InlineData("cancel_operation", McpActivityKind.Cancel)]
    [InlineData("initialize", McpActivityKind.Protocol)]
    [InlineData("tools/list", McpActivityKind.Protocol)]
    public void 도구_이름으로_성격을_가른다(string tool, McpActivityKind expected) =>
        Assert.Equal(expected, McpActivityLog.Classify(tool));

    /// <summary>
    /// 제안 도구가 늘어나도 자동으로 '제안'으로 잡혀야 합니다 — 접두사로 가르는 이유입니다.
    /// </summary>
    [Fact]
    public void 새_제안_도구도_제안으로_잡힌다() =>
        Assert.Equal(McpActivityKind.Proposal, McpActivityLog.Classify("propose_something_new"));

    // --- 보관 --------------------------------------------------------------

    [Fact]
    public void 오래된_기록은_밀려난다()
    {
        var log = new McpActivityLog();
        for (int i = 0; i < McpActivityLog.Capacity + 20; i++)
        {
            log.Record(new McpActivity(
                DateTime.Now, $"tool{i}", "", McpActivityKind.Read, false, 1));
        }

        var items = log.Snapshot();
        Assert.Equal(McpActivityLog.Capacity, items.Count);

        // 가장 오래된 20건이 밀려나고 최신이 남아야 합니다.
        Assert.Equal("tool20", items[0].Tool);
        Assert.Equal($"tool{McpActivityLog.Capacity + 19}", items[^1].Tool);
    }

    [Fact]
    public void 기록하면_알린다()
    {
        var log = new McpActivityLog();
        McpActivity? got = null;
        log.Recorded += (_, a) => got = a;

        log.Record(new McpActivity(DateTime.Now, "list_disks", "디스크 0", McpActivityKind.Read, false, 5));

        Assert.NotNull(got);
        Assert.Equal("list_disks", got.Tool);
    }

    // --- 거절 판정 (실기에서 두 번 틀렸던 자리) -------------------------------

    /// <summary>
    /// 도구가 거절해도 HTTP 200으로 옵니다. 오류는 <b>본문 안</b>에 있고, 그 본문에서
    /// 따옴표가 어떻게 이스케이프되는지가 관건입니다.
    /// </summary>
    /// <remarks>
    /// 실기에서 두 번 틀렸습니다. 처음에는 상태 코드만 봐서 못 잡았고, 다음에는
    /// <c>\"ok\":false</c>로 찾았는데 SDK가 <c>"</c>로 이스케이프해 또 못 잡았습니다.
    /// 형태가 바뀌면 조용히 다시 안 잡히므로 시험으로 고정합니다.
    /// </remarks>
    [Theory]
    [InlineData(BodyShape.SdkUnicodeEscape, true)]   // SDK가 실제로 보내는 형태
    [InlineData(BodyShape.BackslashQuote, true)]     // 흔한 다른 형태
    [InlineData(BodyShape.ProtocolError, true)]      // MCP 규약 수준 오류
    [InlineData(BodyShape.Success, false)]           // 정상 응답은 붉게 칠하지 않아야 합니다
    public void 본문에서_거절을_알아본다(BodyShape shape, bool expected)
    {
        // 이스케이프가 핵심이라 문자열을 코드로 만듭니다 — 소스에 그대로 적으면
        // 편집기·컴파일러를 거치며 형태가 바뀌어, 무엇을 시험하는지 흐려집니다.
        const string Bs = "\\";          // 역슬래시 한 개
        string body = shape switch
        {
            BodyShape.SdkUnicodeEscape =>
                $"data: {{\"result\":{{\"content\":[{{\"text\":\"{{{Bs}u0022ok{Bs}u0022:false}}\"}}]}}}}",
            BodyShape.BackslashQuote =>
                $"data: {{\"result\":{{\"content\":[{{\"text\":\"{{{Bs}\"ok{Bs}\":false}}\"}}]}}}}",
            BodyShape.ProtocolError =>
                "data: {\"result\":{\"isError\":true}}",
            _ =>
                $"data: {{\"result\":{{\"content\":[{{\"text\":\"{{{Bs}u0022ok{Bs}u0022:true}}\"}}]}}}}",
        };

        var method = typeof(McpHost).GetMethod(
            "LooksLikeError", BindingFlags.NonPublic | BindingFlags.Static)!;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        bool actual = (bool)method.Invoke(null, [stream])!;

        Assert.Equal(expected, actual);
    }

    /// <summary>응답 본문의 이스케이프 형태.</summary>
    public enum BodyShape { SdkUnicodeEscape, BackslashQuote, ProtocolError, Success }
}
