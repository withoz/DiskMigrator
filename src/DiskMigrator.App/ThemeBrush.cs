using System.Windows;
using System.Windows.Media;

namespace DiskMigrator.App;

/// <summary>
/// 코드에서 색이 필요할 때 <b>지금 끼워져 있는 팔레트</b>에서 꺼내 씁니다.
/// </summary>
/// <remarks>
/// 색을 코드에 적어 두면 팔레트를 갈아 끼워도 그 자리만 옛 색으로 남습니다 — 어두운 화면에
/// 흰 조각이 박힌 것처럼 보이고, 대개 만든 사람은 밝은 쪽만 보므로 알아채지 못합니다.
///
/// <para><b>결과를 static으로 담아 두지 마십시오.</b> 한 번 꺼내 필드에 굳히면 색조를 바꿔도
/// 그 값이 그대로 남습니다. 필요할 때마다 부르는 편이 맞습니다(사전 조회는 값싸고,
/// 이 색들은 화면을 그릴 때만 쓰입니다).</para>
/// </remarks>
public static class ThemeBrush
{
    /// <summary>팔레트에서 브러시를 꺼냅니다. 없으면 눈에 띄는 색으로 돌려줍니다.</summary>
    /// <remarks>
    /// 없는 이름을 조용히 투명이나 검정으로 넘기면 <b>안 보이는 채로 배포됩니다.</b>
    /// 자홍색은 화면에서 즉시 눈에 띄어, 이름을 빠뜨렸다는 것을 개발 중에 알게 합니다.
    /// (두 팔레트의 이름이 어긋나는 것 자체는 `ThemeParityTests`가 먼저 막습니다.)
    /// </remarks>
    public static Brush Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        return Brushes.Magenta;
    }
}
