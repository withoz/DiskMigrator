using System.Globalization;
using System.Resources;

namespace DiskMigrator.Core.Localization;

/// <summary>
/// Core가 사용자에게 보여줄 문자열(안전 경고 등)을 문화권에 맞춰 조회합니다.
/// 중립 리소스(Strings.resx)는 한국어, Strings.en.resx가 영어입니다. 언어는 앱이
/// 시작 시 <see cref="CultureInfo.CurrentUICulture"/>로 정하며, 여기서는 그것을 따릅니다.
/// </summary>
/// <remarks>
/// 로그·예외 진단 문자열까지 옮기지는 않습니다 — 사용자에게 보이는 것만 리소스로 뺍니다.
/// </remarks>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("DiskMigrator.Core.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
        => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
