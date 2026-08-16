using System.Net;
using System.Reflection;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 주소만으로 연결하는 길 — Claude 앱의 커넥터 화면에는 토큰 칸이 없습니다.
/// </summary>
/// <remarks>
/// 헤더가 정석이지만 그 화면은 주소와 OAuth 항목만 받습니다. 우리는 OAuth를 쓰지 않으므로,
/// 화면으로 연결하려는 사용자는 인증에 막혀 아무것도 못 합니다 — 명령을 칠 줄 아는 사람만
/// 쓸 수 있게 되는 셈입니다.
///
/// <para>대신 <b>토큰이 주소에 실리면 로그에 남습니다.</b> 사용자가 문제 신고에 로그를
/// 첨부하는 순간 열쇠가 함께 나갑니다. 그래서 가림이 함께 있어야 이 기능이 성립합니다.</para>
/// </remarks>
public class ConnectorUrlTests
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

    private static McpHost NewHost() =>
        new(new FakeDiskService(), new Proposals.ProposalStore(), new IdleAppState());

    // --- 주소에 실린 토큰 --------------------------------------------------

    [Fact]
    public async Task 커넥터_주소에는_토큰이_실려_있다()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        Assert.NotNull(status.ConnectorUrl);
        Assert.Contains($"key={status.Token}", status.ConnectorUrl);

        // 헤더용 주소에는 들어 있으면 안 됩니다 — 화면에서 둘을 구분해 보여주기 때문입니다.
        Assert.DoesNotContain("key=", status.Url!);
    }

    [Fact]
    public async Task 주소에_실린_토큰으로_통과한다()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var client = new HttpClient();
        var res = await client.PostAsync(status.ConnectorUrl, new StringContent("{}"));

        // 인증은 통과해야 합니다(그 뒤 MCP 규약 오류는 여기서 볼 일이 아닙니다).
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task 주소에_틀린_토큰이_실리면_막힌다()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var client = new HttpClient();
        var res = await client.PostAsync($"{status.Url}?key=wrong-token", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task 토큰이_아예_없으면_막힌다()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var client = new HttpClient();
        var res = await client.PostAsync(status.Url, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // --- 로그 가림 ----------------------------------------------------------

    /// <summary>
    /// 로그로 나가는 문장에서 토큰이 지워져야 합니다.
    /// </summary>
    /// <remarks>
    /// ASP.NET은 요청 URL을 그대로 로그에 남깁니다. 주소에 토큰이 실리면 그 파일 하나로
    /// 열쇠가 새어 나갑니다 — 사용자가 문제 신고에 첨부하는 바로 그 파일입니다.
    /// </remarks>
    [Fact]
    public void 로그에서_토큰을_가린다()
    {
        const string Secret = "SECRET-TOKEN-VALUE";

        var providerType = typeof(McpHost).GetNestedType(
            "RedactingLoggerProvider", BindingFlags.NonPublic)!;

        var captured = new List<string>();
        var factory = new CapturingLoggerFactory(captured);

        using var provider = (IDisposable)Activator.CreateInstance(
            providerType, factory, new Func<string?>(() => Secret))!;

        var logger = ((Microsoft.Extensions.Logging.ILoggerProvider)provider).CreateLogger("test");
        logger.LogInformation("Request starting POST http://127.0.0.1:47821/mcp?key={Key} - 200", Secret);

        string line = Assert.Single(captured);
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.Contains("…", line, StringComparison.Ordinal);
    }

    private sealed class CapturingLoggerFactory(List<string> sink) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capturing(sink);
        public void Dispose() { }

        private sealed class Capturing(List<string> sink) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
                TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
                sink.Add(formatter(state, ex));
        }
    }
}
