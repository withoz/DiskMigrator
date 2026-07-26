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
        FitToScreen();
    }

    /// <summary>화면이 이보다 좁으면 UI를 비율 축소합니다 — 설계 기준 창 크기.</summary>
    private const double DesignWidth = 1180;
    private const double DesignHeight = 840;

    /// <summary>
    /// 화면(작업 영역)보다 큰 창은 잘립니다 — 특히 부팅 USB(WinPE)는 그래픽 드라이버가 없어
    /// 1024×768 같은 낮은 해상도로 뜹니다. 창 크기만 줄이면 내용은 여전히 설계 폭을 요구해
    /// 스크롤·잘림이 남았습니다(실기에서 확인). 그래서 창을 최대화하고 <b>UI 전체를
    /// LayoutTransform으로 비율 축소</b>해, 어떤 해상도에서도 모든 내용이 화면 안에 들어가게
    /// 합니다.
    /// </summary>
    private void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;

        MinWidth = Math.Min(MinWidth, wa.Width);
        MinHeight = Math.Min(MinHeight, wa.Height);
        if (Width > wa.Width) Width = wa.Width;
        if (Height > wa.Height) Height = wa.Height;

        double scale = Math.Min(1.0, Math.Min(wa.Width / DesignWidth, wa.Height / DesignHeight));
        if (scale < 1.0)
        {
            WindowState = WindowState.Maximized;
            RootLayout.LayoutTransform = new ScaleTransform(scale, scale);
        }
    }

    // --- 언어 전환 --------------------------------------------------------
    //
    // 헤더의 "한국어 · English"를 누르면 선택을 저장하고(App.SwitchLanguage) 창을 새 언어로
    // 다시 그립니다. XAML 문자열은 로드 시점에 언어가 잡히므로 창을 새로 만들어야 합니다.

    private void LangKo_Click(object sender, MouseButtonEventArgs e) => SwitchLanguageIfIdle("ko");

    private void LangEn_Click(object sender, MouseButtonEventArgs e) => SwitchLanguageIfIdle("en");

    /// <summary>토글 비활성화(XAML)와 별개의 이중 방어 — 작업 중엔 창을 재생성하지 않습니다.</summary>
    private void SwitchLanguageIfIdle(string lang)
    {
        if (DataContext is MainViewModel { CanSwitchLanguage: false }) return;
        (Application.Current as App)?.SwitchLanguage(lang);
    }

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
        // 부팅 USB 제작(IsPeBuilding)도 같은 확인을 받습니다 — 도중에 닫으면 USB가 반쯤
        // 쓰인 채 남습니다(Stage는 Selecting이라 아래 검사만으로는 걸러지지 않음).
        if (DataContext is MainViewModel vm && (vm.Stage == AppStage.Running || vm.IsPeBuilding))
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
