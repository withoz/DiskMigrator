using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiskMigrator.App.ViewModels;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace DiskMigrator.App;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;

    /// <summary>이번 실행의 로그 파일 경로. 결과 화면에서 사용자에게 보여줍니다.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMigrator", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(LogDirectory);

        // 이 부류의 도구는 사고가 났을 때 "무슨 일이 있었는지"를 재구성할 수 있어야 합니다.
        // 로그는 사용자가 문제를 신고할 때 유일한 증거입니다.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDirectory, "diskmigrator-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _loggerFactory = new SerilogLoggerFactory(Log.Logger);

        Log.Information("=== DiskMigrator 시작 ===");

        // 처리하지 못한 예외가 앱을 조용히 죽이면 사용자는 디스크 상태를 알 수 없게 됩니다.
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "처리되지 않은 도메인 예외");

        var viewModel = new MainViewModel(_loggerFactory);
        var window = new MainWindow { DataContext = viewModel };

        MainWindow = window;
        window.Show();

        _ = viewModel.RefreshDisksAsync();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "처리되지 않은 UI 예외");

        MessageBox.Show(
            $"예기치 않은 오류가 발생했습니다:\n\n{e.Exception.Message}\n\n" +
            $"진행 중이던 클론 작업이 있었다면 대상 디스크는 불완전한 상태일 수 있습니다.\n\n" +
            $"로그: {LogDirectory}",
            "DiskMigrator 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== DiskMigrator 종료 (코드 {Code}) ===", e.ApplicationExitCode);
        Log.CloseAndFlush();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
