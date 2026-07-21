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
    /// 이번 실행의 UI 언어를 정합니다. 우선순위: 저장된 사용자 선택(LanguagePreference) >
    /// 환경변수 <c>DM_LANG</c> > OS UI 언어(한국어면 ko, 그 외 en). 창이 로드되기 전에
    /// 호출해야 문자열이 올바른 언어로 잡힙니다.
    /// </summary>
    private static void ApplyCulture()
    {
        string lang = ResolveLanguage();
        var culture = new CultureInfo(lang);
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>이번 실행에 쓸 언어 코드("ko"/"en")를 우선순위에 따라 정합니다.</summary>
    private static string ResolveLanguage()
    {
        if (LanguagePreference.Load() is { } pref) return pref;

        string? env = Environment.GetEnvironmentVariable("DM_LANG");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim().StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
    }

    /// <summary>
    /// UI 언어를 바꾸고 메인 창을 새 언어로 다시 그립니다 — 재시작·UAC 없이. 선택은 저장돼
    /// 다음 실행에도 유지됩니다. XAML 문자열은 로드 시점에 언어가 잡히므로 창을 새로 만듭니다.
    /// </summary>
    public void SwitchLanguage(string lang)
    {
        LanguagePreference.Save(lang);

        // 이미 그 언어면 선택만 저장하고 다시 그리지 않습니다.
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == lang) return;

        ApplyCulture();

        // 새 창을 먼저 띄운 뒤 옛 창을 닫습니다 — 마지막 창이 아니라 앱이 꺼지지 않습니다.
        var old = MainWindow;
        ShowMainWindow();
        old?.Close();
        Log.Information("UI 언어를 {Lang}로 전환했습니다.", lang);
    }

    /// <summary>메인 창(뷰모델 포함)을 만들어 띄우고 초기 작업을 시작합니다.</summary>
    private void ShowMainWindow()
    {
        var viewModel = new MainViewModel(_loggerFactory!);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        _ = viewModel.RefreshDisksAsync();
        _ = viewModel.CheckForUpdatesAsync();
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
