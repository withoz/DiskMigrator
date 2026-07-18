using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Registry;
using DiskMigrator.Core.Safety;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Jobs;
using DiskMigrator.Windows.Snapshots;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.App.ViewModels;

/// <summary>화면이 보여줄 단계.</summary>
public enum AppStage
{
    Selecting,
    Running,
    Finished,
}

[SupportedOSPlatform("windows")]
public sealed partial class MainViewModel : ObservableObject
{
    private readonly WindowsDiskService _diskService;
    private readonly VssSnapshotProvider _snapshotProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _cts;
    private PauseController? _pause;

    public MainViewModel(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        _diskService = new WindowsDiskService(loggerFactory.CreateLogger<WindowsDiskService>());
        _snapshotProvider = new VssSnapshotProvider(loggerFactory.CreateLogger<VssSnapshotProvider>());

        IsElevated = _diskService.IsElevated;
        IsSnapshotAvailable = _snapshotProvider.IsAvailable;
        UseSnapshot = IsSnapshotAvailable;
    }

    // --- 상태 -------------------------------------------------------------

    // CanStart는 계산 속성이라 PropertyChanged만으로는 버튼이 다시 평가되지 않습니다.
    // CommunityToolkit의 RelayCommand는 WPF의 CommandManager.RequerySuggested를 듣지 않으므로,
    // CanStart에 영향을 주는 모든 속성이 StartCommand에 직접 알려야 합니다.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AppStage _stage = AppStage.Selecting;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string? _loadError;

    public bool IsElevated { get; }

    public bool IsSnapshotAvailable { get; }

    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DiskItemViewModel? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DiskItemViewModel? _selectedTarget;

    public bool HasSelection => SelectedSource is not null && SelectedTarget is not null;

    /// <summary>
    /// 원본을 스냅샷으로 읽을지. 실행 중인 디스크를 그냥 읽으면 복제 도중 파일이 바뀌어
    /// 결과물이 깨지므로 기본으로 켭니다.
    /// </summary>
    [ObservableProperty] private bool _useSnapshot;

    [ObservableProperty] private bool _zeroFillBadSectors;

    [ObservableProperty] private bool _verifyAfterClone = true;

    /// <summary>
    /// 클론 후 대상 Windows를 하드웨어 독립화(Universal Restore)할지. 시스템 디스크를
    /// 다른 PC로 옮길 때 켭니다. 표준 저장소 드라이버를 부팅 시작으로 설정해 0x7B를 예방합니다.
    /// </summary>
    [ObservableProperty] private bool _universalRestore;

    // --- 안전 점검 ---------------------------------------------------------

