using System.Windows.Media;
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
            (true, _) => ("통과", Pass),
            (false, BootCheckSeverity.Fatal) => ("실패", Fail),
            (false, _) => ("경고", Warn),
            (null, _) => ("확인 불가", Skip),
        };
    }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>배지 텍스트 (통과 / 실패 / 경고 / 확인 불가).</summary>
    public string Mark { get; }

    /// <summary>배지·텍스트 색.</summary>
    public Brush StatusBrush { get; }
}
