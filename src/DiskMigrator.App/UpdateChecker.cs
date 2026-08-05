using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DiskMigrator.App;

/// <summary>업데이트 확인 결과.</summary>
/// <param name="Available">현재보다 새 버전이 있는지.</param>
/// <param name="LatestVersion">최신 버전 문자열(예: "0.8.0"). 없으면 null.</param>
/// <param name="ReleaseUrl">최신 릴리스 페이지 URL. 없으면 null.</param>
public sealed record UpdateInfo(bool Available, string? LatestVersion, string? ReleaseUrl);

/// <summary>
/// GitHub Releases에서 최신 버전을 확인합니다. 자동으로 내려받거나 실행하지 않고,
/// "새 버전이 있는지"와 릴리스 페이지 링크만 돌려줍니다 — 다운로드·설치는 사용자가 결정합니다.
/// </summary>
/// <remarks>
/// 네트워크 오류·오프라인·요청 제한 등은 모두 조용히 "업데이트 없음"으로 처리합니다.
/// 시작 때마다 GitHub에 요청이 나가는 게 싫으면 환경변수 <c>DM_NO_UPDATE_CHECK</c>를 설정하면
/// 확인을 건너뜁니다.
/// </remarks>
public static class UpdateChecker
{
    private const string Repo = "withoz/DiskMigrator";

    /// <summary>
    /// DiskMigrator-X에는 아직 <b>자기 릴리스 채널이 없습니다.</b> 확인을 하지 않습니다.
    /// </summary>
    /// <remarks>
    /// <c>releases/latest</c>는 저장소의 최신 릴리스 하나를 돌려줍니다 — 지금은 <b>수동 버전</b>의
    /// 것입니다. 그대로 두면 X가 *"새 버전이 있습니다 1.4.2"* 라고 안내하고, 누르면 사용자가
    /// <b>다른 제품</b>을 내려받습니다(2026-08-05 실기에서 그대로 재현됨).
    ///
    /// <para>버전을 1.0.0으로 맞춰도 해결되지 않습니다. 태그 비교상 1.0.0 &lt; 1.4.x 이므로
    /// <b>영원히</b> "새 버전이 있다"가 됩니다.</para>
    ///
    /// <para>X를 릴리스할 때 X 전용 태그 접두사(예: <c>x-v1.0.0</c>)로 채널을 나누고, 그 접두사만
    /// 골라 비교하도록 이 클래스를 고친 뒤 이 스위치를 끄십시오. 그때까지는 잘못된 안내를
    /// 하느니 하지 않는 편이 낫습니다.</para>
    /// </remarks>
    /// <remarks>
    /// <c>const</c>가 아니라 <c>static readonly</c>입니다 — <c>const false</c>로 두면 아래 코드가
    /// 전부 "도달 불가"(CS0162)로 잡힙니다. 그 코드는 지울 것이 아니라 채널이 생기면 다시 쓸
    /// 것이므로 남겨 둡니다.
    /// </remarks>
    private static readonly bool HasOwnReleaseChannel = false;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct = default)
    {
        if (!HasOwnReleaseChannel) return new(false, null, null);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DM_NO_UPDATE_CHECK")))
            return new(false, null, null);

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("DiskMigrator-UpdateCheck", "1.0"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new(false, null, null);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            Version? latest = ParseVersion(tag);
            if (latest is null) return new(false, null, null);

            bool newer = Normalize(latest) > Normalize(current);
            return newer
                ? new(true, tag!.TrimStart('v', 'V'), url)
                : new(false, null, null);
        }
        catch
        {
            // 오프라인·요청 제한·형식 변경 등은 업데이트 없음으로 조용히 처리합니다.
            return new(false, null, null);
        }
    }

    /// <summary>"v0.8.0" · "0.8.0-beta" 같은 태그에서 Major.Minor.Build 버전을 뽑습니다.</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];   // 프리릴리스 접미사 제거
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>리비전 유무 차이로 인한 오탐을 없애려 Major.Minor.Build로만 비교합니다.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
