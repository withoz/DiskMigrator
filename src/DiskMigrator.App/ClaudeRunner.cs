using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DiskMigrator.App;

/// <summary>Claude에게 한 번 물어본 결과.</summary>
/// <param name="Ok">답을 받았는지.</param>
/// <param name="Text">사용자에게 보여 줄 답. 실패하면 빈 문자열.</param>
/// <param name="SessionId">
/// 이 대화의 표시. <b>다음 질문에 되넘겨야</b> "그럼 그건 왜 그래?"가 통합니다 — 없으면
/// 매번 처음 만난 사람처럼 답해, 방금 읽은 디스크를 또 읽습니다.
/// </param>
/// <param name="Error">실패 사유(원문). 우리가 번역할 수 없는 남의 문구입니다.</param>
public sealed record ClaudeAnswer(bool Ok, string Text, string? SessionId = null, string? Error = null);

/// <summary>Claude가 일하는 동안 화면으로 흘려보내는 것.</summary>
/// <remarks>
/// 예전에는 도구 이름만 알렸습니다. 그래서 답이 다 만들어질 때까지 화면은 "살펴보는 중…"
/// 하나로 멈춰 있었고, <b>56초를 기다렸다가 글 뭉치를 한꺼번에</b> 받았습니다.
/// Claude는 글자가 생기는 대로 흘려보냅니다 — 우리도 그 신호를 이미 받고 있었으면서
/// 버리고 있었습니다.
/// </remarks>
public abstract record ClaudeEvent
{
    /// <summary>지금 어떤 도구를 부르는 중인지.</summary>
    public sealed record Tool(string Name) : ClaudeEvent;

    /// <summary>답의 조각. 오는 대로 화면에 붙입니다.</summary>
    public sealed record Text(string Chunk) : ClaudeEvent;
}

/// <summary>
/// 앱 안에서 <b>Claude에게 직접 물어봅니다</b> — 사용자가 창을 옮겨 다니지 않아도 되게.
/// </summary>
/// <remarks>
/// <b>왜 우리가 모델을 직접 부르지 않는가.</b> 그러려면 사용자가 API 열쇠를 발급받아
/// 결제 수단을 등록해야 하고, 질문마다 따로 요금이 나갑니다 — 지금 고치려는 "너무 복잡하다"를
/// 더 나쁘게 만듭니다. 게다가 대화 흐름·도구 반복·스트리밍·기록 관리를 우리가 새로 만들어
/// 계속 따라가야 합니다.
///
/// <para><b>그래서 Claude Code를 뒤에서 부릅니다.</b> 사용자가 <i>이미 쓰고 있는 구독</i>으로
/// 동작하고, 대화 흐름은 그쪽이 이미 갖고 있습니다. 우리는 질문을 넘기고 나오는 줄을 화면에
/// 옮기기만 합니다.</para>
///
/// <para><b>우리 도구만 쓰게 묶습니다.</b> <c>--mcp-config</c>로 우리 중계기만 붙이고,
/// <c>--allowedTools</c>와 <c>--permission-mode dontAsk</c>로 그 밖의 것은 허락 없이는
/// 못 하게 합니다. 사용자의 파일을 뒤지라고 부른 것이 아닙니다.</para>
///
/// <para><b>등록(<see cref="ClaudeRegistration"/>)과는 별개입니다.</b> 여기서는 중계기를
/// 그 자리에서 인자로 넘기므로, Claude 설정에 아무것도 등록돼 있지 않아도 동작합니다.
/// 등록은 사용자가 <i>Claude 쪽 창에서</i> 대화하고 싶을 때를 위한 것입니다.</para>
/// </remarks>
public static class ClaudeRunner
{
    /// <summary>이 컴퓨터에서 Claude Code를 찾을 수 있는지.</summary>
    public static bool IsAvailable => ClaudeRegistration.FindOnPath("claude") is not null;

    /// <summary>
    /// 한 번 물어보고 답을 받아옵니다.
    /// </summary>
    /// <param name="question">사용자 말로 된 질문.</param>
    /// <param name="bridgePath">중계기 실행 파일 — 이것을 통해 우리 도구에 닿습니다.</param>
    /// <param name="korean">답을 한국어로 받을지.</param>
    /// <param name="resume">
    /// 이어 갈 대화의 표시(<see cref="ClaudeAnswer.SessionId"/>). null이면 새 대화입니다.
    /// </param>
    /// <param name="progress">지금 무엇을 하고 있는지 — 화면에 그대로 보여 줍니다.</param>
    /// <remarks>
    /// 오래 걸립니다(디스크를 실제로 읽습니다). 진행 상황을 보여 주지 않으면 사용자는
    /// 멈춘 줄 알고 앱을 닫습니다 — 그래서 도구가 불릴 때마다 <paramref name="progress"/>로 알립니다.
    /// </remarks>
    public static async Task<ClaudeAnswer> AskAsync(
        string question,
        string bridgePath,
        bool korean,
        string? resume = null,
        IProgress<ClaudeEvent>? progress = null,
        CancellationToken ct = default)
    {
        string? claude = ClaudeRegistration.FindOnPath("claude");
        if (claude is null) return new(false, "", Error: "claude not found");

        var psi = new ProcessStartInfo(claude)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),

