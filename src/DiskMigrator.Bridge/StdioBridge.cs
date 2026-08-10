using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiskMigrator.App;

namespace DiskMigrator.Bridge;

/// <summary>
/// Claude 데스크톱 앱과 <b>떠 있는 우리 앱</b> 사이를 잇는 중계기.
/// </summary>
/// <remarks>
/// <b>왜 필요한가.</b> Claude 데스크톱 앱이 로컬 서버를 등록하는 길은 하나뿐입니다 —
/// 설정 파일에 <c>command</c>/<c>args</c>를 적어 <b>프로그램을 켜고 표준입출력으로</b>
/// 이야기하는 방식. 주소를 적는 커넥터 화면은 <c>https</c>를 요구해 로컬 <c>http</c>인
/// 우리 통로를 거부합니다(2026-08-10 실기 확인 — <c>URL은 'https'로 시작해야 합니다</c>).
///
/// <para>우리 MCP 서버는 <b>앱 안에서</b> 돕니다. 그래야 관리자 권한을 이미 가진 채로
/// 디스크를 읽고, 제안 카드가 실제 화면에 뜹니다. 이 구조는 포기할 수 없습니다 — 따로
/// 뜨는 서버는 앱의 화면도 권한도 갖지 못합니다.</para>
///
/// <para><b>그래서 중계합니다.</b> 이 모드로 켜진 프로세스는 창을 띄우지 않고, Claude가
/// 표준입력으로 보낸 JSON-RPC를 그대로 앱의 http 통로에 넘긴 뒤 답을 표준출력으로 돌려줍니다.
/// 토큰은 <see cref="McpTokenStore"/>에서 스스로 꺼내 씁니다 — 사용자는 토큰을 볼 일도,
/// 명령창을 열 일도 없습니다.</para>
///
/// <code>
/// Claude 데스크톱 ──표준입출력──▶ (이 모드) ──http + 토큰──▶ 떠 있는 앱(관리자 권한)
/// </code>
///
/// <para><b>토큰 보관은 그대로 통합니다.</b> DPAPI는 <i>사용자</i> 기준이고 권한 상승은
/// 사용자를 바꾸지 않으므로, 관리자로 뜬 앱이 저장한 것을 이 프로세스가 그대로 풉니다.</para>
///
/// <para><b>표준출력에는 JSON-RPC 말고 아무것도 쓰지 않습니다.</b> 한 줄이라도 섞이면
/// Claude 쪽 해석이 깨집니다. 진단은 전부 표준오류로 보냅니다 — Claude 데스크톱이 그것을
/// <c>%APPDATA%\Claude\logs\mcp-server-*.log</c>에 모아 두므로 나중에 볼 수 있습니다.</para>
/// </remarks>
public static class StdioBridge
{
    /// <summary>
    /// 앱이 아직 안 떴을 때 <c>initialize</c>가 기다려 주는 시간.
    /// </summary>
    /// <remarks>
    /// Claude 데스크톱은 <b>자기가 켜질 때</b> 등록된 서버를 함께 켭니다. 그 순간 우리 앱이
    /// 아직 안 떠 있으면 첫 악수부터 실패하고, 서버는 "연결 실패"로 남아 사용자가 Claude를
    /// 다시 켜야 합니다. 잠깐 기다려 주면 사용자가 그 사이에 앱을 켜는 것으로 해결됩니다.
    /// </remarks>
    private static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(30);

