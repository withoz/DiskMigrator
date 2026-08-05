using System.Windows;
using System.Windows.Controls;

namespace DiskMigrator.App.Runtime;

/// <summary>
/// 선택된 항목을 목록 안에서 보이는 자리로 끌어옵니다.
/// </summary>
/// <remarks>
/// 사용자가 직접 클릭할 때는 필요 없던 기능입니다 — 클릭한 항목은 이미 보이니까요. 그런데
/// Claude의 제안을 [적용]하면 <b>코드가 선택을 바꿉니다.</b> WPF는 그때 목록을 움직이지 않으므로,
/// 디스크가 여러 개면 선택된 항목이 목록 밖에 남습니다.
///
/// <para>확인 카드를 두 겹으로 만든 이유가 "무엇에 동의했는지 눈으로 확인하게 하는 것"인데,
/// 정작 적용 결과가 화면 밖에 있으면 그 취지가 무너집니다. 아무것도 선택되지 않은 것처럼
/// 보이는 화면에서 검사·시작 버튼을 누르게 되는 것은 위험합니다.</para>
/// </remarks>
public static class SelectionVisibility
{
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.RegisterAttached(
            "AutoScroll", typeof(bool), typeof(SelectionVisibility),
            new PropertyMetadata(false, OnAutoScrollChanged));

    public static void SetAutoScroll(DependencyObject o, bool value) => o.SetValue(AutoScrollProperty, value);

    public static bool GetAutoScroll(DependencyObject o) => (bool)o.GetValue(AutoScrollProperty);

    private static void OnAutoScrollChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ListBox list) return;

        list.SelectionChanged -= OnSelectionChanged;
        list.Loaded -= OnLoaded;

        if (e.NewValue is true)
        {
            list.SelectionChanged += OnSelectionChanged;

            // 탭을 바꾸면서 목록이 새로 만들어지는 경우, 그때는 이미 선택이 들어가 있어
            // SelectionChanged가 오지 않습니다. 만들어진 뒤에도 한 번 맞춰 줍니다.
            list.Loaded += OnLoaded;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Scroll(sender);

    private static void OnLoaded(object sender, RoutedEventArgs e) => Scroll(sender);

    private static void Scroll(object sender)
    {
        if (sender is not ListBox { SelectedItem: { } item } list) return;

        // 항목 컨테이너가 아직 만들어지지 않았을 수 있으므로 배치가 끝난 뒤로 미룹니다.
        list.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (list.Items.Contains(item)) list.ScrollIntoView(item);
            }));
    }
}
