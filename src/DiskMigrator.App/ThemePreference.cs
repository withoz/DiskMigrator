using System.IO;
using Microsoft.Win32;

namespace DiskMigrator.App;

/// <summary>화면 색조.</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// 사용자가 고른 화면 색조를 저장·불러옵니다. 고른 적이 없으면 <b>Windows 설정을 따릅니다.</b>
/// </summary>
/// <remarks>
/// <c>%LocalAppData%\DiskMigrator-X\theme.txt</c>에 "light" 또는 "dark"를 적습니다.
/// 언어 선택([[LanguagePreference]])과 같은 폴더·같은 방식입니다.
///
/// <para><b>왜 기본이 "Windows를 따름"인가.</b> 이 앱은 하루에 한 번 여는 도구가 아니라
/// 필요할 때만 여는 도구입니다. 그런 앱이 시스템과 다른 색으로 혼자 튀어나오면 사용자는
/// 설정을 찾아 들어가야 합니다. 골라 준 적이 없으면 이미 골라 둔 것(Windows)을 따르는 편이
/// 맞습니다 — 그리고 한 번 고르면 그 선택이 이깁니다.</para>
/// </remarks>
public static class ThemePreference
{
    private static string Path0 => Path.Combine(AppIdentity.DataDirectory, "theme.txt");

    /// <summary>저장된 선택. 고른 적이 없으면 null(= Windows를 따름).</summary>
    public static AppTheme? Load()
    {
        try
        {
            if (!File.Exists(Path0)) return null;
            return File.ReadAllText(Path0).Trim().ToLowerInvariant() switch
            {
                "dark" => AppTheme.Dark,
                "light" => AppTheme.Light,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>선택을 저장합니다. 실패해도 이번 실행에는 적용됩니다.</summary>
    public static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, theme == AppTheme.Dark ? "dark" : "light");
        }
        catch
        {
            // 저장 실패(권한 등)는 치명적이지 않습니다 — 다음 실행에 Windows 설정으로 돌아갈 뿐입니다.
        }
    }

    /// <summary>
    /// Windows의 앱 색조 설정. 읽지 못하면 밝은 쪽으로 봅니다.
    /// </summary>
    /// <remarks>
    /// <c>AppsUseLightTheme</c>은 <b>앱</b>용이고 <c>SystemUsesLightTheme</c>은 작업 표시줄용입니다.
    /// 둘은 따로 설정할 수 있어, 앱이 작업 표시줄 쪽을 보면 사용자 의도와 어긋납니다.
    ///
    /// <para>WinPE에는 이 값이 없습니다 — 그때는 밝은 쪽입니다(부팅 USB 화면은 늘 밝게 떴고,
    /// 급한 상황에서 화면이 갑자기 달라 보이지 않는 편이 낫습니다).</para>
    /// </remarks>
    public static AppTheme FromWindows()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);

            return value is int i && i == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    /// <summary>이번 실행에 쓸 색조 — 저장된 선택이 우선, 없으면 Windows 설정.</summary>
    public static AppTheme Resolve() => Load() ?? FromWindows();
}
