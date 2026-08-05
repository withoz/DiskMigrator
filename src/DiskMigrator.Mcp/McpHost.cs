using System.Net;
using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Mcp;

/// <summary>MCP 엔드포인트가 떠 있는 상태.</summary>
/// <param name="Running">지금 듣고 있는지.</param>
/// <param name="Url">Claude 설정에 넣을 주소. 꺼져 있으면 null.</param>
/// <param name="Token">접근 토큰. 꺼져 있으면 null.</param>
public sealed record McpHostStatus(bool Running, string? Url, string? Token);

/// <summary>
/// 앱 안에서 도는 로컬 MCP 서버. 계획서 §5.1의 "앱이 MCP 서버를 품는다".
/// </summary>
/// <remarks>
/// 앱이 이미 관리자 권한으로 뜨므로, 여기서 도구를 부르면 UAC 없이 디스크를 읽을 수 있습니다.
/// 독립 서버를 두고 작업마다 권한을 올리는 방식은 대화가 UAC 팝업으로 끊깁니다(2026-08-04 조사에서 실증).
///
/// <para><b>기본은 꺼짐</b>입니다. 사용자가 앱 설정에서 명시적으로 켜야 시작합니다 — 디스크를 읽는
/// 통로를 사용자 모르게 열어두지 않습니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class McpHost(
    IDiskService diskService,
    Proposals.ProposalStore proposals,
    IAppState appState,
    ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private readonly ILogger _logger =
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<McpHost>();

    private WebApplication? _app;
    private AccessToken? _token;
    private int _port;

    /// <summary>처음 시도할 포트. 쓰이고 있으면 하나씩 올려가며 찾습니다.</summary>
    private const int PreferredPort = 47821;
    private const int MaxPortAttempts = 20;

    public bool IsRunning => _app is not null;

    public McpHostStatus Status => _app is null
        ? new(false, null, null)
        : new(true, $"http://127.0.0.1:{_port}/mcp", _token?.Value);

    /// <summary>
    /// 엔드포인트를 시작합니다. 이미 떠 있으면 현재 상태를 그대로 돌려줍니다.
    /// </summary>
    /// <param name="includeSensitive">
    /// 시리얼·볼륨 레이블을 가리지 않고 보낼지. 기본은 가립니다 — 진단 결과가 대화 로그에 남습니다.
    /// </param>
    /// <param name="reuse">
    /// 지난번에 쓰던 토큰·포트. 주면 그대로 씁니다 — 사용자가 Claude 설정에 넣어 둔 값이
    /// 그대로 통해야 재시작할 때마다 다시 붙여 넣지 않습니다. null이면 새로 발급합니다.
    /// </param>
    public async Task<McpHostStatus> StartAsync(
        bool includeSensitive = false,
        McpReuse? reuse = null,
        CancellationToken ct = default)
    {
        if (_app is not null) return Status;

        _token = reuse is null ? AccessToken.Create() : AccessToken.FromStored(reuse.Token);

        var builder = WebApplication.CreateSlimBuilder();

        // 앱의 로깅으로 넘깁니다 — 별도 콘솔이 없으므로 파일 로그가 유일한 증거입니다.
        builder.Logging.ClearProviders();
        if (loggerFactory is not null) builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        // 진단 도구가 쓰는 것들. IDiskReader만 등록해 쓰기 서비스는 컨테이너에도 넣지 않습니다.
        builder.Services.AddSingleton<IDiskReader>(new DiskServiceReader(diskService));
        builder.Services.AddSingleton(new Mapping(includeSensitive));

        // ImageInspector는 IDiskService를 받지만 EnumerateDisksAsync만 쓰고 공개 메서드도
        // InspectAsync 하나뿐이라 읽기 전용입니다. 완성된 객체로 넣어, 도구가 그 안의
        // 서비스에 손대지 못하게 합니다 — 컨테이너에 IDiskService 자체는 등록하지 않습니다.
        builder.Services.AddSingleton(new Windows.Jobs.ImageInspector(
            diskService,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Windows.Jobs.ImageInspector>()));

        builder.Services.AddSingleton(new Windows.Jobs.DiagnosticCollector(

            diskService,

            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Windows.Jobs.DiagnosticCollector>()));

        

        builder.Services.AddSingleton<ReadOnlyTools>();
                // 계획 도구용 — CloneSessionFactory에는 실제 클론 세션을 만드는 CreateAsync도 있으므로
        // PreviewAsync만 보이는 어댑터로 감쌉니다.
        builder.Services.AddSingleton<IClonePlanner>(new CloneSessionPlanner(
            new Windows.Jobs.CloneSessionFactory(
                diskService,
                new Windows.Snapshots.VssSnapshotProvider(
                    (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Windows.Snapshots.VssSnapshotProvider>()),
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Windows.Jobs.CloneSessionFactory>())));

        builder.Services.AddSingleton<PlanningTools>();

        // 3단계 — 제안. 저장소와 앱 상태는 앱이 넘겨준 것을 그대로 씁니다(도구가 만들지 않습니다).
        builder.Services.AddSingleton(proposals);
        builder.Services.AddSingleton(appState);
        builder.Services.AddSingleton<ProposalTools>();

        builder.Services
            .AddMcpServer(o =>
            {
                // 제품명은 DiskMigrator-X입니다 — 수동 버전(DiskMigrator v1.x)과 구분됩니다.
                // Claude가 도구 목록에서 어느 앱과 이야기하는지 알아볼 이름이기도 합니다.
                o.ServerInfo = new() { Name = "DiskMigrator-X", Version = ThisVersion };
            })
            .WithHttpTransport()
            .WithTools<ReadOnlyTools>()
            .WithTools<PlanningTools>()
            .WithTools<ProposalTools>();

        // 외부 인터페이스에 열지 않습니다. IPAddress.Loopback에 직접 묶습니다 —
        // "localhost"나 "*" 같은 문자열은 환경에 따라 모든 인터페이스에 붙을 수 있습니다.
        // 지난번 포트를 먼저 시도합니다 — 주소가 그대로여야 Claude 설정을 다시 고치지 않습니다.
        // 그 포트가 이미 쓰이고 있으면 평소대로 빈 포트를 찾습니다.
        _port = FindFreePort(reuse?.Port);
        builder.Services.Configure<KestrelServerOptions>(k => k.Listen(IPAddress.Loopback, _port));

        var app = builder.Build();

        // 토큰 검사 — MCP 경로에 닿기 전에 막습니다.
        app.Use(async (ctx, next) =>
        {
            string? presented = ExtractBearer(ctx.Request.Headers.Authorization);
            if (_token is null || !_token.Matches(presented))
            {
                _logger.LogWarning("MCP 인증 실패: {Path} ({Remote})",
                    ctx.Request.Path, ctx.Connection.RemoteIpAddress);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Invalid or missing token.");
                return;
            }
            await next();
        });

        app.MapMcp("/mcp");

        await app.StartAsync(ct);
        _app = app;

        _logger.LogInformation("MCP 엔드포인트 시작: http://127.0.0.1:{Port}/mcp (토큰 {Preview})",
            _port, _token.Preview);
        return Status;
    }

    /// <summary>엔드포인트를 멈춥니다. 토큰도 버립니다 — 다시 켜면 새로 발급됩니다.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var app = _app;
        if (app is null) return;

        _app = null;
        _token = null;

        try
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
            _logger.LogInformation("MCP 엔드포인트 중지.");
        }
        catch (Exception ex)
        {
            // 멈추다 나는 오류가 앱을 흔들면 안 됩니다.
            _logger.LogWarning(ex, "MCP 엔드포인트 중지 중 오류.");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// <c>initialize</c> 응답으로 Claude에게 알리는 버전 — <b>앱</b>의 버전입니다.
    /// </summary>
    /// <remarks>
    /// 이 어셈블리의 버전을 쓰면 앱을 올려도 값이 그대로라, Claude가 보는 버전과 사용자가
    /// 화면에서 보는 버전이 어긋납니다. 문제를 신고받았을 때 어느 빌드인지 특정하지 못하게 됩니다.
    /// (진단 리포트의 <c>AppVersion</c>도 같은 이유로 진입점 어셈블리를 봅니다.)
    /// </remarks>
    private static string ThisVersion
    {
        get
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(McpHost).Assembly;
            return asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
        }
    }

    private static string? ExtractBearer(string? header) =>
        header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

    /// <summary>
    /// 비어 있는 포트를 찾습니다. 전부 막혀 있으면 예외를 던집니다.
    /// </summary>
    /// <param name="preferFirst">
    /// 먼저 시도할 포트(지난번에 쓰던 것). 비어 있으면 그대로 씁니다 — 주소가 유지되어야
    /// 사용자가 Claude 설정을 다시 고치지 않습니다.
    /// </param>
    private static int FindFreePort(int? preferFirst = null)
    {
        // 통로를 닫고 곧바로 다시 열면 방금 쓰던 포트가 아직 풀리지 않았을 수 있습니다.
        // 그때 다른 번호로 옮겨 가면 주소가 바뀌어 사용자가 Claude 설정을 다시 고쳐야 합니다.
        // 잠깐 기다렸다 같은 포트를 다시 시도합니다 — 진짜 다른 앱이 쓰고 있으면 아래로 내려갑니다.
        if (preferFirst is { } first)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (IsFree(first)) return first;
                Thread.Sleep(200);
            }
        }

        for (int p = PreferredPort; p < PreferredPort + MaxPortAttempts; p++)
        {
            if (IsFree(p)) return p;
        }
        throw new InvalidOperationException(
            $"사용할 수 있는 포트를 찾지 못했습니다 ({PreferredPort}~{PreferredPort + MaxPortAttempts - 1}).");
    }

    /// <summary>그 포트에 지금 묶을 수 있는지.</summary>
    private static bool IsFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>Kestrel의 로그를 앱 로거로 넘깁니다.</summary>
    private sealed class ForwardingLoggerProvider(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);
        public void Dispose() { }
    }
}