    /// <summary>악수 이후의 호출이 기다려 주는 시간 — 짧게. 이미 연결됐던 통로입니다.</summary>
    private static readonly TimeSpan CallWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 중계를 시작합니다. 표준입력이 닫힐 때(=Claude가 종료할 때) 돌아옵니다.
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        // BOM 없는 UTF-8. BOM이 붙으면 첫 줄이 통째로 해석되지 않습니다.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var stdin = new StreamReader(Console.OpenStandardInput(), encoding);
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };

        // 표준오류도 UTF-8로 고정합니다. 그러지 않으면 파이프로 나갈 때 콘솔 기본 코드
        // 페이지로 인코딩되어 한글이 깨집니다 — Claude 데스크톱이 이 내용을
        // mcp-server-*.log에 그대로 모아 두므로, 정작 문제가 났을 때 읽을 수 없게 됩니다.
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });

        return await RelayAsync(stdin, stdout, ct);
    }

    /// <summary>
    /// 실제 중계 — 들어오는 줄을 앱으로 넘기고 나오는 줄을 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// 표준입출력을 인자로 받습니다. 그래야 시험이 <b>진짜 서버를 띄워 놓고</b> 이 경로를
    /// 그대로 통과시켜 볼 수 있습니다 — 응답 형식(사건 스트림 포장, 대화 표시)을 눈으로만
    /// 맞추면 어긋난 것을 배포한 뒤에야 알게 됩니다. 이 제품에서 실제로 그런 일이 있었습니다.
    /// </remarks>
    internal static async Task<int> RelayAsync(TextReader stdin, TextWriter stdout, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var session = new BridgeSession();

        // 표준출력은 한 번에 한 줄씩만 나가야 합니다 — 여러 응답이 섞이면 줄이 깨집니다.
        using var writeLock = new SemaphoreSlim(1, 1);

        Console.Error.WriteLine($"[{AppIdentity.ProductName}] 중계기 시작 — 앱의 연결 통로로 넘깁니다.");

        var inFlight = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break;              // Claude가 닫았습니다.
            if (line.Length == 0) continue;

            // 도구 호출 하나가 몇 분씩 걸릴 수 있습니다(이미지 열기·부팅 진단). 그 동안
            // 뒤따르는 줄까지 막으면 대화가 멈춘 것처럼 보이므로 따로 보냅니다 — JSON-RPC는
            // 응답 순서를 요구하지 않고, 나가는 줄만 잠금으로 묶으면 섞이지 않습니다.
            //
            // ⚠ 그 밖의 줄은 <b>반드시 순서대로</b>입니다. 악수(initialize)의 응답에서
            //   대화 표시를 받아 다음 요청에 실어야 하고, 그 뒤의 알림도 도구 목록보다
            //   먼저 닿아야 합니다. 전부 동시에 보내면 그 순서가 뒤집혀, 서버가 아직 모르는
            //   대화로 온 요청을 거절합니다.
            if (IsToolCall(line))
            {
                inFlight.Add(HandleLineAsync(line, http, session, stdout, writeLock, ct));
                inFlight.RemoveAll(t => t.IsCompleted);
            }
            else
            {
                await HandleLineAsync(line, http, session, stdout, writeLock, ct);
            }
        }

        try { await Task.WhenAll(inFlight); } catch { /* 종료 중입니다. */ }

        Console.Error.WriteLine($"[{AppIdentity.ProductName}] 중계기 종료.");
        return 0;
    }

    /// <summary>한 줄(JSON-RPC 메시지 하나)을 앱으로 넘기고 답을 돌려줍니다.</summary>
    private static async Task HandleLineAsync(
        string line,
        HttpClient http,
        BridgeSession session,
        TextWriter stdout,
        SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        bool isInitialize = LooksLikeInitialize(line);
        var deadline = DateTime.UtcNow + (isInitialize ? HandshakeWait : CallWait);

        try
        {
            while (true)
            {
                var reached = await TryForwardAsync(line, http, session, stdout, writeLock, ct);
                if (reached) return;

                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(500, ct);
            }

            // 여기까지 왔으면 앱이 없거나 통로가 닫혀 있습니다. 조용히 죽으면 Claude는
            // 이유 없이 멈춘 것처럼 보이므로, 사람이 무엇을 해야 하는지 답으로 돌려줍니다.
            await WriteLineAsync(stdout, writeLock, ErrorFor(line,
                "DiskMigrator-X is not reachable. Start the DiskMigrator-X app (it needs administrator " +
                "rights) and press the button that opens the Claude connection, then try again."), ct);
        }
        catch (OperationCanceledException)
        {
            // 종료 중입니다.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{AppIdentity.ProductName}] 중계 실패: {ex.Message}");
            try
            {
                await WriteLineAsync(stdout, writeLock, ErrorFor(line, $"Bridge error: {ex.Message}"), ct);
            }
            catch { /* 표준출력마저 닫혔습니다. */ }
        }
    }

    /// <summary>
    /// 앱에 한 번 넘겨 봅니다. 통로에 <b>닿았으면</b> true(응답도 이미 내보냈습니다),
    /// 앱이 없어 닿지 못했으면 false — 부른 쪽이 다시 시도합니다.
    /// </summary>
    private static async Task<bool> TryForwardAsync(
        string line,
        HttpClient http,
        BridgeSession session,
        TextWriter stdout,
        SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        // 매번 다시 읽습니다 — 사용자가 방금 통로를 열었다면 포트·토큰이 그때 생깁니다.
        if (McpTokenStore.Load() is not { } saved) return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{saved.Port}/mcp")
        {
            Content = new StringContent(line, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // 악수에서 받은 표시들을 이후 요청에 그대로 실어야 같은 대화로 이어집니다.
        if (session.Id is { Length: > 0 } id) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);
        if (session.ProtocolVersion is { Length: > 0 } pv)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", pv);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return false;                          // 앱이 없거나 통로가 닫혔습니다.
        }

        using (response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                session.Id = ids.FirstOrDefault();

            // 알림에는 답이 없습니다(202). 아무것도 내보내지 않아야 합니다.
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted) return true;

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine(
                    $"[{AppIdentity.ProductName}] 앱이 {(int)response.StatusCode}로 거절했습니다.");

                // 401은 저장된 토큰이 앱의 것과 어긋난 경우입니다. 사람이 고칠 수 있게 말해 줍니다.
                string message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "DiskMigrator-X refused the saved key. Re-open the Claude connection in the app " +
                      "(close and open it once) and try again."
                    : $"DiskMigrator-X returned HTTP {(int)response.StatusCode}. {Trim(body)}";

                await WriteLineAsync(stdout, writeLock, ErrorFor(line, message), ct);
                return true;
            }

            bool isEventStream = string.Equals(
                response.Content.Headers.ContentType?.MediaType, "text/event-stream",
                StringComparison.OrdinalIgnoreCase);

            if (isEventStream)
                await RelayEventStreamAsync(response, session, stdout, writeLock, ct);
            else
                await RelayJsonAsync(response, session, stdout, writeLock, ct);

            return true;
        }
    }

    /// <summary>보통의 JSON 응답 하나를 한 줄로 내보냅니다.</summary>
    private static async Task RelayJsonAsync(
        HttpResponseMessage response, BridgeSession session,
        TextWriter stdout, SemaphoreSlim writeLock, CancellationToken ct)
    {
        string body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (body.Length == 0) return;

        session.NoteProtocolVersion(body);
        await WriteLineAsync(stdout, writeLock, Flatten(body), ct);
    }

    /// <summary>
    /// 사건 스트림(<c>text/event-stream</c>)에서 <c>data:</c> 줄만 골라 내보냅니다.
    /// </summary>
    /// <remarks>
    /// 우리 서버는 응답을 이 형식으로도 보냅니다. 표준입출력 쪽은 그런 포장을 모르므로
    /// 알맹이만 꺼내 한 줄씩 넘겨야 합니다.
    /// </remarks>
    private static async Task RelayEventStreamAsync(
        HttpResponseMessage response, BridgeSession session,
        TextWriter stdout, SemaphoreSlim writeLock, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
                continue;
            }

            // 빈 줄이 사건 하나의 끝입니다.
            if (line.Length == 0 && data.Length > 0)
            {
                string payload = data.ToString().Trim();
                data.Clear();
                if (payload.Length == 0) continue;

                session.NoteProtocolVersion(payload);
                await WriteLineAsync(stdout, writeLock, Flatten(payload), ct);
            }
        }

        if (data.Length > 0)
        {
            string payload = data.ToString().Trim();
            if (payload.Length > 0)
            {
                session.NoteProtocolVersion(payload);
                await WriteLineAsync(stdout, writeLock, Flatten(payload), ct);
            }
        }
    }

    /// <summary>표준출력에 한 줄. 여러 응답이 겹쳐 섞이지 않게 잠급니다.</summary>
    private static async Task WriteLineAsync(
        TextWriter stdout, SemaphoreSlim writeLock, string line, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await stdout.WriteLineAsync(line);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// 줄바꿈을 없앱니다 — 표준입출력에서 메시지 하나는 <b>반드시 한 줄</b>이어야 합니다.
    /// </summary>
    private static string Flatten(string json) =>
        json.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);

    /// <summary>요청 줄이 <c>initialize</c>인지 — 이것만 오래 기다려 줍니다.</summary>
    internal static bool LooksLikeInitialize(string line) => MethodOf(line) == "initialize";

    /// <summary>도구를 부르는 줄인지 — 이것만 따로 보내 오래 걸려도 뒤를 막지 않게 합니다.</summary>
    internal static bool IsToolCall(string line) => MethodOf(line) == "tools/call";

    /// <summary>줄이 어떤 방법을 부르는지. 형식을 모르면 빈 문자열.</summary>
    private static string MethodOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("method", out var m)
                ? m.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 그 요청에 대한 JSON-RPC 오류 한 줄을 만듭니다 — <b>같은 id로</b> 돌려줘야
    /// Claude가 무엇에 대한 답인지 알고 기다림을 멈춥니다.
    /// </summary>
    /// <remarks>
    /// id가 없는 알림에는 답하지 않는 것이 규약이지만, 답할 곳이 없으면 사용자는 아무런
    /// 설명도 못 받습니다. 그래서 id가 없으면 <c>null</c>로 보냅니다 — 규약상 허용됩니다.
    /// </remarks>
    internal static string ErrorFor(string requestLine, string message)
    {
        string id = "null";
        try
        {
            using var doc = JsonDocument.Parse(requestLine);
            if (doc.RootElement.TryGetProperty("id", out var element) &&
                element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                id = element.GetRawText();
            }
        }
        catch
        {
            // 형식을 모르는 줄입니다. id 없이 보냅니다.
        }

        // JsonSerializer 대신 JsonEncodedText — 트리밍이 걸고 넘어지지 않고, 하는 일도
        // 문자열 하나를 JSON 규칙으로 감싸는 것뿐입니다.
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id}," +
               $"\"error\":{{\"code\":-32000,\"message\":\"{JsonEncodedText.Encode(message)}\"}}}}";
    }

    private static string Trim(string s) =>
        s.Length <= 200 ? s.Replace('\n', ' ').Replace('\r', ' ') : s[..200].Replace('\n', ' ') + "…";

    /// <summary>악수에서 받은, 이후 요청에 함께 보내야 하는 표시들.</summary>
    private sealed class BridgeSession
    {
        public string? Id { get; set; }
        public string? ProtocolVersion { get; private set; }

        /// <summary>악수 응답에서 합의된 규약 버전을 집어 둡니다(있을 때만).</summary>
        public void NoteProtocolVersion(string json)
        {
            if (ProtocolVersion is not null) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("protocolVersion", out var v) &&
                    v.ValueKind == JsonValueKind.String)
                {
                    ProtocolVersion = v.GetString();
                }
            }
            catch
            {
                // 악수 응답이 아니었습니다.
            }
        }
    }
}
