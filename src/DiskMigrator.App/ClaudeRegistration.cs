using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskMigrator.App;

/// <summary>등록을 시도한 곳.</summary>
public enum ClaudeTarget
{
    /// <summary>Claude 데스크톱 앱.</summary>
    DesktopApp,

    /// <summary>Claude Code — 명령줄 도구.</summary>
    ClaudeCode,
}

/// <summary>등록이 어떻게 끝났는지.</summary>
public enum ClaudeRegistrationStatus
{
    /// <summary>등록했습니다.</summary>
    Registered,

    /// <summary>그 프로그램이 없어 건너뛰었습니다 — 실패가 아닙니다.</summary>
    NotInstalled,

    /// <summary>있는데 등록하지 못했습니다.</summary>
    Failed,
}

/// <summary>한 곳에 등록을 시도한 결과.</summary>
/// <param name="Target">어디에.</param>
/// <param name="Status">어떻게 끝났는지.</param>
/// <param name="Detail">
/// 실패 이유(원문). 화면에 붙여 보여 줍니다 — 우리가 번역할 수 없는 남의 오류 문구입니다.
/// </param>
/// <remarks>
/// 사용자에게 보일 문장은 <b>여기서 만들지 않습니다.</b> 이 앱은 한국어·영어를 함께
/// 쓰므로, 문장은 화면 쪽(문자열 자원)에서 골라야 합니다. 여기서 한국어로 지어 돌려주면
/// 영어 화면에 한국어가 섞입니다 — 부팅 검사 배지에서 실제로 그랬습니다.
/// </remarks>
public sealed record ClaudeRegistrationResult(
    ClaudeTarget Target, ClaudeRegistrationStatus Status, string? Detail = null);

/// <summary>
/// <b>[Claude에 연결하기]</b> 버튼이 실제로 하는 일 — Claude 쪽에 우리 중계기를 등록합니다.
/// </summary>
/// <remarks>
/// <b>왜 만들었나.</b> 그전까지 연결하려면 사용자가 앱에서 명령을 복사해 <i>명령 프롬프트를
/// 열고</i> 붙여 넣어야 했습니다. 초보자는 거기서 멈춥니다 — 2026-08-10에 사용자 본인이
/// "MCP 연결을 어떻게 하지?"라고 물었고, 주소를 넣는 커넥터 화면은 <c>https</c>를 요구해
/// 아예 거부했습니다. 우리가 아는 값으로 우리가 등록하면 그 단계가 통째로 사라집니다.
///
/// <para><b>몰래 고치지 않습니다.</b> "Claude 설정 파일을 앱이 자동으로 손대지 않는다"는
/// 원칙은 그대로입니다 — 사람이 버튼을 누를 때만, 무엇을 쓸지 보여 준 뒤에 씁니다.
/// 기존 파일은 덮어쓰기 전에 <c>.bak</c>으로 남깁니다.</para>
///
/// <para><b>토큰을 설정 파일에 넣지 않습니다.</b> 두 곳 모두 중계기(표준입출력)로 등록합니다.
/// 중계기가 토큰을 스스로 꺼내 쓰므로 열쇠가 평문 파일에 남지 않고, 포트가 바뀌어도
/// 등록을 다시 할 필요가 없습니다 — http 주소를 직접 적어 두면 포트가 바뀌는 순간 끊깁니다.</para>
/// </remarks>
public static class ClaudeRegistration
{
    /// <summary>두 곳 모두에서 쓰는 서버 이름.</summary>
    public const string ServerName = "diskmigrator-x";

    /// <summary>중계기 실행 파일 이름 — 앱 실행 파일 옆에 함께 설치됩니다.</summary>
    public const string BridgeFileName = "DiskMigratorX.Bridge.exe";

    /// <summary>Claude 데스크톱 앱이 읽는 설정 파일.</summary>
    public static string DesktopConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", "claude_desktop_config.json");

    /// <summary>
    /// 중계기 실행 파일의 전체 경로. 없으면 null.
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="AppContext.BaseDirectory"/>가 아니라 <see cref="Environment.ProcessPath"/>를
    /// 봅니다. 이 앱은 단일 파일로 배포되어 <b>임시 폴더에 풀린 뒤</b> 실행되므로, BaseDirectory는
    /// 그 임시 폴더를 가리킵니다 — 거기 적힌 경로를 설정에 써 두면 다음 실행에서 사라집니다.
    /// </remarks>
    public static string? FindBridge()
    {
        string? folder = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(folder)) return null;

