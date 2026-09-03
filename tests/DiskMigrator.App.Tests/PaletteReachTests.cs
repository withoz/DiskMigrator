using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 색이 <b>팔레트 밖에 박혀 있지 않은지</b> — 화면 코드 전수.
/// </summary>
/// <remarks>
/// <b>왜 XAML만으로는 부족한가.</b> 어두운 화면을 만들 때 XAML의 색은 전부 이름으로 모았지만,
/// <b>코드가 직접 만드는 브러시</b>는 그대로 남아 있었습니다(2026-08-17 전수 확인에서 발견).
/// 두 자리가 나왔고 둘 다 눈에 잘 안 띄는 곳이었습니다.
///
/// <list type="bullet">
/// <item>부팅 검사 배지 — "테마 색과 맞춥니다"라는 주석과 함께 값을 <b>베껴 적어</b> 두었습니다.
/// 맞춘 것이 아니라 복사한 것이라, 팔레트가 바뀌어도 따라오지 않았습니다.</item>
/// <item>부팅 USB의 파일 선택 창 — 배경만 밝은 색으로 박아 두었는데 글자는 팔레트를 따라
/// 밝아져, 어두운 모드에서 <b>흰 바탕에 흰 글자</b>가 됐습니다. 하필 부팅이 안 되는 컴퓨터를
/// 살리러 온 사람이 여는 창입니다.</item>
/// </list>
///
/// <para>파티션 조각 색(EFI·데이터 등)만 예외입니다 — 그 색은 <b>종류를 뜻하므로</b> 두 모드에서
/// 같아야 합니다. 예외는 파일 단위로 좁게 둡니다.</para>
/// </remarks>
public class PaletteReachTests
{
    /// <summary>파티션 막대의 조각 색만 값을 직접 갖습니다(종류를 뜻하는 색).</summary>
    private static readonly string[] Allowed = ["DiskLayoutViewModel.cs"];

    [Fact]
    public void 화면_코드가_색을_직접_만들지_않는다()
    {
        var offenders = new List<string>();

        foreach (string file in Directory.GetFiles(AppRoot(), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetFileName(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (Allowed.Contains(rel)) continue;

            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Color\.FromRgb\([^)]*\)|ColorConverter\.ConvertFromString\(""#[0-9A-Fa-f]+""\)"))
                offenders.Add($"{rel}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "팔레트 밖에서 색을 만들고 있습니다(어두운 화면에서 그 자리만 남습니다):\n  " +
            string.Join("\n  ", offenders));
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
