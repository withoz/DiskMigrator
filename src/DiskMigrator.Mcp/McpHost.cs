using System.Net;
using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    ILoggerFactory? loggerFactory = null,
    McpActivityLog? activityLog = null) : IAsyncDisposable
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
        _port = await FindFreePortAsync(reuse?.Port, ct);
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

        // 사용자가 화면에서 "무엇을 읽었는지" 볼 수 있게 호출을 기록합니다.
        // 도구 호출은 전부 이 한 곳을 지나므로 도구마다 손볼 필요가 없습니다.
        if (activityLog is not null) app.Use(RecordActivityAsync);

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
    /// <remarks>
    /// ⚠ <b>기다림은 반드시 비동기여야 합니다.</b> 이 메서드는 <see cref="StartAsync"/>의 첫
    /// <c>await</c>보다 앞에 있어, 부른 스레드에서 그대로 돕니다 — 앱에서는 UI 스레드입니다.
    /// <c>Thread.Sleep</c>을 쓰면 통로를 닫고 바로 다시 열 때 화면이 2초 얼어붙습니다.
    /// </remarks>
    private static async Task<int> FindFreePortAsync(int? preferFirst, CancellationToken ct)
    {
        // 통로를 닫고 곧바로 다시 열면 방금 쓰던 포트가 아직 풀리지 않았을 수 있습니다.
        // 그때 다른 번호로 옮겨 가면 주소가 바뀌어 사용자가 Claude 설정을 다시 고쳐야 합니다.
        // 잠깐 기다렸다 같은 포트를 다시 시도합니다 — 진짜 다른 앱이 쓰고 있으면 아래로 내려갑니다.
        if (preferFirst is { } first)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (IsFree(first)) return first;
                await Task.Delay(200, ct);
            }
        }

        for (int p = PreferredPort; p < PreferredPort + MaxPortAttempts; p++)
        {
            if (IsFree(p)) return p;
        }
        throw new InvalidOperationException(
            $"사용할 수 있는 포트를 찾지 못했습니다 ({PreferredPort}~{PreferredPort + MaxPortAttempts - 1}).");
    }

    /// <summary>
    /// 요청 본문에서 어떤 도구를 불렀는지 읽어 활동 기록에 남깁니다.
    /// </summary>
    /// <remarks>
    /// <b>기록이 호출을 방해해서는 안 됩니다.</b> 본문을 못 읽거나 형식이 달라도 그대로
    /// 통과시킵니다 — 화면에 한 줄 덜 남는 것보다 진단이 실패하는 쪽이 나쁩니다.
    ///
    /// <para>본문을 읽은 뒤 위치를 처음으로 되돌립니다. 그러지 않으면 MCP 처리기가 빈 본문을
    /// 보게 되어 모든 호출이 깨집니다.</para>
    /// </remarks>
    private async Task RecordActivityAsync(HttpContext ctx, Func<Task> next)
    {
        string method = "";
        string tool = "";
        string detail = "";

        try
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(
                ctx.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            string body = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;

            if (body.Length > 0)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                if (method == "tools/call" && root.TryGetProperty("params", out var ps))
                {
                    tool = ps.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    detail = SummarizeArguments(ps);
                }
            }
        }
        catch
        {
            // 형식이 다르거나 읽지 못했습니다. 기록만 건너뜁니다.
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool failed;

        // 도구 호출은 거절되어도 HTTP 200으로 옵니다 — 오류는 본문 안에 있습니다.
        // 상태 코드만 보면 "차단됨"과 "정상 처리"가 화면에서 구분되지 않아, 감사 목록의
        // 뜻이 절반이 됩니다. 그래서 도구 호출에 한해 응답 본문까지 들여다봅니다.
        //
        // 본문을 가로채는 것은 tools/call 요청에만 합니다. MCP는 서버가 먼저 보내는
        // 스트림도 쓰는데, 그런 응답까지 모아 두면 전달이 늦어집니다.
        if (tool.Length > 0 && HttpMethods.IsPost(ctx.Request.Method))
        {
            // ⚠ Response.Body만 바꾸면 잡히지 않습니다. SSE 응답은 BodyWriter(PipeWriter)로
            //   나가므로 스트림 교체를 그냥 지나갑니다 — 실기에서 거절이 계속 정상으로
            //   보였던 이유입니다. 본문 기능(IHttpResponseBodyFeature) 자체를 갈아 끼웁니다.
            var originalFeature = ctx.Features.Get<IHttpResponseBodyFeature>();
            using var buffer = new MemoryStream();
            ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
            try
            {
                await next();
                buffer.Position = 0;
                failed = LooksLikeError(buffer) || ctx.Response.StatusCode >= 400;
            }
            finally
            {
                if (originalFeature is not null) ctx.Features.Set(originalFeature);
                buffer.Position = 0;
                await buffer.CopyToAsync(ctx.Response.Body);
            }
        }
        else
        {
            await next();
            failed = ctx.Response.StatusCode >= 400;
        }

        long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // 알림(notifications/*)은 응답이 없고 사용자에게 의미도 없어 기록하지 않습니다.
        string name = tool.Length > 0 ? tool : method;
        if (name.Length == 0 || name.StartsWith("notifications/", StringComparison.Ordinal)) return;

        activityLog!.Record(new McpActivity(
            DateTime.Now, name, detail, McpActivityLog.Classify(name), failed, ms));
    }

    /// <summary>
    /// 응답 본문이 오류를 담고 있는지 — 우리 도구는 거절도 HTTP 200으로 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// 우리 도구는 <c>ToolResult</c>를 JSON <b>문자열로</b> 감싸 돌려주므로, 본문 안의
    /// <c>ok</c> 필드는 한 번 더 이스케이프된 채로 들어 있습니다.
    ///
    /// <para>⚠ 그 이스케이프가 <c>\"</c>가 아니라 <b><c>"</c></b>입니다 — SDK의 직렬화
    /// 설정 때문입니다. 실기에서 <c>\"ok\":false</c>로 찾다가 아무것도 못 잡았습니다.
    /// 그래서 두 형태를 모두 따옴표로 되돌린 뒤 봅니다.</para>
    ///
    /// <para>판단이 애매하면 <b>실패로 표시하지 않습니다</b> — 멀쩡한 호출을 붉게 칠하면
    /// 목록 자체를 믿지 못하게 됩니다.</para>
    /// </remarks>
    private static bool LooksLikeError(Stream body)
    {
        try
        {
            using var reader = new StreamReader(body, System.Text.Encoding.UTF8, leaveOpen: true);
            string text = reader.ReadToEnd()
                .Replace("\\u0022", "\"", StringComparison.OrdinalIgnoreCase)
                .Replace("\\\"", "\"", StringComparison.Ordinal);

            return text.Contains("\"ok\":false", StringComparison.Ordinal) ||
                   text.Contains("\"isError\":true", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 인자에서 사용자가 알아볼 만한 것만 뽑습니다 — 디스크 번호와 파일 이름.
    /// </summary>
    /// <remarks>
    /// 전부 보여주면 이유(reason) 문장이 길게 흘러 화면이 읽히지 않습니다. 경로는 파일명만
    /// 남깁니다 — 어느 파일인지 알면 충분하고, 전체 경로는 사용자 폴더 이름까지 드러냅니다.
    /// </remarks>
    private static string SummarizeArguments(System.Text.Json.JsonElement ps)
    {
        if (!ps.TryGetProperty("arguments", out var a) ||
            a.ValueKind != System.Text.Json.JsonValueKind.Object) return "";

        var parts = new List<string>();
        foreach (var p in a.EnumerateObject())
        {
            if (p.Name.Contains("DeviceNumber", StringComparison.OrdinalIgnoreCase) &&
                p.Value.TryGetInt32(out int n))
            {
                parts.Add($"디스크 {n}");
            }
            else if (p.Name.Contains("path", StringComparison.OrdinalIgnoreCase) &&
                     p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string? s = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(Path.GetFileName(s));
            }
        }
        return string.Join(" · ", parts);
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
