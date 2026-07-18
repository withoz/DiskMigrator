using System.ComponentModel;
using System.Windows;
using DiskMigrator.App.ViewModels;

namespace DiskMigrator.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
                "클론이 진행 중입니다.\n\n" +
                "지금 닫으면 대상 디스크는 불완전한 상태로 남고, 그 디스크의 데이터를 " +
                "사용할 수 없습니다.\n\n정말 닫으시겠습니까?",
                "작업이 진행 중입니다",
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
}