    public ObservableCollection<SafetyIssue> SafetyIssues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canProceed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ConfirmationPrompt))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _needsConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _confirmationText = "";

    public string ConfirmationPrompt =>
        SelectedTarget is null
            ? ""
            : $"위 디스크의 모든 데이터가 영구히 삭제됩니다. 계속하려면 대상 디스크의 모델명을 " +
              $"그대로 입력하십시오:  {SelectedTarget.Model}";

    /// <summary>
    /// 시작 버튼을 누를 수 있는지. 차단 사유가 없고, 확인이 필요하면 모델명이 정확히 입력돼야 합니다.
    /// </summary>
    public bool CanStart
    {
        get
        {
            if (!CanProceed || SelectedTarget is null) return false;
            if (Stage != AppStage.Selecting) return false;

            return !NeedsConfirmation ||
                   SafetyGuard.IsConfirmationValid(SelectedTarget.Disk, ConfirmationText);
        }
    }

    // --- 진행 상황 ---------------------------------------------------------

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressPhase = "";
    [ObservableProperty] private string _progressRegion = "";
    [ObservableProperty] private string _progressBytes = "";
    [ObservableProperty] private string _progressSpeed = "";
    [ObservableProperty] private string _progressEta = "";
    [ObservableProperty] private string _progressElapsed = "";
    [ObservableProperty] private int _badSectorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused;

    public string PauseButtonText => IsPaused ? "재개" : "일시정지";

    // --- 결과 --------------------------------------------------------------

    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private bool _resultIsSuccess;
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private string? _logFilePath;

    // --- 부팅 구성 검사 (클론 후) ------------------------------------------

    /// <summary>부팅 구성 검사 결과 항목들.</summary>
    public ObservableCollection<BootCheckItemViewModel> BootCheckItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BootCheckCommand))]
    private bool _isBootChecking;

    public bool CanBootCheck => !IsBootChecking;

    /// <summary>검사를 한 번이라도 실행해 결과 패널을 보여줄지.</summary>
    [ObservableProperty] private bool _bootCheckRan;

    [ObservableProperty] private string _bootCheckVerdict = "";

    /// <summary>판정이 긍정(부팅 준비/가능)이면 true — 색 구분용.</summary>
    [ObservableProperty] private bool _bootCheckVerdictIsGood;

    // --- 명령 --------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshDisksAsync()
    {
        IsLoading = true;
        LoadError = null;

        try
        {
            var disks = await _diskService.EnumerateDisksAsync();

            // 새로고침 후에도 같은 물리 디스크를 다시 선택해 주되, 장치 번호가 아니라
            // 신원(모델/시리얼/크기)으로 찾습니다. USB를 다시 꽂으면 번호가 바뀝니다.
            var previousSource = SelectedSource?.Disk;
            var previousTarget = SelectedTarget?.Disk;

            Disks.Clear();
            foreach (var disk in disks) Disks.Add(new DiskItemViewModel(disk));

            SelectedSource = Rematch(previousSource);
            SelectedTarget = Rematch(previousTarget);

            _logger.LogInformation("디스크 {Count}개를 찾았습니다.", disks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "디스크 목록을 읽지 못했습니다.");
            LoadError = $"디스크 목록을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            UpdateSafety();
        }
    }

    private DiskItemViewModel? Rematch(DiskInfo? previous)
    {
        if (previous is null) return null;

        return Disks.FirstOrDefault(d =>
            d.Disk.SizeBytes == previous.SizeBytes &&
            string.Equals(d.Disk.Model, previous.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Disk.SerialNumber ?? "", previous.SerialNumber ?? "", StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSelectedSourceChanged(DiskItemViewModel? value) => UpdateSafety();

    partial void OnSelectedTargetChanged(DiskItemViewModel? value)
    {
        // 대상이 바뀌면 이전 확인은 무효입니다. 사용자가 새 디스크를 다시 확인해야 합니다.
        ConfirmationText = "";
        OnPropertyChanged(nameof(ConfirmationPrompt));
        UpdateSafety();
    }

    partial void OnUseSnapshotChanged(bool value) => UpdateSafety();

    private void UpdateSafety()
    {
        SafetyIssues.Clear();
        CanProceed = false;
        NeedsConfirmation = false;

        if (SelectedSource is null || SelectedTarget is null)
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        var report = SafetyGuard.Evaluate(
            SelectedSource.Disk, SelectedTarget.Disk, IsElevated, UseSnapshot);

        // 심각한 것부터 보여줍니다 — 차단 사유가 정보 메시지에 묻히면 안 됩니다.
        foreach (var issue in report.Issues.OrderByDescending(i => i.Severity))
        {
            SafetyIssues.Add(issue);
        }

        CanProceed = report.CanProceed;
        NeedsConfirmation = report.NeedsTypedConfirmation;

        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnConfirmationTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_pause is null) return;

        if (_pause.IsPaused)
        {
            _pause.Resume();
            IsPaused = false;
            _logger.LogInformation("작업을 재개했습니다.");
        }
        else
        {
            _pause.Pause();
            IsPaused = true;
            _logger.LogInformation("작업을 일시정지했습니다.");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _logger.LogWarning("사용자가 취소를 요청했습니다.");

        // 멈춰 있으면 취소가 먹히도록 먼저 풀어 줍니다.
        _pause?.Resume();
        IsPaused = false;
        _cts?.Cancel();
    }

    [RelayCommand]
    private void BackToSelection()
    {
        Stage = AppStage.Selecting;
        ConfirmationText = "";
        ResetBootCheck();
        _ = RefreshDisksAsync();
    }

    private void ResetBootCheck()
    {
        BootCheckItems.Clear();
        BootCheckRan = false;
        BootCheckVerdict = "";
        BootCheckVerdictIsGood = false;
    }

    /// <summary>
    /// 방금 클론한 대상 디스크의 부팅 구성을 정적으로 검사합니다(실제 부팅 없이).
    /// </summary>
    /// <remarks>
    /// 클론이 끝나면 대상은 온라인·마운트 상태라 BCD·SYSTEM 하이브까지 온전히 읽을 수 있습니다.
    /// 클론으로 파티션이 바뀌었으니 대상 디스크를 새로 열거해 최신 볼륨 경로를 얻습니다.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanBootCheck))]
    private async Task BootCheckAsync()
    {
        if (SelectedTarget is null) return;

        IsBootChecking = true;
        ResetBootCheck();

        try
        {
            var previousTarget = SelectedTarget.Disk;
            var disks = await _diskService.EnumerateDisksAsync();
            var target = disks.FirstOrDefault(d =>
                             d.SizeBytes == previousTarget.SizeBytes &&
                             string.Equals(d.Model, previousTarget.Model, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(d.SerialNumber ?? "", previousTarget.SerialNumber ?? "", StringComparison.OrdinalIgnoreCase))
                         ?? disks.FirstOrDefault(d => d.DeviceNumber == previousTarget.DeviceNumber);

            if (target is null)
            {
                BootCheckVerdict = "대상 디스크를 다시 찾지 못했습니다.";
                BootCheckVerdictIsGood = false;
                BootCheckRan = true;
                return;
            }

            // 파일·레지스트리 I/O가 있으므로 UI 스레드를 막지 않게 백그라운드에서 실행합니다.
            var report = await Task.Run(() => BootReadinessCheck.InspectDisk(target));

            foreach (var item in report.Items)
                BootCheckItems.Add(new BootCheckItemViewModel(item));

            bool anyFatalFailed = report.Items.Any(i =>
                i.Severity == BootCheckSeverity.Fatal && i.Passed == false);

            (BootCheckVerdict, BootCheckVerdictIsGood) =
                (report.WouldBoot, report.HasWarnings, anyFatalFailed) switch
                {
                    (true, false, _) => ("부팅 준비 완료 — 치명 항목 모두 통과", true),
                    (true, true, _) => ("부팅 가능하나 경고 있음 — 아래 경고 항목을 확인하세요", true),
                    (false, _, true) => ("부팅 불가 위험 — 치명 항목이 실패했습니다", false),
                    _ => ("판정 불가 — 치명 항목을 확인하지 못했습니다 (대상이 온라인·마운트 상태인지 확인)", false),
                };

            BootCheckRan = true;
            _logger.LogInformation("부팅 구성 검사: {Verdict}", BootCheckVerdict);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 구성 검사에 실패했습니다.");
            BootCheckVerdict = $"검사에 실패했습니다: {ex.Message}";
            BootCheckVerdictIsGood = false;
            BootCheckRan = true;
        }
        finally
        {
            IsBootChecking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (SelectedSource is null || SelectedTarget is null) return;

        var source = SelectedSource.Disk;
        var target = SelectedTarget.Disk;

        _cts = new CancellationTokenSource();
        _pause = new PauseController();

        Stage = AppStage.Running;
        IsPaused = false;
        ResetBootCheck();
        BadSectorCount = 0;
        ProgressPercent = 0;
        ProgressPhase = "준비 중";
        ProgressRegion = UseSnapshot ? "스냅샷 생성 중... (최대 수십 초)" : "대상 볼륨 잠금 중...";
        ProgressBytes = ProgressSpeed = ProgressEta = ProgressElapsed = "";

        var options = new CloneOptions
        {
            BadSectorPolicy = ZeroFillBadSectors
                ? BadSectorPolicy.ZeroFillAndContinue
                : BadSectorPolicy.Abort,
            VerifyAfterClone = VerifyAfterClone,
        };

        // Progress<T>는 생성한 스레드(UI)의 컨텍스트로 콜백을 돌려주므로 별도 디스패치가 필요 없습니다.
        var progress = new Progress<CloneProgress>(OnProgress);

        try
        {
            var orchestrator = new CloneOrchestrator(_diskService, _snapshotProvider, _loggerFactory);

            var report = await orchestrator.RunAsync(
                source, target, UseSnapshot, options, UniversalRestore,
                progress, _pause, _cts.Token);

            ShowResult(report);
        }
        catch (SafetyViolationException ex)
        {
            _logger.LogError(ex, "안전 검사에 걸려 작업이 중단되었습니다.");
            ShowFailure("안전 검사에 걸려 중단했습니다", ex.Message,
                "대상 디스크에는 아무것도 쓰지 않았습니다.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "권한 부족으로 작업이 실패했습니다.");
            ShowFailure("권한이 부족합니다", ex.Message,
                "프로그램을 관리자 권한으로 다시 실행하십시오.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "작업이 실패했습니다.");
            ShowFailure("작업이 실패했습니다", ex.Message,
                "자세한 내용은 로그 파일을 확인하십시오.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pause?.Dispose();
            _pause = null;
            Stage = AppStage.Finished;
        }
    }

    private void OnProgress(CloneProgress p)
    {
        ProgressPercent = p.Percent;
        ProgressPhase = p.Phase;
        ProgressRegion = p.CurrentRegion;
        ProgressBytes = $"{SizeFormatter.Format(p.BytesProcessed)} / {SizeFormatter.Format(p.TotalBytes)}";
        ProgressSpeed = SizeFormatter.FormatSpeed(p.SpeedBytesPerSecond);
        ProgressEta = p.Eta is { } eta ? SizeFormatter.FormatDuration(eta) : "계산 중...";
        ProgressElapsed = SizeFormatter.FormatDuration(p.Elapsed);
        BadSectorCount = p.BadSectorCount;
    }

    private void ShowResult(CloneJobReport report)
    {
        var result = report.Result;
        var details = new List<string>();

        details.Add($"복사한 데이터: {SizeFormatter.Format(result.BytesCopied)}");
        details.Add($"소요 시간: {SizeFormatter.FormatDuration(result.Duration)}");
        details.Add($"평균 속도: {SizeFormatter.FormatSpeed(result.AverageSpeedBytesPerSecond)}");

        if (report.SnapshotTimeUtc is { } snapshotTime)
        {
            details.Add($"스냅샷 시점: {snapshotTime.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                        "(이 시점 이후의 변경 사항은 복제되지 않았습니다)");
        }

        if (report.UnsnapshottedPartitions.Count > 0)
        {
            details.Add($"스냅샷 없이 원시 복사한 파티션: {string.Join(", ", report.UnsnapshottedPartitions)}");
        }

        details.Add(result.VerificationPassed switch
        {
            true => "검증: 통과 — 원본과 대상이 일치합니다.",
            false => "검증: 실패 — 원본과 대상이 일치하지 않습니다.",
            null => "검증: 수행하지 않음",
        });

        if (result.BadSectors.Count > 0)
        {
            details.Add($"불량 섹터: {result.BadSectors.Count}개를 0으로 채웠습니다. " +
                        "해당 위치의 파일은 손상되었을 수 있습니다.");
        }

        if (report.GptRepair is { } gpt)
        {
            details.Add($"GPT: {gpt.Description}");
        }

        if (report.UniversalRestore is { } ur)
        {
            details.Add($"새 하드웨어 대비: {ur.Message}");
        }

        ResultDetails = string.Join("\n", details);
        ResultIsSuccess = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;

        (ResultTitle, ResultMessage) = result.Outcome switch
        {
            CloneOutcome.Completed => (
                "클론이 완료되었습니다",
                $"[{report.Target.DeviceNumber}] {report.Target.Model} 이(가) 원본의 정확한 사본이 되었습니다."),

            CloneOutcome.CompletedWithBadSectors => (
                "클론이 완료되었지만 불량 섹터가 있었습니다",
                $"원본에서 읽지 못한 섹터 {result.BadSectors.Count}개는 0으로 채웠습니다. " +
                "대부분의 파일은 정상이지만 일부가 손상되었을 수 있으니 중요한 데이터를 확인하십시오."),

            CloneOutcome.Cancelled => (
                "작업이 취소되었습니다",
                $"[{report.Target.DeviceNumber}] {report.Target.Model} 은(는) 불완전한 상태입니다. " +
                "이 디스크로 부팅하거나 데이터를 사용하지 마십시오."),

            _ => (
                "작업이 실패했습니다",
                result.ErrorMessage ?? "알 수 없는 오류입니다."),
        };
    }

    private void ShowFailure(string title, string message, string details)
    {
        ResultIsSuccess = false;
        ResultTitle = title;
        ResultMessage = message;
        ResultDetails = details;
    }
}
