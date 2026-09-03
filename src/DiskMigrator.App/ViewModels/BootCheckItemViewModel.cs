using System.Windows.Media;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Registry;

namespace DiskMigrator.App.ViewModels;

/// <summary>부팅 구성 검사 결과 한 항목을 화면용으로 감쌉니다.</summary>
public sealed class BootCheckItemViewModel
{
    // ⚠ 예전에는 색을 여기 적어 두고 "테마 색과 맞춥니다"라고 주석만 달아 두었습니다.
    //   맞춘 것이 아니라 <b>베껴 적은 것</b>이라, 팔레트가 어두운 쪽으로 바뀌어도 이 네 개는
    //   그대로 남았습니다 — 특히 실패(#DC2626)는 어두운 바탕에서 가라앉아 덜 위험해 보입니다.
    //   부팅 검사 결과에서 무엇이 실패인지가 가장 중요한데 그것이 흐려지는 자리였습니다.
    //
    //   static으로 담아 두지 않습니다. 색조를 바꾸면 창이 새로 그려지고 이 항목들도 다시
    //   만들어지므로, 그때 팔레트에서 꺼내면 맞는 색이 됩니다.
    private static Brush Pass => ThemeBrush.Get("Success");
    private static Brush Fail => ThemeBrush.Get("SeverityBlocker");
    private static Brush Warn => ThemeBrush.Get("SeverityWarn");
    private static Brush Skip => ThemeBrush.Get("SeverityInfo");

    public BootCheckItemViewModel(BootCheckItem item)
    {
        Name = item.Name;
        Detail = item.Detail;

        (Mark, StatusBrush) = (item.Passed, item.Severity) switch
        {
            // 배지 문구도 화면 언어를 따라야 합니다 — 영어로 쓰는 사용자에게 이 네 개만
            // 한국어로 남아 있었습니다. 같은 파일의 PeFileDialog 방식(L.T)을 따릅니다.
            (true, _) => (L.T("통과", "Pass"), Pass),
            (false, BootCheckSeverity.Fatal) => (L.T("실패", "Fail"), Fail),
            (false, _) => (L.T("경고", "Warning"), Warn),
            (null, _) => (L.T("확인 불가", "Not verified"), Skip),
        };
    }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>배지 텍스트 (통과 / 실패 / 경고 / 확인 불가).</summary>
    public string Mark { get; }

    /// <summary>배지·텍스트 색.</summary>
    public Brush StatusBrush { get; }
}
