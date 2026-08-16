using System.Reflection;
using DiskMigrator.Mcp;
using DiskMigrator.Mcp.Tools;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// Claude의 안전 판정이 <b>화면과 갈리지 않는지</b> — 계획서 2단계 완료 조건.
/// </summary>
/// <remarks>
/// <b>왜 이것이 그냥 틀린 것보다 나쁜가.</b> Claude가 "안전합니다"라고 하는데 화면은 경고를
/// 띄우면, 사용자는 <b>둘 중 어느 쪽을 믿어야 할지 모릅니다.</b> 디스크를 통째로 지우는
/// 도구에서 그 혼란은 값이 비쌉니다. 하나라도 틀리는 편이 차라리 낫습니다 — 적어도 어느 쪽을
/// 고쳐야 할지는 아니까요.
///
/// <para><b>판정기는 원래부터 하나였습니다</b>(<c>SafetyGuard.Evaluate</c>). 갈릴 수 있는 곳은
/// <b>넘기는 값을 만드는 코드</b>였고, 실제로 두 벌이 있었습니다 —
/// ① 최대 절전 이미지 판정(화면은 파일만, 도구는 하이브를 거쳐)
/// ② 스냅샷 설정(화면은 사용자 선택, 도구는 늘 true 가정).
/// 2026-08-17에 둘 다 한곳으로 모았고, 이 시험이 그것을 지킵니다.</para>
/// </remarks>
public class SafetyParityTests
{
    /// <summary>
    /// 최대 절전 판정은 <see cref="DiskMigrator.Core.Registry.HibernationImage"/> 한 곳에만 있어야 합니다.
    /// </summary>
    /// <remarks>
    /// 도구가 <c>hiberfil.sys</c>를 직접 찾으면 화면과 갈릴 길이 다시 열립니다.
    /// 이름을 직접 지목해 막습니다 — 다음에 누가 "여기서 잠깐 확인만" 하고 되살리는 것을.
    /// </remarks>
    [Fact]
    public void 도구는_최대절전_판정을_스스로_하지_않는다()
    {
        string source = ToolSource("PlanningTools.cs");

        Assert.DoesNotContain("hiberfil.sys", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HibernationImage.IsPresent", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>evaluate_safety</c>는 스냅샷 설정을 <b>가정하지 않고 화면에서 읽어야</b> 합니다.
    /// </summary>
    [Fact]
    public void 스냅샷_설정은_화면에서_읽는다()
    {
        string source = ToolSource("PlanningTools.cs");

        // 인자를 안 주면 화면 값을 씁니다.
        Assert.Contains("useSnapshot ?? appState.UseSnapshot", source, StringComparison.Ordinal);

        // 예전처럼 true로 못 박아 두면 안 됩니다.
        Assert.DoesNotContain("bool useSnapshot = true", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 화면 쪽 창구(<see cref="IAppState"/>)는 설정을 <b>읽기만</b> 할 수 있어야 합니다.
    /// </summary>
    /// <remarks>
    /// 판정을 맞추자고 도구가 옵션을 <b>바꿀 수 있게</b> 되면, "Claude는 읽기만 한다"가 무너집니다.
    /// 옵션을 정하는 것은 사람입니다.
    /// </remarks>
    [Fact]
    public void 도구는_화면_설정을_바꿀_수_없다()
    {
        var property = typeof(IAppState).GetProperty(nameof(IAppState.UseSnapshot));

        Assert.NotNull(property);
        Assert.True(property!.CanRead);
        Assert.False(property.CanWrite);
    }

    /// <summary>도구 소스 파일을 읽어 옵니다(시험 실행 폴더 기준 상대 경로).</summary>
    /// <remarks>
    /// 리플렉션으로는 "무엇을 직접 계산하는지"를 볼 수 없습니다. 이 시험이 막으려는 것은
    /// <b>코드가 다시 두 벌이 되는 것</b>이라, 소스를 그대로 봐야 합니다.
    /// </remarks>
    private static string ToolSource(string fileName)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "src", "DiskMigrator.Mcp", "Tools", fileName);
        Assert.True(File.Exists(path), $"도구 소스를 찾지 못했습니다: {path}");

        return File.ReadAllText(path);
    }
}
