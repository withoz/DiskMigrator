using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskMigrator.App.Localization;
using DiskMigrator.App.ViewModels;

namespace DiskMigrator.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateLanguageToggle();
    }

    // --- 언어 전환 --------------------------------------------------------
    //
    // 헤더의 "한국어 · English"를 누르면 선택을 저장하고(App.SwitchLanguage) 창을 새 언어로
    // 다시 그립니다. XAML 문자열은 로드 시점에 언어가 잡히므로 창을 새로 만들어야 합니다.

    private void LangKo_Click(object sender, MouseButtonEventArgs e) =>
        (Application.Current as App)?.SwitchLanguage("ko");

    private void LangEn_Click(object sender, MouseButtonEventArgs e) =>
        (Application.Current as App)?.SwitchLanguage("en");

    /// <summary>현재 언어를 굵게·진하게, 나머지는 흐리게 표시합니다.</summary>
    private void UpdateLanguageToggle()
    {
        bool ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
        var active = (Brush)FindResource("TextBrush");
        var inactive = (Brush)FindResource("Muted");

        LangKo.FontWeight = ko ? FontWeights.SemiBold : FontWeights.Normal;
        LangEn.FontWeight = ko ? FontWeights.Normal : FontWeights.SemiBold;
        LangKo.Foreground = ko ? active : inactive;
        LangEn.Foreground = ko ? inactive : active;
    }

    /// <summary>
    /// 클론이 도는 중에 창을 닫으면 대상 디스크가 반쯤 쓰인 상태로 남습니다.
    /// 프로세스가 죽으면 볼륨 잠금도 함께 풀려 Windows가 깨진 파일 시스템을
    /// 마운트하려 들 수 있으므로, 사용자에게 한 번 더 묻습니다.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { Stage: AppStage.Running })
        {
            var answer = MessageBox.Show(
                Strings.Get("CloseRunningMsg1") + "\n\n" +
                Strings.Get("CloseRunningMsg2") + "\n\n" +
                Strings.Get("CloseRunningMsg3"),
                Strings.Get("CloseRunningTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    // --- 막대에서 끌어 조정 ------------------------------------------------
    //
    // 손잡이는 입력 장치일 뿐입니다. 끌린 픽셀을 바이트로 옮겨 뷰모델에 넘기면, 값은
    // 기존 배선(FreeSpacePlanner → 미리보기·시작 버튼·엔진)을 그대로 탑니다.
    // 여기서 크기를 직접 계산하거나 미리보기를 그리지 않습니다 — 그러면 화면과 엔진이 갈립니다.

    private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not FrameworkElement thumb) return;

        // 픽셀 → 막대 너비 비율 → 바이트. 환산 계수는 뷰모델이 배치에서 계산해 둡니다.
        double barWidth = FindBarWidth(thumb);
        if (barWidth <= 0) return;

        vm.NudgeResizeBytes(e.HorizontalChange / barWidth * vm.ResizeBytesPerFraction);
    }

    private void ResizeThumb_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // 막대 폭이 600px쯤이면 1픽셀이 1 GB가 넘습니다. 끌기만으로는 그 이상 정밀해질 수
        // 없으므로 키보드로 정확히 맞출 수 있게 합니다.
        long step = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0
            ? 1L << 30    // 1 GiB
            : 1L << 20;   // 1 MiB

        switch (e.Key)
        {
            case System.Windows.Input.Key.Left:
                vm.NudgeResizeBytes(-step);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Right:
                vm.NudgeResizeBytes(step);
                e.Handled = true;
                break;
        }
    }

    /// <summary>손잡이가 올라가 있는 막대의 실제 너비. 못 찾으면 0.</summary>
    private static double FindBarWidth(FrameworkElement thumb)
    {
        for (DependencyObject? node = thumb; node is not null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "BarArea" } bar) return bar.ActualWidth;
        }
        return 0;
    }
}
