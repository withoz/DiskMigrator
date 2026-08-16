using DiskMigrator.App;
using DiskMigrator.Bridge;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// Claude 데스크톱 앱과 이어 주는 중계기 — <b>진짜 서버에 붙여</b> 확인합니다.
/// </summary>
/// <remarks>
/// 이 중계기는 Claude 데스크톱 앱이 우리와 이야기할 수 있는 <b>유일한</b> 길입니다.
/// 그 앱은 주소를 적는 커넥터 화면에서 <c>https</c>를 요구해 로컬 <c>http</c>인 우리 통로를
/// 거부하고(2026-08-10 실기 확인), 남은 길은 프로그램을 켜서 표준입출력으로 이야기하는
/// 방식뿐이기 때문입니다.
///
/// <para><b>왜 형식을 눈으로 맞추면 안 되는가.</b> 이 앱에서 이미 두 번 그렇게 잃었습니다 —
/// 응답이 사건 스트림(<c>text/event-stream</c>)으로 나가는 것을 모른 채 스트림만 갈아 끼워
/// 활동 기록이 조용히 비었고, 도구 결과의 이스케이프가 예상과 달라 거절을 못 잡았습니다.
/// "안 깨졌다"는 "동작한다"가 아닙니다. 그래서 여기서는 실제 <see cref="McpHost"/>를 띄우고
/// 중계기를 그 앞에 세워, 나오는 줄을 그대로 봅니다.</para>
/// </remarks>
[Collection(HostTestCollection.Name)]
public class StdioBridgeTests
{
    private sealed class FakeDiskService : IDiskService
    {
        public bool IsElevated => true;
        public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>([]);
        public IBlockDevice OpenRead(DiskInfo disk) => throw new InvalidOperationException();
        public IBlockDevice OpenWriteExclusive(DiskInfo disk) => throw new InvalidOperationException();
        public void RefreshDiskProperties(DiskInfo disk) => throw new InvalidOperationException();
        public Task<SafeRemoveResult> SafeRemoveAsync(DiskInfo disk, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class IdleAppState : IAppState
    {
        public bool IsBusy => false;
        public bool UseSnapshot => true;
        public OperationProgress GetProgress() => new(false, null, 0, null, null, null, null);
        public void RequestCancel() { }
    }

    /// <summary>악수 → 알림 → 도구 목록. 실제 클라이언트가 보내는 순서 그대로입니다.</summary>
    private const string Handshake =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"bridge-test","version":"1.0"}}}""" + "\n" +
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n" +
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n";

    /// <summary>
    /// 시험은 <b>사용자와 같은 자리</b>의 연결 정보를 씁니다(중계기가 거기서 읽으므로).
    /// 그래서 원래 값을 반드시 되돌립니다 — 시험이 사용자 설정을 바꿔 놓으면 안 됩니다.
    /// </summary>
    private static async Task WithSavedConnection(string token, int port, Func<Task> body)
    {
        var original = McpTokenStore.Load();
        try
        {
            McpTokenStore.Save(token, port);
            await body();
        }
        finally
        {
            if (original is null) McpTokenStore.Clear();
            else McpTokenStore.Save(original.Token, original.Port);
        }
    }

    private static McpHost NewHost() =>
        new(new FakeDiskService(), new Proposals.ProposalStore(), new IdleAppState());

    [Fact]
    public async Task 중계기는_진짜_서버와_악수하고_도구_목록까지_받아_온다()
    {
        var host = NewHost();
        var status = await host.StartAsync();
        int port = new Uri(status.Url!).Port;

        try
        {
            await WithSavedConnection(status.Token!, port, async () =>
            {
                var output = new StringWriter();
                await StdioBridge.RelayAsync(new StringReader(Handshake), output);

                string text = output.ToString();

                // 서버가 자기를 밝혔는지 — 악수가 실제로 성사됐다는 뜻입니다.
                Assert.Contains("DiskMigrator-X", text);

                // 도구 목록이 왔는지. 이름 하나라도 없으면 Claude 화면에 아무 도구도 안 뜹니다.
                Assert.Contains("list_disks", text);

                // 응답은 요청과 같은 번호로 와야 합니다.
                Assert.Contains("\"id\":1", text);
                Assert.Contains("\"id\":2", text);
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task 나가는_줄은_모두_한_줄짜리_JSON이다()
    {
        var host = NewHost();
        var status = await host.StartAsync();
        int port = new Uri(status.Url!).Port;

        try
        {
            await WithSavedConnection(status.Token!, port, async () =>
            {
                var output = new StringWriter();
                await StdioBridge.RelayAsync(new StringReader(Handshake), output);

                string[] lines = output.ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim('\r'))
                    .Where(l => l.Length > 0)
                    .ToArray();

                Assert.NotEmpty(lines);

                // 표준입출력에서는 메시지 하나가 반드시 한 줄입니다. 줄바꿈이 섞여 나가면
                // 받는 쪽이 반 토막 난 JSON을 읽고 연결 전체가 깨집니다.
                foreach (string line in lines)
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(line);
                    Assert.Equal("2.0", parsed.RootElement.GetProperty("jsonrpc").GetString());
                }
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task 알림에는_답하지_않는다()
    {
        var host = NewHost();
        var status = await host.StartAsync();
        int port = new Uri(status.Url!).Port;

        try
        {
            await WithSavedConnection(status.Token!, port, async () =>
            {
                var output = new StringWriter();
                await StdioBridge.RelayAsync(new StringReader(Handshake), output);

                // 악수 1 + 도구 목록 1 = 두 줄. 알림에 답을 지어 보내면 받는 쪽이
                // "부른 적 없는 답"을 받아 규약 위반으로 끊습니다.
                int count = output.ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => l.Trim().Length > 0);

                Assert.Equal(2, count);
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task 앱이_없으면_사람이_할_일을_담은_오류로_답한다()
    {
        // 통로를 열지 않았습니다 — 저장된 포트에는 아무도 없습니다.
        await WithSavedConnection("not-a-real-token", 47999, async () =>
        {
            var output = new StringWriter();

            // 악수는 오래 기다리므로, 기다리지 않는 보통 요청으로 확인합니다.
            await StdioBridge.RelayAsync(
                new StringReader("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""" + "\n"),
                output);

            string text = output.ToString();

            Assert.Contains("\"id\":7", text);
            Assert.Contains("\"error\"", text);

            // 조용히 실패하면 사용자는 Claude가 고장 난 줄 압니다. 무엇을 해야 하는지
            // 답 안에 있어야 합니다.
            Assert.Contains("DiskMigrator-X", text);
        });
    }

    [Fact]
    public void 도구_호출만_따로_보낸다()
    {
        // 악수와 알림은 순서가 뒤집히면 안 되므로 줄줄이 처리합니다.
        Assert.False(StdioBridge.IsToolCall("""{"jsonrpc":"2.0","id":1,"method":"initialize"}"""));
        Assert.False(StdioBridge.IsToolCall("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.False(StdioBridge.IsToolCall("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        // 오래 걸릴 수 있는 것은 이것뿐입니다.
        Assert.True(StdioBridge.IsToolCall("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_disks"}}"""));

        // 형식을 모르는 줄에 겁먹고 따로 보내지 않습니다.
        Assert.False(StdioBridge.IsToolCall("이건 JSON이 아닙니다"));
    }

    [Fact]
    public void 오류에는_요청과_같은_번호가_실린다()
    {
        // 번호가 다르면 부른 쪽은 답을 못 받은 채 계속 기다립니다.
        Assert.Contains("\"id\":42",
            StdioBridge.ErrorFor("""{"jsonrpc":"2.0","id":42,"method":"tools/list"}""", "무엇이든"));

        Assert.Contains("\"id\":\"abc\"",
            StdioBridge.ErrorFor("""{"jsonrpc":"2.0","id":"abc","method":"tools/list"}""", "무엇이든"));

        // 번호가 없으면 null — 규약이 허용합니다.
        Assert.Contains("\"id\":null",
            StdioBridge.ErrorFor("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", "무엇이든"));
    }
}
