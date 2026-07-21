using System.Windows;

namespace DiskMigrator.App;

/// <summary>
/// 최초 실행 시 EULA를 보여주고 동의를 받는 창.
/// </summary>
/// <remarks>
/// <see cref="Window.ShowDialog"/>로 모달로 띄우고, 반환된 <see cref="Window.DialogResult"/>가
/// <c>true</c>이면 동의, 그 외(닫기·종료 버튼)면 미동의입니다. 동의 버튼은 확인 체크박스가
/// 켜져야만 활성화됩니다(XAML 바인딩).
/// </remarks>
public partial class EulaWindow : Window
{
    public EulaWindow()
    {
        InitializeComponent();
        EulaText.Text = EulaAcceptance.LoadText();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
