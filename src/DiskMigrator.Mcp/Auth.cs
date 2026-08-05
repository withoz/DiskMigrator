using System.Security.Cryptography;

namespace DiskMigrator.Mcp;

/// <summary>
/// 로컬 MCP 엔드포인트의 접근 토큰. 앱 실행 단위로 새로 만듭니다.
/// </summary>
/// <remarks>
/// 127.0.0.1에만 묶더라도 같은 PC의 다른 프로세스는 접근할 수 있습니다.
/// 이 도구는 디스크 정보를 읽으므로 아무나 부르게 두면 안 됩니다 — 계획서 §7.
///
/// <para>토큰을 파일에 저장하지 않습니다. 앱을 다시 켜면 새 토큰이 나오고,
/// 사용자는 앱 화면에서 복사해 MCP 설정에 넣습니다. 번거롭지만,
/// 남의 설정 파일을 앱이 몰래 고치는 것보다 낫습니다.</para>
/// </remarks>
public sealed class AccessToken
{
    private AccessToken(string value) => Value = value;

    /// <summary>사용자가 복사해 넣을 토큰 문자열.</summary>
    public string Value { get; }

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
