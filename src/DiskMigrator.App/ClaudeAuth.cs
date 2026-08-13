using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DiskMigrator.App;

/// <summary>지금 이 컴퓨터에서 Claude에 누가 들어와 있는지.</summary>
/// <param name="Installed">Claude Code가 이 컴퓨터에 있는지.</param>
/// <param name="LoggedIn">로그인돼 있는지.</param>
/// <param name="Email">로그인한 계정. 모르면 null.</param>
/// <param name="Plan">구독 종류(<c>max</c>, <c>pro</c> 등). 모르면 null.</param>
public sealed record ClaudeAccount(bool Installed, bool LoggedIn, string? Email = null, string? Plan = null)
{
    /// <summary>아무것도 못 물어보는 상태.</summary>
    public static readonly ClaudeAccount NotInstalled = new(false, false);
}

/// <summary>
/// Claude 로그인 상태를 읽고, 로그인 절차를 띄웁니다.
/// </summary>
/// <remarks>
/// <b>앱 안에 진짜 로그인 창은 만들지 않습니다.</b> 아이디·비밀번호를 우리 창에서 받으려면
/// Anthropic이 발급하는 자격이 있어야 하고, 무엇보다 <b>디스크를 지우는 도구가 남의 계정
/// 비밀번호를 받아서는 안 됩니다.</b> 비밀번호는 이 앱을 지나가지 않습니다.
///
/// <para>대신 <c>claude auth</c>에 물어보고, 필요하면 그쪽 로그인 절차를 띄웁니다.
/// 사용자에게는 "버튼 한 번"으로 같아 보이되, 자격 증명은 우리가 만지지 않습니다.</para>
/// </remarks>
public static class ClaudeAuth
{
    /// <summary>
    /// 로그인 상태를 읽습니다. Claude Code가 없으면 <see cref="ClaudeAccount.NotInstalled"/>.
    /// </summary>
    /// <remarks>
    /// <c>claude auth status --json</c>은 기계가 읽으라고 있는 출력입니다(사람용은 <c>--text</c>).
    /// 형식이 달라져도 앱이 죽지 않게, 못 읽으면 "모른다"로 처리합니다.
    /// </remarks>
    public static async Task<ClaudeAccount> ReadAsync(CancellationToken ct = default)
    {
        string? claude = ClaudeRegistration.FindOnPath("claude");
        if (claude is null) return ClaudeAccount.NotInstalled;

        try
        {
            var psi = new ProcessStartInfo(claude)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");

            using var process = Process.Start(psi);
            if (process is null) return new(true, false);

            string output = await process.StandardOutput.ReadToEndAsync(ct);
            _ = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return Parse(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 있긴 한데 물어보지 못했습니다. 로그인 여부는 모릅니다.
            return new(true, false);
        }
    }

    /// <summary>출력 한 덩어리에서 계정 정보를 꺼냅니다.</summary>
    internal static ClaudeAccount Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool loggedIn = root.TryGetProperty("loggedIn", out var l) &&
                            l.ValueKind == JsonValueKind.True;

            return new(
                Installed: true,
                LoggedIn: loggedIn,
                Email: Text(root, "email"),
                Plan: Text(root, "subscriptionType"));
        }
        catch
        {
            return new(true, false);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// 로그인 절차를 띄웁니다. 우리는 <b>기다리지 않습니다</b> — 사용자가 브라우저에서 끝냅니다.
    /// </summary>
    /// <remarks>
    /// 창을 감추지 않습니다. <c>claude auth login</c>은 주소를 찍어 주거나 입력을 받을 수
    /// 있는데, 창이 없으면 사용자는 <b>아무 일도 일어나지 않는 버튼</b>을 누른 셈이 됩니다.
    ///
    /// <para>⚠ <b>실기 확인이 필요한 자리입니다.</b> 이 앱은 관리자 권한으로 뜨므로 여기서
    /// 띄우는 것도 상승된 채로 돕니다. 그때 브라우저가 이미 켜져 있는 보통 권한 창으로
    /// 넘어가지 못하고 새로 뜰 수 있습니다. 실패하면 사용자에게 명령 한 줄을 보여 주고
    /// 직접 실행하게 하는 길을 남겨야 합니다.</para>
    /// </remarks>
    public static bool StartLogin()
    {
        string? claude = ClaudeRegistration.FindOnPath("claude");
        if (claude is null) return false;

        try
        {
            var psi = new ProcessStartInfo(claude)
            {
                // 창을 보여 줘야 합니다 — 사용자가 그 안에서 절차를 마칩니다.
                UseShellExecute = true,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>사용자가 직접 칠 수 있는 명령 — 버튼이 안 될 때의 대비책.</summary>
    public const string LoginCommand = "claude auth login";
}
