using System.IO;

namespace DiskMigrator.App;

/// <summary>
/// 사용자가 고른 UI 언어를 저장·불러옵니다. 없으면(자동) OS 언어를 씁니다.
/// </summary>
/// <remarks>
/// <c>%LocalAppData%\DiskMigrator\language.txt</c>에 "ko" 또는 "en"을 적습니다. 로그·EULA
/// 표시와 같은 폴더라 한 곳에서 관리됩니다. 저장된 값이 우선, 다음이 환경변수 DM_LANG,
/// 그다음이 OS 언어입니다(App.ApplyCulture).
/// </remarks>
public static class LanguagePreference
{
    private static string Path0 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMigrator", "language.txt");

    /// <summary>저장된 언어 코드("ko"/"en"). 없으면 null.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(Path0)) return null;
            string v = File.ReadAllText(Path0).Trim().ToLowerInvariant();
            return v is "ko" or "en" ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>언어 선택을 저장합니다.</summary>
    public static void Save(string lang)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path0)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path0, lang);
        }
        catch
        {
            // 저장 실패(권한 등)는 치명적이지 않습니다 — 이번 세션에는 적용되고 다음엔 OS 기본.
        }
    }
}
