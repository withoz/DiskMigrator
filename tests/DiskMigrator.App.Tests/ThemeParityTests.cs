using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 밝은 팔레트와 어두운 팔레트가 <b>같은 이름을 갖고 있는지</b>.
/// </summary>
/// <remarks>
/// <b>왜 이것이 위험한가.</b> 화면은 색을 이름으로 찾습니다. 어두운 쪽에 이름 하나가 없으면
/// 그 이름을 쓰는 화면이 <b>뜨는 순간</b> 앱이 죽습니다 — 컴파일은 통과하고, 밝은 모드에서는
/// 아무 문제가 없으며, 만든 사람은 대개 밝은 쪽만 보므로 그대로 배포됩니다.
///
/// <para>게다가 화면 전체가 아니라 <b>그 색을 쓰는 화면</b>만 죽습니다. 부팅 복구 탭에서만
/// 쓰는 색이 빠져 있으면, 부팅이 안 되는 컴퓨터를 살리러 온 사람 앞에서 처음 터집니다.</para>
///
/// <para>색 <b>값</b>은 당연히 달라야 하므로 보지 않습니다. 이름만 맞댑니다.</para>
/// </remarks>
public class ThemeParityTests
{
    [Fact]
    public void 두_팔레트가_같은_이름을_갖는다()
    {
        var light = KeysIn("Light.xaml");
        var dark = KeysIn("Dark.xaml");

        Assert.NotEmpty(light);

        var missingInDark = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var missingInLight = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(missingInDark.Length == 0,
            "어두운 팔레트에 없는 이름: " + string.Join(", ", missingInDark));
        Assert.True(missingInLight.Length == 0,
            "밝은 팔레트에 없는 이름: " + string.Join(", ", missingInLight));
    }

    /// <summary>
    /// 화면과 코드가 <b>실제로 쓰는 이름이 팔레트에 다 있는지</b>.
    /// </summary>
    /// <remarks>
    /// 위 시험은 두 팔레트끼리만 맞댑니다 — <b>양쪽 모두에서</b> 빠진 이름은 못 잡습니다.
    /// 그것도 같은 방식으로 앱을 죽입니다(오타 하나면 충분합니다).
    /// </remarks>
    [Fact]
    public void 화면이_찾는_이름이_모두_팔레트에_있다()
    {
        var defined = KeysIn("Light.xaml");

        // 팔레트 밖에서 정의되는 것들(글꼴·변환기·스타일)은 이 시험의 대상이 아닙니다.
        var elsewhere = NamesDefinedOutsidePalette();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in new[] { "MainWindow.xaml", "App.xaml", "EulaWindow.xaml" })
        {
            string text = File.ReadAllText(Path.Combine(AppRoot(), file));
            foreach (Match m in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_]+)\}"))
                used.Add(m.Groups[1].Value);
        }

        // 코드에서 꺼내 쓰는 것도 같은 팔레트를 봅니다.
        foreach (string file in Directory.GetFiles(AppRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"ThemeBrush\.Get\(""([A-Za-z0-9_]+)""\)"))
                used.Add(m.Groups[1].Value);
        }

        var unknown = used.Except(defined).Except(elsewhere)
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(unknown.Length == 0,
            "어디에도 정의되지 않은 이름: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// 앱의 스타일이 색을 <b>DynamicResource</b>로 잡는지.
    /// </summary>
    /// <remarks>
    /// <c>StaticResource</c>로 두면 App.xaml을 읽는 순간 값이 굳습니다. 그러면 팔레트를 갈아
    /// 끼워도 스타일은 옛 색을 쥔 채라, 어두운 모드에서 <b>글자만 검게</b> 남아 안 보이게 됩니다.
    /// 조용히 되돌아가기 쉬운 자리라 못 박아 둡니다.
    /// </remarks>
    [Fact]
    public void 앱_스타일은_팔레트를_따라간다()
    {
        string app = File.ReadAllText(Path.Combine(AppRoot(), "App.xaml"));
        var palette = KeysIn("Light.xaml");

        var stuck = Regex.Matches(app, @"\{StaticResource\s+([A-Za-z0-9_]+)\}")
            .Select(m => m.Groups[1].Value)
            .Where(palette.Contains)
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(stuck.Length == 0,
            "App.xaml에서 색이 굳어 있습니다(DynamicResource여야 합니다): " + string.Join(", ", stuck));
    }

    // --- 읽기 도구 ------------------------------------------------------------

    private static HashSet<string> KeysIn(string paletteFile)
    {
        var doc = XDocument.Load(Path.Combine(AppRoot(), "Themes", paletteFile));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        return new HashSet<string>(
            doc.Root!.Elements()
                .Select(e => (string?)e.Attribute(x + "Key"))
                .Where(k => k is not null)!,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 팔레트 밖에서 정의되는 이름 — 글꼴·변환기·스타일·템플릿.
    /// </summary>
    /// <remarks>
    /// App.xaml뿐 아니라 <b>각 창이 스스로 가진</b> 자원(<c>Window.Resources</c>)도 봅니다.
    /// 공용 부품은 App.xaml에, 그 창에서만 쓰는 것은 창 안에 두는 구조라 한쪽만 보면
    /// 멀쩡한 이름을 "없다"고 합니다.
    /// </remarks>
    private static HashSet<string> NamesDefinedOutsidePalette()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            // 기본 스타일을 이어받을 때 쓰는 형식 참조({x:Type Button} 등)는 이름이 아닙니다.
            "Button",
        };

        foreach (string file in new[] { "App.xaml", "MainWindow.xaml", "EulaWindow.xaml" })
        {
            string text = File.ReadAllText(Path.Combine(AppRoot(), file));
            foreach (Match m in Regex.Matches(text, @"x:Key=""([A-Za-z0-9_]+)"""))
                names.Add(m.Groups[1].Value);
        }

        return names;
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "DiskMigrator.App");
    }
}
