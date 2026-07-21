using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace DiskMigrator.App.Localization;

/// <summary>
/// UI 문자열을 문화권에 맞춰 조회합니다. 중립 리소스(Strings.resx)는 한국어이고,
/// Strings.en.resx가 영어입니다. <see cref="CultureInfo.CurrentUICulture"/>가 en이면 영어,
/// 그 외(또는 리소스 누락)면 한국어로 되돌아갑니다. 언어는 앱 시작 시 한 번 정해집니다.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("DiskMigrator.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
        => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}

/// <summary>
/// XAML에서 <c>Text="{loc:Loc SelectDisks}"</c> 처럼 문자열을 가져오는 마크업 확장.
/// 로드 시점에 <see cref="Strings.Get"/>로 해석하므로, 시작 시 정해진 언어를 따릅니다.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => Strings.Get(Key);
}
