using System.Globalization;
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

    /// <summary>
    /// 이번 실행의 UI 언어를 정합니다. 환경변수 <c>DM_LANG</c>(예: en, ko)가 있으면 그것을,
    /// 없으면 OS UI 언어를 씁니다 — 한국어면 한국어, 그 외는 영어. 창이 로드되기 전에
    /// 호출해야 문자열이 올바른 언어로 잡힙니다.
    /// </summary>
    private static void ApplyCulture()
    {
        string? env = Environment.GetEnvironmentVariable("DM_LANG");
        CultureInfo culture = !string.IsNullOrWhiteSpace(env)
            ? new CultureInfo(env)
            : (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                ? new CultureInfo("ko")
                : new CultureInfo("en"));

        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplyCulture();

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

        // 데이터를 지울 수 있는 도구라, 위험을 고지하고 동의를 받은 뒤에만 실행합니다.
        // 동의는 사용자별·버전별로 한 번만 받습니다(EulaAcceptance). 미동의면 창을 열지 않고 종료.
        if (!EulaAcceptance.IsAccepted())
        {
            var eulaWindow = new EulaWindow();
            if (eulaWindow.ShowDialog() != true)
            {
                Log.Information("EULA 미동의 — 실행하지 않고 종료합니다.");
                Shutdown();
                return;
            }

            EulaAcceptance.RecordAcceptance();
            Log.Information("EULA v{Version} 동의를 기록했습니다.", EulaAcceptance.Version);
        }

        var viewModel = new MainViewModel(_loggerFactory);
        var window = new MainWindow { DataContext = viewModel };

        MainWindow = window;
        window.Show();

        _ = viewModel.RefreshDisksAsync();
        _ = viewModel.CheckForUpdatesAsync();
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