            // 빈 폴더에서 돌립니다 — 지금 폴더에 있는 남의 프로젝트 설정이 함께 딸려 오면
            // 우리가 의도하지 않은 지시가 섞입니다.
            WorkingDirectory = NeutralFolder(),
        };

        foreach (string a in BuildArguments(question, bridgePath, korean, resume)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) return new(false, "", Error: "could not start");

        // 표준오류는 따로 끝까지 읽습니다. 안 읽으면 파이프가 차는 순간 상대가 멈춥니다.
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        string answer = "";
        string? failure = null;
        string? session = null;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                Interpret(line, progress, ref answer, ref failure, ref session);
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 사용자가 중단했습니다. 자식까지 함께 정리합니다 — Claude가 띄운 중계기가
            // 남으면 통로를 계속 쥐고 있습니다.
            try { process.Kill(entireProcessTree: true); } catch { /* 이미 끝났습니다. */ }
            throw;
        }

        if (answer.Length > 0 && failure is null) return new(true, answer, session);

        string stderr = await errorTask;
        return new(false, answer, session,
            failure ?? FirstMeaningfulLine(stderr) ?? $"exit {process.ExitCode}");
    }

    /// <summary>실제로 넘기는 인자들.</summary>
    /// <remarks>
    /// <c>--bare</c>는 쓰지 않습니다 — 그 모드는 사용자의 로그인을 읽지 않고 API 열쇠를
    /// 요구하므로, 구독을 그대로 쓰겠다는 이 설계의 전제가 무너집니다.
    /// </remarks>
    internal static IReadOnlyList<string> BuildArguments(
        string question, string bridgePath, bool korean, string? resume = null) =>
    [
        "-p", question,

        // 이어지는 질문이면 같은 대화로 붙입니다 — 없으면 "그건 왜 그래?"가 통하지 않고,
        // 방금 읽은 디스크를 처음부터 다시 읽습니다.
        .. resume is { Length: > 0 } ? new[] { "--resume", resume } : [],

        // 한 줄씩 나오는 대로 받아 화면에 옮깁니다 — 다 끝난 뒤에만 보여 주면 몇 분간
        // 아무 일도 없는 것처럼 보입니다.
        "--output-format", "stream-json",
        "--verbose",

        // 글자가 만들어지는 대로 받습니다. 이 한 줄이 없으면 완성된 덩어리만 오고,
        // 사용자는 답이 다 될 때까지 멈춘 화면을 봅니다 — 실기에서 56초였습니다.
        "--include-partial-messages",

        // 우리 중계기만 붙입니다. 여기서 직접 넘기므로 Claude 설정에 등록이 없어도 됩니다.
        "--mcp-config", McpConfigJson(bridgePath),

        // ⚠ 이 한 줄이 없으면 <b>사용자가 등록해 둔 다른 MCP 서버가 전부 함께 뜹니다.</b>
        //   2026-08-13 실기: 우리 것 하나만 부르려던 자리에 Figma·Gmail·Google Drive·
        //   Google Calendar까지 붙어 도구가 56개에서 92개로 늘었고, 그중 셋은 인증이 풀린
        //   상태(needs-auth)였습니다. 디스크를 봐 달라고 부른 자리에 남의 메일 도구가
        //   들어와 있을 이유가 없습니다 — 느려지고, 실패할 거리가 늘고, 무엇보다
        //   사용자가 허락한 적 없는 연결입니다.
        "--strict-mcp-config",

        // 우리 도구는 묻지 않고 쓰게 합니다 — 화면 없는 실행이라 물어보면 그대로 멈춥니다.
        "--allowedTools", $"mcp__{ClaudeRegistration.ServerName}",

        // 그 밖의 것은 허락 규칙에 없으면 하지 못합니다. 디스크를 봐 달라고 부른 것이지
        // 이 컴퓨터의 파일을 뒤져 달라고 부른 것이 아닙니다.
        "--permission-mode", "dontAsk",

        "--append-system-prompt", FocusPrompt(korean),
    ];

    /// <summary>중계기를 가리키는 MCP 설정 한 덩어리.</summary>
    internal static string McpConfigJson(string bridgePath) =>
        "{\"mcpServers\":{\"" + ClaudeRegistration.ServerName + "\":{\"command\":" +
        JsonQuote(bridgePath) + ",\"args\":[\"--mcp-stdio\"]}}}";

    /// <summary>무엇을 하는 자리인지 알려 주는 덧붙임 지시.</summary>
    /// <remarks>
    /// 답이 길고 전문적이면 초보자에게는 없는 것과 같습니다. 이 앱의 사용자는 "무엇을
    /// 눌러야 하는지"를 알고 싶어 합니다 — 그것을 문장으로 못 박습니다.
    /// </remarks>
    internal static string FocusPrompt(bool korean) => korean
        ? "당신은 DiskMigrator-X 앱 안에서 디스크 문제를 진단하고 있습니다. " +
          "판단은 반드시 diskmigrator-x 도구로 직접 읽어서 하십시오 — 추측하지 마십시오. " +
          "이 컴퓨터의 파일을 읽거나 고치지 마십시오. " +
          "답은 한국어로, 컴퓨터를 잘 모르는 사람이 읽는다고 생각하고 짧게 쓰십시오. " +
          "줄임말을 쓰지 말고 풀어서 설명하십시오. " +
          "확인하지 못한 것은 원인이라고 말하지 말고 '확인하지 못했다'고 말하십시오. " +
          "마지막에는 사용자가 이 앱에서 무엇을 누르면 되는지 한 줄로 알려 주십시오."
        : "You are diagnosing disk problems inside the DiskMigrator-X app. " +
          "Base every claim on what the diskmigrator-x tools actually read — do not guess. " +
          "Do not read or modify files on this computer. " +
          "Answer briefly, for someone who is not comfortable with computers. " +
          "If you could not verify something, say you could not verify it rather than naming it as the cause. " +
          "End with one line telling the user which button to press in this app.";

    /// <summary>
    /// 나온 줄 하나를 해석합니다 — 진행 상황이면 알리고, 마지막 답이면 담습니다.
    /// </summary>
    /// <remarks>
    /// 모르는 종류는 <b>그냥 넘깁니다.</b> 형식이 늘어나는 것은 정상이고, 모르는 줄에
    /// 걸려 넘어지면 답을 통째로 잃습니다.
    /// </remarks>
    private static void Interpret(
        string line, IProgress<ClaudeEvent>? progress, ref string answer, ref string? failure, ref string? session)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            // 대화 표시는 여러 줄에 실려 옵니다. 보이는 대로 잡아 둡니다 — 이것을 놓치면
            // 다음 질문이 새 대화가 되어 사용자는 "왜 못 알아듣지?" 하게 됩니다.
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                session = sid.GetString();

            switch (type)
            {
                // 글자 조각. 이것이 흘러야 사용자가 답이 만들어지는 것을 봅니다.
                case "stream_event" when root.TryGetProperty("event", out var ev) &&
                                         ev.TryGetProperty("delta", out var delta) &&
                                         delta.TryGetProperty("type", out var dt) &&
                                         dt.GetString() == "text_delta" &&
                                         delta.TryGetProperty("text", out var txt):
                    if (txt.GetString() is { Length: > 0 } chunk)
                        progress?.Report(new ClaudeEvent.Text(chunk));
                    break;

                case "assistant" when root.TryGetProperty("message", out var m) &&
                                      m.TryGetProperty("content", out var blocks) &&
                                      blocks.ValueKind == JsonValueKind.Array:
                    foreach (var block in blocks.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use" &&
                            block.TryGetProperty("name", out var n))
                        {
                            progress?.Report(new ClaudeEvent.Tool(FriendlyToolName(n.GetString() ?? "")));
                        }
                    }
                    break;

                case "result":
                    // 마지막 줄입니다. 여기에 최종 답과 성패가 함께 들어 있습니다.
                    if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                        answer = r.GetString() ?? "";

                    if (root.TryGetProperty("is_error", out var e) &&
                        e.ValueKind == JsonValueKind.True)
                    {
                        failure = answer.Length > 0 ? answer : "run failed";
                        answer = "";
                    }
                    break;
            }
        }
        catch
        {
            // JSON이 아닌 줄입니다(경고 등). 넘깁니다.
        }
    }

    /// <summary>도구 이름을 사람 말로 — 화면에 그대로 뜹니다.</summary>
    private static string FriendlyToolName(string raw)
    {
        // "mcp__diskmigrator-x__list_disks" → "list_disks"
        int last = raw.LastIndexOf("__", StringComparison.Ordinal);
        return last >= 0 ? raw[(last + 2)..] : raw;
    }

    /// <summary>남의 프로젝트 설정이 딸려 오지 않도록, 우리 폴더 안의 빈 자리에서 돌립니다.</summary>
    private static string NeutralFolder()
    {
        string path = Path.Combine(AppIdentity.DataDirectory, "ask");
        try { Directory.CreateDirectory(path); } catch { return AppIdentity.DataDirectory; }
        return path;
    }

    private static string? FirstMeaningfulLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>문자열 하나를 JSON 규칙으로 감쌉니다.</summary>
    private static string JsonQuote(string s) => $"\"{System.Text.Json.JsonEncodedText.Encode(s)}\"";
}
