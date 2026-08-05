using System.Net;
using System.Net.Http.Headers;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 엔드포인트가 실제로 뜨고, <b>토큰 없이는 아무것도 못 한다</b>는 것을 확인합니다 — 계획서 §7·§8.
/// </summary>
/// <remarks>
/// 127.0.0.1에만 묶더라도 같은 PC의 다른 프로세스는 접근할 수 있습니다.
/// 인증이 실제로 막는지는 코드를 읽어서가 아니라 요청을 보내서 확인해야 합니다.
/// </remarks>
public class McpHostTests
{
    /// <summary>
    /// 디스크를 건드리지 않는 가짜 서비스 — 호스트 자체만 시험합니다.
    /// </summary>
    /// <remarks>
    /// 열거 외의 메서드는 <b>부르면 터지게</b> 두었습니다. 진단 경로가 실수로 쓰기 통로에
    /// 손을 대면 테스트가 조용히 지나가는 대신 실패해야 합니다.
    /// </remarks>
    private sealed class FakeDiskService : IDiskService
    {
        public bool IsElevated => true;

        public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>([]);

        public IBlockDevice OpenRead(DiskInfo disk) => throw new InvalidOperationException(NotHere);

        public IBlockDevice OpenWriteExclusive(DiskInfo disk) => throw new InvalidOperationException(NotHere);

        public void RefreshDiskProperties(DiskInfo disk) => throw new InvalidOperationException(NotHere);

        public Task<SafeRemoveResult> SafeRemoveAsync(DiskInfo disk, CancellationToken ct = default) =>
            throw new InvalidOperationException(NotHere);

        private const string NotHere = "진단 경로에서 불려서는 안 되는 메서드입니다.";
    }
    /// <summary>앱이 없는 환경에서 호스트만 시험하기 위한 가짜 상태.</summary>
    private sealed class IdleAppState : IAppState
    {
        public bool IsBusy => false;
        public OperationProgress GetProgress() => new(false, null, 0, null, null, null, null);
        public void RequestCancel() => throw new InvalidOperationException("이 시험에서는 불려서는 안 됩니다.");
    }

    /// <summary>테스트마다 새 호스트 — 제안 저장소도 함께 만듭니다.</summary>
    private static McpHost NewHost() =>
        new(new FakeDiskService(), new Proposals.ProposalStore(), new IdleAppState());


    [Fact]
    public async Task 시작하면_루프백_주소와_토큰을_돌려준다()
    {
        await using var host = NewHost();

        var status = await host.StartAsync();

        Assert.True(status.Running);
        Assert.StartsWith("http://127.0.0.1:", status.Url);
        Assert.EndsWith("/mcp", status.Url);
        Assert.False(string.IsNullOrWhiteSpace(status.Token));

        await host.StopAsync();
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task 토큰_없는_요청은_401()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var http = new HttpClient();
        var resp = await http.PostAsync(status.Url, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 틀린_토큰도_401()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var resp = await http.PostAsync(status.Url, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 맞는_토큰이면_401이_아니다()
    {
        await using var host = NewHost();
        var status = await host.StartAsync();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", status.Token);
        var resp = await http.PostAsync(status.Url, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // MCP 규약을 지키지 않은 본문이라 400대가 나올 수 있지만, 인증에서 막히지는 않아야 합니다.
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 다시_켜면_토큰이_바뀐다()
    {
        await using var host = NewHost();

        string? first = (await host.StartAsync()).Token;
        await host.StopAsync();
        string? second = (await host.StartAsync()).Token;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task 멈춘_뒤에는_상태가_비어_있다()
    {
        await using var host = NewHost();
        await host.StartAsync();
        await host.StopAsync();

        var status = host.Status;
        Assert.False(status.Running);
        Assert.Null(status.Url);
        Assert.Null(status.Token);
    }
}
