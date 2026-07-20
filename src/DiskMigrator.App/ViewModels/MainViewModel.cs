using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
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
    /// 빈 영역을 건너뛰고 사용 중인 블록만 복사할지(스마트 클론). 실데이터가 적으면 크게
    /// 빨라집니다. 스냅샷 모드에서만 효과가 있습니다.
    /// </summary>
    [ObservableProperty] private bool _skipUnusedBlocks;

    /// <summary>
    /// 클론 후 대상 Windows를 하드웨어 독립화(Universal Restore)할지. 시스템 디스크를
    /// 다른 PC로 옮길 때 켭니다. 표준 저장소 드라이버를 부팅 시작으로 설정해 0x7B를 예방합니다.
    /// </summary>
    [ObservableProperty] private bool _universalRestore;

    // --- 남는 공간 처리 ----------------------------------------------------
    //
    // 세 방법은 같은 질문("남는 공간을 누구에게?")의 답이라 배타적입니다. 예전에는 별개
    // 체크박스라 둘 다 켤 수 있었고, 그러면 하나가 조용히 무시됐습니다.

    /// <summary>확대할 후보 파티션 목록(원본의 NTFS 파티션).</summary>
    public ObservableCollection<PartitionChoiceViewModel> ResizablePartitions { get; } = [];

    /// <summary>선택한 원본/대상의 파티션 배치 막대. 고른 디스크가 없으면 null(화면에서 숨김).</summary>
    [ObservableProperty] private DiskLayoutViewModel? _sourceLayout;

    [ObservableProperty] private DiskLayoutViewModel? _targetLayout;

    /// <summary>
    /// 복제가 끝난 뒤 대상이 어떤 배치가 될지. 위 대상 막대("지금 지워질 것")와 시점이 달라
    /// 따로 그립니다.
    /// </summary>
    [ObservableProperty] private DiskLayoutViewModel? _targetAfterLayout;

    /// <summary>남는 공간을 미할당으로 남길지(기본).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrowDetails))]
    private bool _freeSpaceLeave = true;

    /// <summary>남는 공간을 마지막 파티션에 합칠지.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrowDetails))]
    private bool _freeSpaceExpandLast;

    /// <summary>고른 파티션을 넓힐지. GPT 원본이고 대상이 더 클 때만 고를 수 있습니다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrowDetails))]
    private bool _freeSpaceGrowPartition;

    /// <summary>'고른 파티션 넓히기'를 골랐을 때만 파티션·크기 입력을 보여줍니다.</summary>
    public bool ShowGrowDetails => FreeSpaceGrowPartition;

    [ObservableProperty] private PartitionChoiceViewModel? _selectedResizePartition;

    /// <summary>true면 남는 공간을 전부 확대 파티션에, false면 <see cref="ResizeSizeGb"/> 크기로.</summary>
    [ObservableProperty] private bool _resizeFillRemaining = true;

    [ObservableProperty] private string _resizeSizeGb = "";

    /// <summary>
    /// 대상이 원본보다 커서 확대할 여지가 있는지. 리사이즈는 <b>GPT 원본</b>만, 그리고 원본과
    /// 대상의 <b>논리 섹터 크기가 같을 때</b>만 지원합니다(GPT 엔트리 위치가 LBA 단위라
    /// 섹터 크기가 다르면 재배치가 어긋납니다).
    /// </summary>
    public bool CanResize =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedSource.Disk.PartitionStyle == PartitionStyle.Gpt &&
        SelectedSource.Disk.LogicalSectorSize == SelectedTarget.Disk.LogicalSectorSize &&
        SelectedTarget.Disk.SizeBytes > SelectedSource.Disk.SizeBytes &&
        ResizablePartitions.Count > 0;

    /// <summary>리사이즈로 확대한 파티션 번호(클론 후 "파티션 확장" 버튼이 이 파티션을 넓힘). 없으면 null.</summary>
    private int? _grownPartitionNumber;

    // --- 안전 점검 ---------------------------------------------------------

    public ObservableCollection<SafetyIssue> SafetyIssues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(BlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canProceed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ConfirmationPrompt))]
    [NotifyPropertyChangedFor(nameof(BlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _needsConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(BlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _confirmationText = "";

    public string ConfirmationPrompt =>
        SelectedTarget is null
            ? ""
            : $"계속하려면 대상 디스크의 모델명을 그대로 입력하십시오 — {SelectedTarget.Model}";

    /// <summary>
    /// 시작 버튼을 누를 수 있는지. 차단 사유가 없고, 확인이 필요하면 모델명이 정확히 입력돼야 합니다.
    /// </summary>
    public bool CanStart
    {
        get
        {
            if (!CanProceed || SelectedTarget is null) return false;
            if (Stage != AppStage.Selecting) return false;
            if (ResolveFreeSpacePlan().Error is not null) return false;

            return !NeedsConfirmation ||
                   SafetyGuard.IsConfirmationValid(SelectedTarget.Disk, ConfirmationText);
        }
    }

    /// <summary>
    /// 시작 버튼이 비활성인 이유. 회색 버튼만 보여주고 이유를 말하지 않으면 사용자는
    /// 무엇을 더 해야 하는지 알 수 없습니다. 누를 수 있으면 빈 문자열입니다.
    /// </summary>
    public string BlockedReason
    {
        get
        {
            if (SelectedSource is null && SelectedTarget is null) return "원본과 대상 디스크를 고르십시오.";
            if (SelectedSource is null) return "원본 디스크를 고르십시오.";
            if (SelectedTarget is null) return "대상 디스크를 고르십시오.";

            if (!CanProceed)
            {
                var blocker = SafetyIssues.FirstOrDefault(i => i.Severity == SafetySeverity.Blocker);
                return blocker is null
                    ? "안전 검사를 통과하지 못했습니다."
                    : $"차단됨 — {blocker.Message}";
            }

            // 남는 공간 설정이 덜 됐으면 확인 입력보다 먼저 말해 줍니다 — 모델명을 다 치고
            // 나서야 "파티션을 고르십시오"를 만나면 헛수고가 됩니다.
            if (ResolveFreeSpacePlan().Error is { } freeSpaceError) return freeSpaceError;

            if (NeedsConfirmation &&
                !SafetyGuard.IsConfirmationValid(SelectedTarget.Disk, ConfirmationText))
            {
                return "확인 입력이 아직 완료되지 않아 시작할 수 없습니다.";
            }

            return "";
        }
    }

    public bool HasBlockedReason => BlockedReason.Length > 0;

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

    // --- 부팅 복구 (BCD 장치 참조 수정) ------------------------------------

    /// <summary>검사에서 BCD 장치 참조 문제가 잡혀 복구 버튼을 보여줄지.</summary>
    [ObservableProperty] private bool _bootRepairAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairBootCommand))]
    private bool _isRepairingBoot;

    public bool CanRepairBoot => !IsRepairingBoot;

    [ObservableProperty] private bool _bootRepairRan;
    [ObservableProperty] private string _bootRepairMessage = "";
    [ObservableProperty] private bool _bootRepairSuccess;

    // --- 파티션 확장 재시도 (남는 공간을 마지막 파티션에 합치기) ------------

    /// <summary>
    /// 대상에 남는 미할당 공간이 있어 "파티션 확장" 버튼을 보여줄지. 클론 중에는 대상 볼륨
    /// 접근 제약으로 확장이 실패하기 쉬우므로, 대상을 단독 연결한 뒤 이 버튼으로 마무리합니다.
    /// </summary>
    [ObservableProperty] private bool _partitionExpandAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExpandPartitionCommand))]
    private bool _isExpandingPartition;

    public bool CanExpandPartition => !IsExpandingPartition;

    [ObservableProperty] private bool _partitionExpandRan;
    [ObservableProperty] private string _partitionExpandMessage = "";
    [ObservableProperty] private bool _partitionExpandSuccess;

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

    /// <summary>
    /// 방금 클론한 대상 디스크를 지금 다시 열거해 최신 정보를 얻습니다. 클론으로 파티션이
    /// 바뀌었고 USB 재연결로 장치 번호가 달라졌을 수 있으므로, 먼저 신원(모델/시리얼/크기)으로
    /// 찾고 실패하면 장치 번호로 대체합니다.
    /// </summary>
    private async Task<DiskInfo?> ResolveCurrentTargetAsync()
    {
        if (SelectedTarget is null) return null;

        var previousTarget = SelectedTarget.Disk;
        var disks = await _diskService.EnumerateDisksAsync();
        return disks.FirstOrDefault(d =>
                   d.SizeBytes == previousTarget.SizeBytes &&
                   string.Equals(d.Model, previousTarget.Model, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(d.SerialNumber ?? "", previousTarget.SerialNumber ?? "", StringComparison.OrdinalIgnoreCase))
               ?? disks.FirstOrDefault(d => d.DeviceNumber == previousTarget.DeviceNumber);
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

        // 배치 막대는 한쪽만 골라도 보여줍니다 — 고르는 중에 디스크 구성을 확인하는 것이 목적입니다.
        SourceLayout = DiskLayoutViewModel.For(SelectedSource?.Disk, DiskRole.Source);
        TargetLayout = DiskLayoutViewModel.For(SelectedTarget?.Disk, DiskRole.Target);
        UpdateAfterLayout();

        if (SelectedSource is null || SelectedTarget is null)
        {
            // "원본 디스크를 고르십시오" 같은 안내도 여기서 갱신돼야 합니다.
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(BlockedReason));
            OnPropertyChanged(nameof(HasBlockedReason));
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

        // 차단 사유 문구는 SafetyIssues 내용에 따라 달라지므로 목록을 채운 뒤 알립니다.
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(HasBlockedReason));

        UpdateResizeChoices();

        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>원본 파티션을 확대 후보 목록으로 채우고, 확대 가능 여부를 갱신합니다.</summary>
    private void UpdateResizeChoices()
    {
        int? previous = SelectedResizePartition?.Number;
        ResizablePartitions.Clear();

        if (SelectedSource is not null)
        {
            // 확대는 diskpart로 NTFS를 늘리므로 NTFS 파티션만 후보로 둡니다.
            foreach (var p in SelectedSource.Disk.Partitions
                         .Where(p => p.FileSystem is not null &&
                                     p.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p.StartingOffset))
            {
                ResizablePartitions.Add(new PartitionChoiceViewModel(p));
            }
        }

        // 이전 선택을 같은 번호로 복원하거나 첫 항목으로.
        SelectedResizePartition =
            ResizablePartitions.FirstOrDefault(c => c.Number == previous) ?? ResizablePartitions.FirstOrDefault();

        OnPropertyChanged(nameof(CanResize));
        OnPropertyChanged(nameof(HasFreeSpace));
        OnPropertyChanged(nameof(FreeSpaceText));

        // 대상이 더 이상 크지 않거나 후보가 없으면 '고른 파티션 넓히기'를 끄고 기본으로 되돌립니다.
        // 고를 수 없게 된 방식이 선택된 채로 남으면 시작할 때 엉뚱하게 실패합니다.
        if (FreeSpaceGrowPartition && !CanResize)
        {
            FreeSpaceGrowPartition = false;
            FreeSpaceLeave = true;
        }

        // 남는 공간이 아예 없으면 선택 자체가 의미 없으므로 기본으로 되돌립니다.
        if (!HasFreeSpace && !FreeSpaceLeave)
        {
            FreeSpaceExpandLast = false;
            FreeSpaceGrowPartition = false;
            FreeSpaceLeave = true;
        }
    }

    /// <summary>
    /// 남는 공간 선택이 성립하지 않는 이유. 없으면 빈 문자열.
    /// </summary>
    /// <remarks>
    /// 같은 문구가 시작 버튼 아래(<see cref="BlockedReason"/>)에도 뜨지만, 그곳은 화면 한참
    /// 아래입니다. 설정을 만지는 사람의 눈은 막대와 라디오에 있으므로, 사라진 "변경 후" 막대
    /// 자리에서 바로 이유를 말해 줘야 합니다. 그림이 소리 없이 없어지는 것은 아무 설명도
    /// 아닙니다.
    /// </remarks>
    public string FreeSpaceError =>
        SelectedSource is null || SelectedTarget is null
            ? ""
            : ResolveFreeSpacePlan().Error ?? "";

    public bool HasFreeSpaceError => FreeSpaceError.Length > 0;

    /// <summary>대상이 원본보다 커서 남는 공간이 생기는지.</summary>
    public bool HasFreeSpace =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedTarget.Disk.SizeBytes > SelectedSource.Disk.SizeBytes;

    /// <summary>"남는 공간 2.73 TB 를 어떻게 할까요" — 무엇에 대한 선택인지 바로 알 수 있게.</summary>
    public string FreeSpaceText
    {
        get
        {
            if (!HasFreeSpace) return "";
            long free = SelectedTarget!.Disk.SizeBytes - SelectedSource!.Disk.SizeBytes;
            return $"남는 공간 {SizeFormatter.Format(free)} 를 어떻게 할까요";
        }
    }

    partial void OnFreeSpaceGrowPartitionChanged(bool value)
    {
        // 켰는데 아무 것도 안 골랐으면 첫 후보를 선택해 줍니다.
        if (value && SelectedResizePartition is null)
            SelectedResizePartition = ResizablePartitions.FirstOrDefault();

        RefreshFreeSpaceChoice();
    }

    partial void OnFreeSpaceLeaveChanged(bool value) => RefreshFreeSpaceChoice();
    partial void OnFreeSpaceExpandLastChanged(bool value) => RefreshFreeSpaceChoice();
    partial void OnSelectedResizePartitionChanged(PartitionChoiceViewModel? value) => RefreshFreeSpaceChoice();
    partial void OnResizeFillRemainingChanged(bool value) => RefreshFreeSpaceChoice();
    partial void OnResizeSizeGbChanged(string value) => RefreshFreeSpaceChoice();

    /// <summary>
    /// 남는 공간 선택이 바뀌면 결과 막대와 시작 버튼이 <b>함께</b> 갱신돼야 합니다.
    /// 막대만 다시 그리면, 파티션을 고르지 않아 시작할 수 없는 상태인데도 버튼은 켜져 있습니다.
    /// </summary>
    private void RefreshFreeSpaceChoice()
    {
        UpdateAfterLayout();

        OnPropertyChanged(nameof(FreeSpaceError));
        OnPropertyChanged(nameof(HasFreeSpaceError));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(HasBlockedReason));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 화면 상태를 클론 엔진이 받는 형태로 옮깁니다. 판정 자체는
    /// <see cref="FreeSpacePlanner"/>(Core, 단위 테스트 있음)가 하고 여기서는 값만 건넵니다.
    /// 미리보기 막대·시작 버튼·실제 실행이 모두 이 하나를 부릅니다.
    /// </summary>
    private FreeSpacePlan ResolveFreeSpacePlan() => FreeSpacePlanner.Resolve(
        HasFreeSpace, CanResize, FreeSpaceExpandLast, FreeSpaceGrowPartition,
        SelectedResizePartition?.Number, ResizeFillRemaining, ResizeSizeGb,
        SelectedSource?.Disk.Partitions, SelectedTarget?.Disk.SizeBytes ?? 0);

    /// <summary>
    /// "복제가 끝나면 이렇게 됩니다" 막대를 다시 계산합니다. 남는 공간 선택이 바뀔 때마다
    /// 즉시 반영돼야 사용자가 결과를 보면서 고를 수 있습니다.
    /// </summary>
    private void UpdateAfterLayout()
    {
        if (SelectedSource is null || SelectedTarget is null)
        {
            TargetAfterLayout = null;
            return;
        }

        var plan = ResolveFreeSpacePlan();

        // 시작할 수 없는 상태에서는 결과 막대를 그리지 않습니다. 무엇이 될지 모르는 채로
        // 그럴듯한 그림을 보여 주는 것보다 아무것도 안 보여 주는 편이 정직합니다.
        if (plan.Error is not null)
        {
            TargetAfterLayout = null;
            return;
        }

        var projected = ProjectedLayout.After(
            SelectedSource.Disk, SelectedTarget.Disk.SizeBytes, plan.Mode, plan.Grow);

        if (projected is null)
        {
            TargetAfterLayout = null;
            return;
        }

        // 미리보기는 '대상 크기의 디스크에 이 배치'라는 가상의 디스크입니다.
        var preview = new DiskInfo
        {
            DeviceNumber = SelectedTarget.Disk.DeviceNumber,
            Model = SelectedTarget.Disk.Model,
            SizeBytes = SelectedTarget.Disk.SizeBytes,
            LogicalSectorSize = SelectedTarget.Disk.LogicalSectorSize,
            PartitionStyle = SelectedSource.Disk.PartitionStyle,
            Partitions = projected,
        };

        TargetAfterLayout = DiskLayoutViewModel.For(preview, DiskRole.TargetAfter);
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
        BootRepairAvailable = false;
        BootRepairRan = false;
        BootRepairMessage = "";
        PartitionExpandAvailable = false;
        PartitionExpandRan = false;
        PartitionExpandMessage = "";
        PartitionExpandSuccess = false;
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
            var target = await ResolveCurrentTargetAsync();

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

            // BCD 장치 참조가 이 디스크와 불일치(0xc000000e)면 복구 버튼을 제안합니다.
            BootRepairAvailable = report.Items.Any(i =>
                i.Passed == false && i.Name.Contains("장치 참조"));

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

    /// <summary>
    /// 클론의 BCD 장치 참조를 이 디스크의 파티션으로 다시 설정해 0xc000000e를 고칩니다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepairBoot))]
    private async Task RepairBootAsync()
    {
        if (SelectedTarget is null) return;

        IsRepairingBoot = true;
        BootRepairRan = false;
        try
        {
            var target = await ResolveCurrentTargetAsync();

            if (target is null)
            {
                BootRepairMessage = "대상 디스크를 다시 찾지 못했습니다.";
                BootRepairSuccess = false;
                BootRepairRan = true;
                return;
            }

            var repair = new BootRepair(_loggerFactory.CreateLogger<BootRepair>());
            var result = await Task.Run(() => repair.Repair(target));

            BootRepairMessage = result.Message;
            BootRepairSuccess = result.Success;
            BootRepairRan = true;
            _logger.LogInformation("부팅 복구: 성공={Success} {Message}", result.Success, result.Message);

            // 복구 성공 시 자동으로 다시 검사해 결과를 갱신합니다.
            if (result.Success) await BootCheckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 복구에 실패했습니다.");
            BootRepairMessage = $"복구에 실패했습니다: {ex.Message}";
            BootRepairSuccess = false;
            BootRepairRan = true;
        }
        finally
        {
            IsRepairingBoot = false;
        }
    }

    /// <summary>
    /// 대상 디스크의 마지막 파티션을 남는 미할당 공간까지 확장합니다.
    /// </summary>
    /// <remarks>
    /// 클론 중 자동 확장은 원본이 함께 연결돼 있어 대상 볼륨에 접근하지 못해 실패하기 쉽습니다.
    /// 대상을 단독으로 연결한 뒤 이 버튼을 누르면(원본 분리 상태) 확실히 동작합니다 —
    /// diskpart가 파티션과 NTFS를 한 번에 정합적으로 늘리므로 중간에 깨진 상태가 없습니다.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanExpandPartition))]
    private async Task ExpandPartitionAsync()
    {
        if (SelectedTarget is null) return;

        IsExpandingPartition = true;
        PartitionExpandRan = false;
        try
        {
            var target = await ResolveCurrentTargetAsync();
            if (target is null)
            {
                PartitionExpandMessage = "대상 디스크를 다시 찾지 못했습니다.";
                PartitionExpandSuccess = false;
                PartitionExpandRan = true;
                return;
            }

            var extender = new PartitionExtender(
                _diskService, _loggerFactory.CreateLogger<PartitionExtender>());

            // 리사이즈로 확대한 파티션이 있으면 그 파티션을(마지막이 아닐 수 있음), 아니면 마지막 파티션을 넓힙니다.
            var result = _grownPartitionNumber is { } grownNumber
                ? await extender.TryExpandPartitionAsync(target.DeviceNumber, grownNumber)
                : await extender.TryExpandLastAsync(target.DeviceNumber);

            PartitionExpandMessage = result.Message;
            PartitionExpandSuccess = result.Success;
            PartitionExpandRan = true;
            _logger.LogInformation("파티션 확장: 성공={Success} {Message}", result.Success, result.Message);

            // 성공했으면 더 확장할 공간이 없으니 버튼을 감춥니다.
            if (result.Success) PartitionExpandAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "파티션 확장에 실패했습니다.");
            PartitionExpandMessage = $"확장에 실패했습니다: {ex.Message}";
            PartitionExpandSuccess = false;
            PartitionExpandRan = true;
        }
        finally
        {
            IsExpandingPartition = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (SelectedSource is null || SelectedTarget is null) return;

        var source = SelectedSource.Disk;
        var target = SelectedTarget.Disk;

        // 남는 공간 처리 방식을 먼저 정합니다. 입력이 잘못됐으면 대상에 아무것도 쓰지 않고
        // 여기서 멈춥니다 — 몇 시간짜리 작업을 시작한 뒤 잘못을 알리는 것보다 낫습니다.
        var plan = ResolveFreeSpacePlan();

        // 시작 버튼은 이미 이 사유로 막혀 있어야 합니다. 그래도 한 번 더 봅니다 — 대상 디스크에
        // 쓰기 시작한 뒤에는 되돌릴 수 없으므로, 배선이 어긋났을 때 조용히 진행되면 안 됩니다.
        if (plan.Error is { } planError)
        {
            Stage = AppStage.Finished;
            ShowFailure("남는 공간 설정이 올바르지 않습니다", planError,
                "대상 디스크에는 아무것도 쓰지 않았습니다.");
            return;
        }

        var freeSpaceMode = plan.Mode;
        var growRequest = plan.Grow;
        _grownPartitionNumber = growRequest?.PartitionNumber;

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
            SkipUnusedBlocks = SkipUnusedBlocks,
            FreeSpace = freeSpaceMode,
            GrowRequest = growRequest,
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

        // 파티션 확장 결과는 아래 전용 패널(재시도 버튼 포함)에서 보여주므로 여기선 생략합니다.

        ResultDetails = string.Join("\n", details);
        ResultIsSuccess = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;

        // 클론 중 파티션 확장을 시도했다면 그 결과를 결과 화면에도 보여줍니다.
        if (report.PartitionExpand is { } expand)
        {
            PartitionExpandRan = true;
            PartitionExpandMessage = expand.Message;
            PartitionExpandSuccess = expand.Success;
        }

        // 대상에 남는 공간이 있고 아직 확장이 끝나지 않았으면 "파티션 확장" 버튼을 제안합니다.
        // 클론 중 자동 확장은 대상 볼륨 접근 제약으로 실패하기 쉬우므로, 대상을 단독 연결한
        // 뒤 이 버튼으로 마무리하는 것이 정석입니다.
        bool alreadyExpanded = report.PartitionExpand is { Success: true };
        PartitionExpandAvailable = ResultIsSuccess &&
                                   report.Target.SizeBytes > report.Source.SizeBytes &&
                                   !alreadyExpanded;

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

        // 리사이즈 클론에서 GPT 재작성이 실패하면 파티션 데이터는 새 위치에 있는데 파티션
        // 테이블은 옛 위치를 가리켜 배치가 깨진 상태입니다. 데이터가 복사됐다는 이유로 "완료"로
        // 보이면 사용자가 부팅 불가 디스크를 정상 사본으로 오인합니다. 명확한 실패·경고로 덮어씁니다.
        if (report.ResizeLayoutCorrupted)
        {
            ResultIsSuccess = false;
            PartitionExpandAvailable = false;
            ResultTitle = "클론했지만 파티션 배치가 깨졌습니다 — 이 디스크로 부팅하지 마십시오";
            ResultMessage =
                $"[{report.Target.DeviceNumber}] {report.Target.Model} 에 데이터는 복사됐지만, " +
                "파티션 리사이즈 배치를 반영하는 GPT 재작성에 실패해 파티션 테이블이 옛 위치를 " +
                "가리킵니다. 이 디스크로 부팅하거나 데이터를 사용하지 마십시오. " +
                "리사이즈를 끄고 다시 클론하십시오.";
        }
    }

    private void ShowFailure(string title, string message, string details)
    {
        ResultIsSuccess = false;
        ResultTitle = title;
        ResultMessage = message;
        ResultDetails = details;
    }
}
