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

    // 수동 스플래시 — 최소 노출 시간을 보장하기 위해 빌드 액션 대신 직접 띄우고 닫습니다.
    private SplashScreen? _splash;
    private DateTime _splashShownAt;
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromSeconds(1.5);

    /// <summary>이번 실행의 로그 파일 경로. 결과 화면에서 사용자에게 보여줍니다.</summary>
    public static string LogDirectory { get; } = AppIdentity.LogDirectory;

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
    /// <remarks>
    /// 저장된 선택·DM_LANG이 없으면 <b>영어가 기본</b>입니다(전 세계 배포 기준). 예전에는 OS
    /// 언어를 따랐지만, 제품 기본을 영어로 정하면서 명시적 선택만 한국어를 씁니다 — 헤더의
    /// 한국어·English 토글로 바꾸면 그 선택이 저장되어 유지됩니다.
    /// </remarks>
    private static string ResolveLanguage()
    {
        if (LanguagePreference.Load() is { } pref) return pref;

        string? env = Environment.GetEnvironmentVariable("DM_LANG");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim().StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";

        return "en";
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

        // 누가 Claude에 들어와 있는지 — 머리말에 조용히 표시합니다. 못 읽어도 앱은 그대로
        // 동작합니다(이 앱은 디스크 도구이고, Claude는 막혔을 때 부르는 사람입니다).
        _ = viewModel.RefreshClaudeAuthAsync();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 스플래시를 가능한 한 일찍 — 메인 창이 뜬 뒤에도 최소 노출 시간을 채우고 사라집니다.
        _splash = new SplashScreen("Resources/splash.png");
        _splash.Show(autoClose: false, topMost: true);
        _splashShownAt = DateTime.UtcNow;

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

        // 제품명과 버전을 함께 남깁니다 — 사용자가 로그를 보내왔을 때 어느 앱의 어느 빌드인지
        // 특정할 수 없으면 이미 고친 결함을 다시 쫓게 됩니다. 수동 버전과 함께 설치되므로
        // 제품명이 특히 중요합니다.
        var asmVer = typeof(App).Assembly.GetName().Version;
        Log.Information("=== " + AppIdentity.ProductName + " v{Version} 시작 (OS {Os}, {Bits}bit) ===",
            asmVer is null ? "?" : $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}",
            Environment.OSVersion.VersionString,
            Environment.Is64BitProcess ? 64 : 32);

        // 처리하지 못한 예외가 앱을 조용히 죽이면 사용자는 디스크 상태를 알 수 없게 됩니다.
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "처리되지 않은 도메인 예외");

        // 데이터를 지울 수 있는 도구라, 위험을 고지하고 동의를 받은 뒤에만 실행합니다.
        // 동의는 사용자별·버전별로 한 번만 받습니다(EulaAcceptance). 미동의면 창을 열지 않고 종료.
        //
        // 부팅 USB(WinPE) 안에서는 묻지 않습니다 — 그 USB는 이 앱으로 동의를 마친 사용자가
        // 직접 만든 것이고, 램디스크라 동의를 기록해도 부팅마다 사라져 같은 질문만 반복됩니다.
        if (!DiskMigrator.Windows.Pe.WinPeEnvironment.IsWinPe && !EulaAcceptance.IsAccepted())
        {
            // 스플래시가 topMost라 동의 창을 가리므로 먼저 닫습니다(첫 실행 한정).
            CloseSplash(TimeSpan.Zero);
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

        try
        {
            var viewModel = new MainViewModel(_loggerFactory);
            var window = new MainWindow { DataContext = viewModel };

            MainWindow = window;
            window.Show();

            CloseSplashAfterMinimum();

            _ = viewModel.RefreshDisksAsync();
            _ = viewModel.CheckForUpdatesAsync();
            _ = viewModel.RefreshClaudeAuthAsync();
        }
        catch
        {
            // 시작 중 실패해도 스플래시는 반드시 걷습니다 — topMost라 그대로 두면 오류 대화상자
            // 까지 가려, 사용자는 아무 설명 없이 멈춘 로고만 보게 됩니다.
            CloseSplash(TimeSpan.Zero);
            throw;
        }
    }

    /// <summary>스플래시를 즉시 또는 페이드로 닫습니다(없으면 무시).</summary>
    private void CloseSplash(TimeSpan fade)
    {
        var splash = _splash;
        _splash = null;
        splash?.Close(fade);
    }

    /// <summary>
    /// 메인 창이 뜬 뒤에도 스플래시를 최소 노출 시간까지 유지했다가 부드럽게 닫습니다 —
    /// 빠른 PC에서 스플래시가 한 순간에 지나가 버리는 것을 막습니다.
    /// </summary>
    private void CloseSplashAfterMinimum()
    {
        if (_splash is null) return;

        var remaining = MinimumSplashTime - (DateTime.UtcNow - _splashShownAt);
        if (remaining <= TimeSpan.Zero)
        {
            CloseSplash(TimeSpan.FromMilliseconds(400));
            return;
        }

        var timer = new DispatcherTimer { Interval = remaining };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CloseSplash(TimeSpan.FromMilliseconds(400));
        };
        timer.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "처리되지 않은 UI 예외");

        // 아직 스플래시가 떠 있으면(시작 도중 실패) 먼저 걷습니다 — topMost라 아래 오류 창을
        // 가려 버려, 사용자에게는 설명 없이 멈춘 로고만 보이게 됩니다.
        CloseSplash(TimeSpan.Zero);

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
        // Claude 연결 통로를 닫습니다 — 앱이 사라졌는데 포트만 잡혀 있으면 안 됩니다.
        // 종료 경로라 실패해도 계속 진행합니다.
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
        {
            try { vm.ShutdownMcpAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warning(ex, "Claude 연결 통로 정리 중 오류"); }
        }

        Log.Information("=== " + AppIdentity.ProductName + " 종료 (코드 {Code}) ===", e.ApplicationExitCode);
        Log.CloseAndFlush();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
