using System.Security.Cryptography;

namespace DiskMigrator.Mcp;

/// <summary>지난번 연결에서 이어 쓸 값 — 토큰과 포트.</summary>
/// <param name="Token">그때 발급했던 토큰.</param>
/// <param name="Port">그때 실제로 열렸던 포트.</param>
public sealed record McpReuse(string Token, int Port);

/// <summary>
/// 로컬 MCP 엔드포인트의 접근 토큰.
/// </summary>
/// <remarks>
/// 127.0.0.1에만 묶더라도 같은 PC의 다른 프로세스는 접근할 수 있습니다.
/// 이 도구는 디스크 정보를 읽으므로 아무나 부르게 두면 안 됩니다 — 계획서 §7.
///
/// <para><b>이 어셈블리는 토큰을 저장하지 않습니다.</b> 어디에 어떻게 보관할지는 앱이
/// 정합니다(<c>McpTokenStore</c>가 DPAPI로 암호화해 사용자 폴더에 둡니다). 여기서는 값을
/// 만들고 대조하는 일만 합니다.</para>
///
/// <para>실행마다 새로 만들던 것을 이어 쓰게 바꾼 이유: 앱을 재시작할 때마다 사용자가
/// 주소·토큰을 복사해 Claude 설정에 다시 붙여 넣어야 했습니다. 하루 작업에서 일곱 번
/// 반복한 뒤 고쳤습니다. <b>Claude의 설정 파일을 앱이 고치는 일은 여전히 하지 않습니다</b> —
/// 사용자가 한 번 붙여 넣은 값이 계속 통하게 만들었을 뿐입니다.</para>
/// </remarks>
public sealed class AccessToken
{
    private AccessToken(string value) => Value = value;

    /// <summary>사용자가 복사해 넣을 토큰 문자열.</summary>
    public string Value { get; }

    /// <summary>
    /// 보관해 둔 문자열로 토큰을 되살립니다 — 재시작 후에도 같은 열쇠를 쓰기 위해.
    /// </summary>
    public static AccessToken FromStored(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("빈 토큰은 되살릴 수 없습니다.", nameof(value))
            : new AccessToken(value);

    /// <summary>암호학적 난수로 새 토큰을 만듭니다.</summary>
    public static AccessToken Create()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        // URL·헤더에 그대로 넣을 수 있게 base64url 형태로.
        string s = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return new AccessToken(s);
    }

    /// <summary>
    /// 제시된 값이 이 토큰과 같은지 — <b>길이에 무관한 시간</b>으로 비교합니다.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(Value));
    }

    /// <summary>화면에 보여줄 때 쓰는 가림 형태(앞 4글자만).</summary>
    public string Preview => Value.Length <= 4 ? Value : Value[..4] + "…";
}
