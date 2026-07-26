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

/// <summary>
/// 엔진 계층(Core·Windows)의 사용자 노출 문자열을 <b>쓰이는 자리에서</b> 이중언어로 적습니다.
/// </summary>
/// <remarks>
/// 결과 메시지·예외 문구·진행 단계처럼 UI에 도달하는 문자열이 엔진 전반에 수백 개라, 전부
/// 리소스 키로 빼면 키 이름 짓기·resx 동기화가 코드보다 커집니다. 대신 두 언어를 나란히 적는
/// <c>L.T("한국어", "English")</c>를 씁니다 — 번역이 코드 리뷰에서 바로 보이고, 키 불일치가
/// 원천적으로 없습니다. 보간 문자열은 두 번 평가되지만 전부 값싼 인자입니다. 제3 언어를
/// 추가하게 되면 그때 리소스로 옮깁니다. UI 라벨(XAML)은 계속 resx를 씁니다.
/// </remarks>
public static class L
{
    /// <summary>현재 UI 언어가 한국어인지.</summary>
    public static bool IsKo => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    /// <summary>현재 UI 언어에 맞는 쪽을 돌려줍니다.</summary>
    public static string T(string ko, string en) => IsKo ? ko : en;
}
