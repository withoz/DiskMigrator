using System.Windows.Media;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Registry;

namespace DiskMigrator.App.ViewModels;

/// <summary>부팅 구성 검사 결과 한 항목을 화면용으로 감쌉니다.</summary>
public sealed class BootCheckItemViewModel
{
    // 테마 색과 맞춥니다 (App.xaml: Success/Danger/Warning/Muted).
    private static readonly SolidColorBrush Pass = new(Color.FromRgb(0x05, 0x96, 0x69));
    private static readonly SolidColorBrush Fail = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Skip = new(Color.FromRgb(0x6B, 0x72, 0x80));

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
