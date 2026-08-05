using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiskMigrator.App;

/// <summary>보관해 둔 연결 정보. 없으면 <see cref="McpTokenStore.Load"/>가 null을 돌려줍니다.</summary>
/// <param name="Token">지난번에 쓰던 접근 토큰.</param>
/// <param name="Port">지난번에 실제로 열린 포트. 다음에도 이 포트를 먼저 시도합니다.</param>
public sealed record McpConnectionSettings(string Token, int Port);

/// <summary>
/// MCP 접근 토큰과 포트를 다음 실행까지 <b>기억합니다.</b>
/// </summary>
/// <remarks>
/// 원래는 앱을 켤 때마다 새 토큰을 만들었습니다. 안전해 보이지만, 실제로는 앱을 재시작할
/// 때마다 사용자가 주소·토큰을 복사해 Claude 설정에 다시 붙여 넣어야 했습니다 —
/// 2026-08-05 하루 작업에서만 <b>일곱 번</b> 반복했습니다. 초보자에게는 그 지점에서 대부분
/// 포기합니다.
///
/// <para><b>바뀐 것과 바뀌지 않은 것.</b> 토큰을 기억한다고 통로가 열려 있는 것은 아닙니다.
/// 통로는 여전히 사용자가 <c>[연결 통로 열기]</c>를 눌러야 열리고, 앱을 닫으면 닫힙니다.
/// 기억하는 것은 "열었을 때 쓸 열쇠"뿐입니다.</para>
///
/// <para><b>저장 방식.</b> 파일에 평문으로 두면 같은 PC의 다른 사용자 계정이 읽을 수 있습니다.
/// DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)로 암호화해 <b>이 Windows 사용자만</b>
/// 풀 수 있게 합니다. 파일을 다른 PC나 다른 계정으로 옮기면 복호화에 실패하며, 그때는
/// 조용히 새 토큰을 발급합니다.</para>
///
/// <para>읽기·쓰기 실패는 모두 "저장된 것 없음"으로 처리합니다 — 토큰을 못 읽는 것이
/// 앱을 못 쓰는 이유가 되어서는 안 됩니다.</para>
/// </remarks>
public static class McpTokenStore
{
    private static string FilePath => Path.Combine(AppIdentity.DataDirectory, "mcp-connection.dat");

    /// <summary>암호문에 섞는 고정 값 — 다른 용도로 암호화된 데이터와 뒤바뀌지 않게 합니다.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DiskMigrator-X.mcp.v1");

    /// <summary>보관된 연결 정보. 없거나 읽지 못하면 null.</summary>
    public static McpConnectionSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);

            // 형식: "토큰\n포트"
            string[] parts = Encoding.UTF8.GetString(plain).Split('\n');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) return null;
            if (!int.TryParse(parts[1], out int port) || port is < 1 or > 65535) return null;

            return new McpConnectionSettings(parts[0], port);
        }
        catch
        {
            // 다른 계정·다른 PC에서 만든 파일이거나 손상됐습니다. 새로 발급하면 됩니다.
            return null;
        }
    }

    /// <summary>연결 정보를 보관합니다. 실패해도 앱 동작에는 영향이 없습니다.</summary>
    public static void Save(string token, int port)
    {
        try
        {
            Directory.CreateDirectory(AppIdentity.DataDirectory);
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes($"{token}\n{port}"), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }
        catch
        {
            // 보관하지 못하면 다음 실행에서 새 토큰이 나올 뿐입니다.
        }
    }

    /// <summary>보관된 정보를 지웁니다 — 사용자가 토큰을 새로 발급할 때.</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 지우지 못해도 다음 저장이 덮어씁니다.
        }
    }
}