        string path = Path.Combine(folder, BridgeFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Claude 데스크톱 앱에 등록합니다. 이미 있으면 최신 경로로 바꿔 씁니다.
    /// </summary>
    /// <remarks>
    /// 데스크톱 앱이 로컬 서버를 받아들이는 길은 <c>mcpServers</c>에 <c>command</c>를 적는
    /// 것 하나뿐입니다(2026-08-10 확인). 파일의 다른 항목은 손대지 않습니다 — 사용자가
    /// 이미 등록해 둔 다른 서버와 설정이 함께 들어 있습니다.
    /// </remarks>
    public static ClaudeRegistrationResult RegisterDesktopApp(string bridgePath) =>
        RegisterDesktopApp(bridgePath, DesktopConfigPath);

    /// <summary>설정 파일 자리를 정해서 등록합니다 — 시험이 진짜 파일을 건드리지 않게.</summary>
    internal static ClaudeRegistrationResult RegisterDesktopApp(string bridgePath, string configPath)
    {
        try
        {
            JsonObject root;
            if (File.Exists(configPath))
            {
                string existing = File.ReadAllText(configPath);
                root = (string.IsNullOrWhiteSpace(existing)
                    ? new JsonObject()
                    : JsonNode.Parse(existing) as JsonObject) ?? new JsonObject();

                // 덮어쓰기 전에 원본을 남깁니다 — 우리 실수로 남의 설정을 잃게 할 수 없습니다.
                File.Copy(configPath, configPath + ".bak", overwrite: true);
            }
            else
            {
                root = new JsonObject();
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            }

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = [];
                root["mcpServers"] = servers;
            }

            servers[ServerName] = new JsonObject
            {
                ["command"] = bridgePath,
                // 중계기는 인자를 요구하지 않지만, 설정 파일을 사람이 열어 봤을 때 이것이
                // 무엇을 하는 프로그램인지 알아볼 수 있게 남깁니다.
                ["args"] = new JsonArray("--mcp-stdio"),
            };

            File.WriteAllText(
                configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new(ClaudeTarget.DesktopApp, ClaudeRegistrationStatus.Registered);
        }
        catch (Exception ex)
        {
            return new(ClaudeTarget.DesktopApp, ClaudeRegistrationStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Claude Code(명령줄)에 등록합니다. 설치돼 있지 않으면 건너뜁니다.
    /// </summary>
    /// <remarks>
    /// <c>claude mcp add</c>를 대신 실행합니다 — 설정 파일을 직접 건드리는 것보다 안전합니다.
    /// 형식은 그쪽이 정하는 것이고, 우리가 그 형식을 따라가려 하면 언젠가 어긋납니다.
    ///
    /// <para><c>--scope user</c>로 넣습니다. 기본값은 <b>지금 폴더에서만</b> 통하는 범위라,
    /// 다른 폴더에서 Claude를 열면 등록이 없는 것처럼 보입니다.</para>
    /// </remarks>
    public static ClaudeRegistrationResult RegisterClaudeCode(string bridgePath)
    {
        string? claude = FindOnPath("claude");
        if (claude is null)
            return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.NotInstalled);

        try
        {
            // "--" 뒤는 실행할 프로그램입니다. 그 앞뒤를 섞으면 인자로 먹힙니다.
            var psi = new ProcessStartInfo(claude)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[]
                     { "mcp", "add", "--scope", "user", ServerName, "--", bridgePath, "--mcp-stdio" })
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
                return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.Failed, "start failed");

            // ⚠ 출력을 먼저 끝까지 읽고 기다립니다. 반대로 하면 파이프 버퍼가 차는 순간
            //   상대가 쓰기에서 멈추고, 우리는 그 상대를 기다립니다 — 서로 막힙니다.
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 이미 끝났습니다. */ }
                return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.Failed, "timeout");
            }

            if (process.ExitCode == 0)
                return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.Registered);

            string why = string.IsNullOrWhiteSpace(error) ? output : error;
            return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.Failed, FirstLine(why));
        }
        catch (Exception ex)
        {
            return new(ClaudeTarget.ClaudeCode, ClaudeRegistrationStatus.Failed, ex.Message);
        }
    }

    /// <summary>PATH에서 실행 파일을 찾습니다(<c>.cmd</c>·<c>.exe</c> 등 확장자 포함).</summary>
    /// <remarks>
    /// <c>Process.Start("claude")</c>만으로는 찾지 못하는 경우가 있습니다 — 명령줄 도구가
    /// <c>.cmd</c> 감싸개로 설치되면 PATHEXT를 거쳐야 풀리기 때문입니다. 직접 훑습니다.
    /// </remarks>
    internal static string? FindOnPath(string command)
    {
        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim(), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // PATH에 잘못된 문자가 든 항목이 있습니다. 다음으로 넘어갑니다.
                }
            }
        }
        return null;
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "이유 없음";
        return line.Length <= 160 ? line : line[..160] + "…";
    }
}
