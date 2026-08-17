using System.IO;
using System.Reflection;
using DiskMigrator.App;
using Xunit;

namespace DiskMigrator.App.Tests;

/// <summary>
/// 설치 프로그램과 앱이 <b>같은 약속</b>을 보고 있는지.
/// </summary>
/// <remarks>
/// 설치 마지막 화면의 [DiskMigrator-X 실행]은 <b>재시작이 필요 없을 때만</b> 나옵니다.
/// 앱이 켜진 채로 설치하면 실행 파일을 못 바꿔 교체가 다음 부팅으로 밀리고, 그 화면은
/// "컴퓨터를 다시 시작하십시오"로 바뀌며 실행 선택이 사라집니다 — 사용자에게는 <b>그 기능이
/// 없는 것</b>으로 보입니다(2026-08-17에 실제로 그렇게 보고받았습니다).
///
/// <para>그래서 앱이 "켜져 있다"는 표시를 남기고 설치 프로그램이 그것을 봅니다. 이름이 두
/// 파일에 나뉘어 있어, 한쪽만 바꾸면 <b>아무 말 없이</b> 예전 상태로 돌아갑니다 — 설치는
/// 멀쩡히 되고 마지막 화면만 조용히 달라지므로 알아채기 어렵습니다.</para>
/// </remarks>
public class InstallerHandoffTests
{
    [Fact]
    public void 설치_프로그램과_앱이_같은_이름을_본다()
    {
        string iss = InstallerScript();

        Assert.Contains($"AppMutex={App.RunningMutexName}", iss, StringComparison.Ordinal);

        // 다른 세션에서 도는 설치 프로그램도 볼 수 있어야 합니다.
        Assert.Contains($@"Global\{App.RunningMutexName}", iss, StringComparison.Ordinal);
    }

    /// <summary>마지막 화면의 실행 선택이 <b>남아 있는지</b>.</summary>
    /// <remarks>
    /// 이 줄이 사라지면 설치가 끝나도 앱을 어디서 여는지 알 수 없습니다. 시작 메뉴를 찾아
    /// 들어가야 하는데, 그 한 걸음에서 적잖은 사람이 멈춥니다.
    /// </remarks>
    [Fact]
    public void 설치가_끝나면_실행을_권한다()
    {
        string iss = InstallerScript();

        Assert.Contains("[Run]", iss, StringComparison.Ordinal);
        Assert.Contains("postinstall", iss, StringComparison.Ordinal);
        Assert.Contains("{cm:LaunchProgram", iss, StringComparison.Ordinal);
    }

    /// <summary>마지막 실행이 <b>관리자 권한으로</b> 걸리는지.</summary>
    /// <remarks>
    /// 설치 프로그램은 마지막 실행을 일부러 권한을 낮춰 겁니다. 보통 앱은 그래야 맞지만
    /// 이 앱은 디스크를 직접 여느라 관리자 권한을 요구해, 낮춘 채로는 거부됩니다
    /// (<c>CreateProcess failed; code 740</c>). 사용자에게는 <b>"체크했는데 앱이 안 뜬다"</b>로만
    /// 보입니다 — 2026-08-17 실기에서 정확히 그렇게 나왔습니다.
    /// </remarks>
    [Fact]
    public void 마지막_실행은_권한을_낮추지_않는다()
    {
        Assert.Contains("runascurrentuser", InstallerScript(), StringComparison.Ordinal);
    }

    /// <summary>파일을 붙잡은 프로그램을 닫도록 되어 있는지.</summary>
    /// <remarks>
    /// 앱에서 Claude에게 물어본 적이 있으면 <c>claude.exe</c>가 남아 중계기를 붙잡습니다.
    /// 2026-08-13에 그 파일만 갱신되지 않았고, 그 뒤 [Claude에 연결하기]가 옛 중계기를
    /// 가리켰습니다.
    /// </remarks>
    [Fact]
    public void 붙잡고_있는_프로그램을_닫는다()
    {
        Assert.Contains("CloseApplications=yes", InstallerScript(), StringComparison.Ordinal);
    }

    private static string InstallerScript()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "installer")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "installer", "DiskMigrator.iss");
        Assert.True(File.Exists(path), $"설치 스크립트를 찾지 못했습니다: {path}");

        return File.ReadAllText(path);
    }
}
