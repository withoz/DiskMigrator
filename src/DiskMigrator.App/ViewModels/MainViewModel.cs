using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskMigrator.App.Localization;
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

        // 스마트 클론은 스냅샷 볼륨의 할당 정보를 읽어야 하므로 스냅샷이 있을 때만 켭니다.
        SkipUnusedBlocks = IsSnapshotAvailable;
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
    /// <remarks>
    /// 기본으로 켭니다. 대부분의 디스크는 실데이터가 용량보다 훨씬 적어 복제 시간이 크게
    /// 줄고, 결과물은 통째 복사와 다르지 않습니다. 스냅샷이 없으면 엔진이 통째 복사로
    /// 안전하게 되돌아가므로 켜 둬서 나빠질 것이 없습니다.
    /// </remarks>
    [ObservableProperty] private bool _skipUnusedBlocks;

    /// <summary>
    /// 클론 후 대상 Windows를 하드웨어 독립화(Universal Restore)할지. 시스템 디스크를
    /// 다른 PC로 옮길 때 켭니다. 표준 저장소 드라이버를 부팅 시작으로 설정해 0x7B를 예방합니다.
    /// </summary>
    [ObservableProperty] private bool _universalRestore = true;

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

    /// <summary>'파티션 조정'을 골랐을 때만 파티션·크기 입력을 보여줍니다.</summary>
    public bool ShowGrowDetails => FreeSpaceGrowPartition;

    [ObservableProperty] private PartitionChoiceViewModel? _selectedResizePartition;

    /// <summary>true면 남는 공간을 전부 확대 파티션에, false면 <see cref="ResizeSizeGb"/> 크기로.</summary>
    [ObservableProperty] private bool _resizeFillRemaining = true;

    /// <summary><see cref="ResizeFillRemaining"/>의 반대. '새 총 크기' 라디오가 묶이는 곳입니다.</summary>
    /// <remarks>
    /// 예전에는 '남는 공간 전부'만 뷰모델에 묶여 있고 '새 총 크기'의 선택 상태는 화면에만
    /// 있었습니다. 그러면 <see cref="ResizeFillRemaining"/> 바인딩이 다시 적용되는 순간
    /// 선택이 '남는 공간 전부'로 돌아가고, 입력한 크기 대신 <b>남는 공간 전부</b>가 쓰입니다 —
    /// 사용자가 지정한 것과 전혀 다른 결과인데 화면은 그렇게 보이지 않습니다.
    /// 두 라디오 모두 뷰모델이 받치게 해서 그런 상태 자체를 없앱니다.
    /// </remarks>
    public bool ResizeUseCustomSize
    {
        get => !ResizeFillRemaining;
        set
        {
            // 라디오는 꺼질 때도 false를 써 보냅니다. 그때 반대쪽을 건드리면 두 개가 서로를
            // 꺼서 아무것도 선택되지 않습니다.
            if (value) ResizeFillRemaining = false;
        }
    }

    [ObservableProperty] private string _resizeSizeGb = "";

    /// <summary>
    /// 대상이 원본보다 커서 확대할 여지가 있는지. 리사이즈는 <b>GPT 원본</b>만, 그리고 원본과
    /// 대상의 <b>논리 섹터 크기가 같을 때</b>만 지원합니다(GPT 엔트리 위치가 LBA 단위라
    /// 섹터 크기가 다르면 재배치가 어긋납니다).
    /// </summary>
    public bool CanResize =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedSource.Disk.PartitionStyle is PartitionStyle.Gpt or PartitionStyle.Mbr &&
        SelectedSource.Disk.LogicalSectorSize == SelectedTarget.Disk.LogicalSectorSize &&
        SelectedTarget.Disk.SizeBytes > SelectedSource.Disk.SizeBytes &&
        !SelectedSource.Disk.HasExtendedPartition &&
        !ExceedsMbrLimit &&
        ResizablePartitions.Count > 0;

    /// <summary>
    /// MBR 원본인데 대상이 약 2 TB를 넘는지. MBR의 시작·크기 필드는 32비트 섹터 수라
    /// 그보다 뒤는 가리킬 수 없어, 뒤로 밀린 파티션이 표현되지 않습니다.
    /// </summary>
    private bool ExceedsMbrLimit =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedSource.Disk.PartitionStyle == PartitionStyle.Mbr &&
        (SelectedTarget.Disk.SizeBytes / SelectedTarget.Disk.LogicalSectorSize) - 1 > uint.MaxValue;

    /// <summary>'파티션 조정'이 회색인 이유. 쓸 수 있으면 빈 문자열.</summary>
    /// <remarks>
    /// 항목 설명에 "GPT 원본만"이라고 적어 두었지만 막는 조건은 네 가지입니다. 하나만 적어
    /// 두면 나머지 세 경우에는 왜 회색인지 알 방법이 없고, 적어 둔 그 하나조차 흘려보게 됩니다.
    /// 지금 고른 디스크에 해당하는 이유를 그 자리에서 말해 줍니다.
    /// </remarks>
    public string ResizeBlockedReason
    {
        get
        {
            if (SelectedSource is null || SelectedTarget is null || CanResize) return "";

            if (SelectedSource.Disk.PartitionStyle is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
            {
                return $"원본이 {SelectedSource.Disk.PartitionStyle.ToString().ToUpperInvariant()} 형식이라 " +
                       "쓸 수 없습니다 — 이 방식은 파티션을 옮기고 파티션 테이블을 다시 씁니다.";
            }

            if (SelectedSource.Disk.HasExtendedPartition)
            {
                return "원본에 확장 파티션(논리 드라이브)이 있어 쓸 수 없습니다 — 논리 드라이브는 " +
                       "EBR 체인으로 이어져 있어 옮기려면 체인 전체를 다시 써야 합니다.";
            }

            if (ExceedsMbrLimit)
            {
                return "MBR 원본은 약 2 TB까지만 파티션 위치를 가리킬 수 있어, 이보다 큰 대상에는 " +
                       "쓸 수 없습니다. '마지막 파티션에 합치기'를 쓰거나 원본을 GPT로 바꾸십시오.";
            }

            if (SelectedSource.Disk.LogicalSectorSize != SelectedTarget.Disk.LogicalSectorSize)
            {
                return $"원본과 대상의 섹터 크기가 다릅니다(원본 {SelectedSource.Disk.LogicalSectorSize}바이트, " +
                       $"대상 {SelectedTarget.Disk.LogicalSectorSize}바이트). 파티션 위치가 어긋나 쓸 수 없습니다.";
            }

            if (ResizablePartitions.Count == 0)
                return "원본에 넓힐 수 있는 NTFS 파티션이 없습니다.";

            return "대상이 원본보다 크지 않아 넓힐 공간이 없습니다.";
        }
    }

    public bool HasResizeBlockedReason => ResizeBlockedReason.Length > 0;

    /// <summary>리사이즈로 확대한 파티션 번호(클론 후 "파티션 확장" 버튼이 이 파티션을 넓힘). 없으면 null.</summary>
    private int? _grownPartitionNumber;

    /// <summary>이번 클론에서 복구 파티션이 뒤로 밀렸는지. 완료 화면 안내에 씁니다.</summary>
    private bool _recoveryPartitionMoved;

    /// <summary>
    /// 복구 환경(WinRE)이 끊어졌음을 알리는 안내. 해당 없으면 빈 문자열.
    /// </summary>
    /// <remarks>
    /// Windows는 <c>ReAgent.xml</c>에 복구 파티션의 위치를 적어 두고 그 자리를 찾아갑니다.
    /// 리사이즈로 복구 파티션이 밀리면 그 위치가 달라져 복구 환경만 사라진 것처럼 보입니다 —
    /// <b>부팅은 정상</b>이라 사용자가 알아채기 어렵고, 정작 필요할 때(복구가 필요한 순간)
    /// 없다는 걸 알게 됩니다. 명령 한 줄이면 되는 일이므로 여기서 말해 줍니다.
    /// </remarks>
    public string RecoveryHint =>
        _recoveryPartitionMoved && ResultIsSuccess
            ? "복구 파티션이 뒤로 밀려 Windows가 기억하던 위치와 달라졌습니다. 복제한 디스크로 부팅한 뒤 " +
              "관리자 명령 프롬프트에서 reagentc /enable 을 한 번 실행하면 복구 환경이 다시 연결됩니다. " +
              "(Windows 부팅 자체는 정상입니다.)"
            : "";

    public bool HasRecoveryHint => RecoveryHint.Length > 0;

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
            : string.Format(CultureInfo.CurrentCulture, Strings.Get("ConfirmPromptFmt"), SelectedTarget.Model);

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
            if (SelectedSource is null && SelectedTarget is null) return Strings.Get("BlockChooseSourceTarget");
            if (SelectedSource is null) return Strings.Get("BlockChooseSource");
            if (SelectedTarget is null) return Strings.Get("BlockChooseTarget");

            if (!CanProceed)
            {
                var blocker = SafetyIssues.FirstOrDefault(i => i.Severity == SafetySeverity.Blocker);
                return blocker is null
                    ? Strings.Get("BlockSafetyFailed")
                    : string.Format(CultureInfo.CurrentCulture, Strings.Get("BlockPrefixFmt"), blocker.Message);
            }

            // 남는 공간 설정이 덜 됐으면 확인 입력보다 먼저 말해 줍니다 — 모델명을 다 치고
            // 나서야 "파티션을 고르십시오"를 만나면 헛수고가 됩니다.
            if (ResolveFreeSpacePlan().Error is { } freeSpaceError) return freeSpaceError;

            if (NeedsConfirmation &&
                !SafetyGuard.IsConfirmationValid(SelectedTarget.Disk, ConfirmationText))
            {
                return Strings.Get("BlockConfirmIncomplete");
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveryHint))]
    [NotifyPropertyChangedFor(nameof(HasRecoveryHint))]
    private bool _resultIsSuccess;
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

    // --- UEFI 변환 ---------------------------------------------------------
    //
    // MBR·활성 파티션 배치의 사본은 레거시(CSM) 부팅을 지원하는 하드웨어에서만 켜집니다.
    // NVMe에는 레거시 부팅용 옵션 ROM이 사실상 없어, 요즘 PC로 옮기려면 GPT/UEFI로
    // 바꿔야 합니다. 실기에서 이 변환을 손으로 하느라 여러 시간을 썼습니다.

    /// <summary>대상이 BIOS 전용 배치라 UEFI 변환 버튼을 보여줄지.</summary>
    [ObservableProperty] private bool _uefiConvertAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertToUefiCommand))]
    private bool _isConvertingToUefi;

    public bool CanConvertToUefi => !IsConvertingToUefi;

    [ObservableProperty] private bool _uefiConvertRan;
    [ObservableProperty] private string _uefiConvertMessage = "";
    [ObservableProperty] private bool _uefiConvertSuccess;

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

    partial void OnUseSnapshotChanged(bool value)
    {
        // 스마트 클론은 스냅샷 볼륨의 NTFS 할당 비트맵을 읽어야 하므로 스냅샷 없이는 성립하지
        // 않습니다. 스냅샷을 끄면 체크박스가 회색이 될 뿐 켜진 상태로 남는데, 그러면 엔진은
        // 그 값을 조용히 버리고 통째 복사를 합니다 — 화면은 "빈 영역을 건너뜁니다"라고 말하는데
        // 실제로는 아무 일도 일어나지 않습니다. 함께 꺼서 그 상태 자체를 없앱니다.
        if (!value) SkipUnusedBlocks = false;

        UpdateSafety();
    }

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
            SelectedSource.Disk, SelectedTarget.Disk, IsElevated, UseSnapshot,
            sourceHibernated: HasHibernationImage(SelectedSource.Disk));

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

    /// <summary>
    /// 원본에 최대 절전 이미지(<c>hiberfil.sys</c>)가 있는지 — 사본이 검은 화면에서 멈추는 원인.
    /// </summary>
    /// <remarks>
    /// <see cref="SafetyGuard"/>는 파일을 읽지 않는 순수 판정기라, 파일 확인은 여기서 하고
    /// 결과만 넘깁니다. 볼륨이 마운트되지 않았거나 접근할 수 없으면 false — 확인하지 못한 것을
    /// 문제로 단정하지 않습니다.
    /// </remarks>
    private static bool HasHibernationImage(DiskInfo disk)
    {
        try
        {
            string? windowsRoot = BootReadinessCheck.ResolveInput(disk).WindowsRoot;
            return windowsRoot is not null && File.Exists(Path.Combine(windowsRoot, "hiberfil.sys"));
        }
        catch
        {
            return false;
        }
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
                ResizablePartitions.Add(new PartitionChoiceViewModel(p)
                {
                    Selected = c => SelectedResizePartition = c,
                });
            }
        }

        // 이전 선택을 같은 번호로 복원하거나 첫 항목으로.
        SelectedResizePartition =
            ResizablePartitions.FirstOrDefault(c => c.Number == previous) ?? ResizablePartitions.FirstOrDefault();

        OnPropertyChanged(nameof(CanResize));
        OnPropertyChanged(nameof(ResizeBlockedReason));
        OnPropertyChanged(nameof(HasResizeBlockedReason));
        OnPropertyChanged(nameof(HasFreeSpace));
        OnPropertyChanged(nameof(FreeSpaceText));

        // 대상이 더 이상 크지 않거나 후보가 없으면 '파티션 조정'을 끄고 기본으로 되돌립니다.
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
            return string.Format(CultureInfo.CurrentCulture, Strings.Get("FreeSpaceHeaderFmt"), SizeFormatter.Format(free));
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
    partial void OnSelectedResizePartitionChanged(PartitionChoiceViewModel? value)
    {
        // 칩은 자기가 켜졌을 때만 알려 옵니다(라디오는 꺼질 때도 false를 보내므로). 나머지를
        // 끄는 것은 여기서 합니다 — 목록 밖에서 선택이 바뀌어도 화면이 따라오게 하려면
        // 뷰모델이 한 방향을 책임져야 합니다.
        foreach (var c in ResizablePartitions) c.IsSelected = ReferenceEquals(c, value);

        RefreshFreeSpaceChoice();
    }
    partial void OnResizeFillRemainingChanged(bool value)
    {
        OnPropertyChanged(nameof(ResizeUseCustomSize));
        RefreshFreeSpaceChoice();
    }
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
        UpdateResizeHandle(plan);
    }

    // --- 막대에서 끌어 조정 ------------------------------------------------
    //
    // 숫자를 입력하는 대신 경계를 끌어서 맞춥니다. 손잡이는 입력 장치일 뿐이고, 값은
    // ResizeSizeGb로 흘러 기존 배선(FreeSpacePlanner → 미리보기·시작 버튼·엔진)을 그대로
    // 탑니다. 손잡이가 자기만의 계산으로 미리보기를 그리면 화면과 엔진이 또 갈라집니다.

    /// <summary>손잡이를 보여줄지 — '파티션 조정'으로 실제 조정이 가능할 때만.</summary>
    [ObservableProperty] private bool _showResizeHandle;

    /// <summary>손잡이의 가로 위치(막대 너비에 대한 비율 0~1).</summary>
    [ObservableProperty] private double _resizeHandleFraction;

    /// <summary>
    /// 막대 너비 비율 1당 몇 바이트인지. 끌린 픽셀을 바이트로 옮길 때 씁니다.
    /// </summary>
    /// <remarks>
    /// 막대는 실제 비율대로 그려지지 않습니다 — 너무 작아 안 보이는 조각에 최소 폭을 주고
    /// 전체를 다시 정규화하기 때문입니다(<see cref="DiskLayoutMap.MinDisplayFraction"/>).
    /// 그래서 막대 전체를 선형으로 보면 끌어놓은 위치와 실제 크기가 어긋납니다.
    ///
    /// <para>다만 <b>넓힐 파티션과 남는 공간</b>은 둘 다 커서 최소 폭 보정 대상이 아니므로,
    /// 그 둘이 나눠 갖는 구간 안에서는 픽셀과 바이트가 정확히 비례합니다. 그 구간의
    /// 비율과 바이트 수로 환산 계수를 만듭니다.</para>
    /// </remarks>
    [ObservableProperty] private double _resizeBytesPerFraction;

    private void UpdateResizeHandle(FreeSpacePlan plan)
    {
        if (TargetAfterLayout is not { } layout ||
            plan.Mode != FreeSpaceMode.GrowPartition ||
            plan.Grow is not { } grow)
        {
            ShowResizeHandle = false;
            return;
        }

        var segments = layout.Segments;
        int growIndex = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].PartitionNumber == grow.PartitionNumber) { growIndex = i; break; }
        }

        // 넓힐 조각 뒤의 미할당 구간이 조정 여지입니다. 없으면 이미 꽉 찬 상태입니다.
        // 뒤에 남은 미할당 구간. '남는 공간 전부'를 고르면 <b>이것이 없습니다</b> — 넓힌
        // 파티션이 끝까지 차지하기 때문입니다. 그때도 손잡이는 나와야 합니다. 없으면 기본
        // 상태에서 손잡이가 영영 안 보여 사용자가 이 기능을 발견할 수 없습니다.
        var free = growIndex >= 0
            ? segments.Skip(growIndex + 1).FirstOrDefault(s => s.PartitionNumber is null)
            : null;

        // 실제로 움직일 수 있는지는 배치가 아니라 범위가 정합니다 — 이미 최대치여도
        // 왼쪽으로는 줄일 수 있습니다.
        if (growIndex < 0 ||
            SelectedResizePartition is not { } choice ||
            ResizeBounds(choice) is not var (min, max) || max <= min)
        {
            ShowResizeHandle = false;
            return;
        }

        double adjustableFraction = segments[growIndex].Fraction + (free?.Fraction ?? 0);
        long adjustableBytes = segments[growIndex].LengthBytes + (free?.LengthBytes ?? 0);

        if (adjustableFraction <= 0)
        {
            ShowResizeHandle = false;
            return;
        }

        ResizeHandleFraction = segments.Take(growIndex + 1).Sum(s => s.Fraction);
        ResizeBytesPerFraction = adjustableBytes / adjustableFraction;
        ShowResizeHandle = true;
    }

    /// <summary>
    /// 넓힐 파티션 크기를 <paramref name="deltaBytes"/>만큼 옮깁니다(끌기·키보드가 함께 부릅니다).
    /// </summary>
    /// <remarks>
    /// 범위를 벗어나는 값은 <b>만들 수 없게</b> 잘라냅니다. 잘못된 값을 만들게 두고 나중에
    /// 오류를 띄우는 것보다, 애초에 갈 수 없는 곳으로 손잡이가 가지 않는 편이 낫습니다.
    /// 1 MiB 단위로 맞추는 것도 여기서 합니다 — 파티션 정렬 단위와 같습니다.
    /// </remarks>
    public void NudgeResizeBytes(double deltaBytes)
    {
        if (SelectedSource is null || SelectedTarget is null ||
            SelectedResizePartition is not { } choice) return;

        var bounds = ResizeBounds(choice);
        if (bounds is not var (min, max) || max <= min) return;

        // '남는 공간 전부'로 시작했다면 현재 값은 최대치입니다. 끌기 시작과 동시에
        // '새 총 크기' 모드로 넘어갑니다 — 손잡이를 움직였는데 값이 안 바뀌면 안 됩니다.
        long current = ResizeFillRemaining
            ? max
            : FreeSpacePlanner.TryParseSizeGb(ResizeSizeGb, out double gb) && gb > 0
                ? (long)(gb * FreeSpacePlanner.BytesPerGb)
                : min;

        long moved = (long)Math.Clamp(current + deltaBytes, min, max);

        // 1 MiB 경계로 맞춘 뒤 다시 한 번 범위 안으로 넣습니다(내림이 min 아래로 갈 수 있음).
        moved = Math.Clamp(moved / ResizePlanner.Alignment * ResizePlanner.Alignment, min, max);

        ResizeFillRemaining = false;
        ResizeSizeGb = ((double)moved / FreeSpacePlanner.BytesPerGb)
            .ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>넓힐 수 있는 범위(바이트). 계산할 수 없으면 null.</summary>
    private (long Min, long Max)? ResizeBounds(PartitionChoiceViewModel choice)
    {
        if (SelectedSource is null || SelectedTarget is null) return null;

        var part = SelectedSource.Disk.Partitions.FirstOrDefault(p => p.Number == choice.Number);
        if (part is null) return null;

        try
        {
            // 최대치는 '남는 공간 전부'와 같은 배치입니다 — 엔진이 쓰는 계산을 그대로 씁니다.
            var full = ResizePlanner.Plan(
                SelectedSource.Disk.Partitions, SelectedTarget.Disk.SizeBytes,
                new PartitionGrowRequest(choice.Number, null));

            long max = full.GrownPartition?.LengthBytes ?? part.LengthBytes;
            return (part.LengthBytes, max);
        }
        catch
        {
            // 넓힐 수 없는 조합(여유 없음 등)에서는 손잡이도 의미가 없습니다.
            return null;
        }
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
        UefiConvertAvailable = false;
        UefiConvertRan = false;
        UefiConvertMessage = "";
        UefiConvertSuccess = false;
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
                BootCheckVerdict = Strings.Get("TargetNotFoundAgain");
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
                BootRepairMessage = Strings.Get("TargetNotFoundAgain");
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
                PartitionExpandMessage = Strings.Get("TargetNotFoundAgain");
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

    /// <summary>
    /// BIOS/MBR로 복제된 대상을 GPT/UEFI로 부팅 가능하게 바꿉니다.
    /// </summary>
    /// <remarks>
    /// 되돌릴 수 없는 변경이라 사용자가 직접 눌러야 합니다. 복제 자체는 원본을 그대로 옮기는
    /// 일이고, 이것은 "다른 방식으로 부팅되게 만드는" 별개의 결정입니다.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanConvertToUefi))]
    private async Task ConvertToUefiAsync()
    {
        if (SelectedTarget is null) return;

        IsConvertingToUefi = true;
        UefiConvertRan = false;
        try
        {
            var target = await ResolveCurrentTargetAsync();
            if (target is null)
            {
                UefiConvertMessage = Strings.Get("TargetNotFoundAgain");
                UefiConvertSuccess = false;
                UefiConvertRan = true;
                return;
            }

            var converter = new UefiConverter(_diskService, _loggerFactory.CreateLogger<UefiConverter>());
            var result = await converter.ConvertAsync(target);

            UefiConvertMessage = result.Message;
            UefiConvertSuccess = result.Success;
            UefiConvertRan = true;

            foreach (string step in result.Steps) _logger.LogInformation("UEFI 변환 단계: {Step}", step);
            _logger.LogInformation("UEFI 변환: 성공={Success} {Message}", result.Success, result.Message);

            // 성공했으면 이미 GPT이므로 더 변환할 것이 없습니다.
            if (result.Success) UefiConvertAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UEFI 변환에 실패했습니다.");
            UefiConvertMessage = $"변환에 실패했습니다: {ex.Message}";
            UefiConvertSuccess = false;
            UefiConvertRan = true;
        }
        finally
        {
            IsConvertingToUefi = false;
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

        // 넓힌 파티션보다 뒤에 있는 것들은 오른쪽으로 밀립니다. 그 안에 복구 파티션이 있으면
        // 위치가 달라져 WinRE가 끊어지므로, 끝난 뒤 알려 주려고 지금 기억해 둡니다.
        _recoveryPartitionMoved =
            growRequest is not null &&
            source.Partitions.FirstOrDefault(p => p.Number == growRequest.PartitionNumber) is { } grown &&
            source.Partitions.Any(p => p.IsWindowsRecovery && p.StartingOffset > grown.StartingOffset);

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
            // 라벨을 "GPT"로 박아 두면 MBR 클론에서 "GPT: MBR 파티션 테이블을 다시 썼습니다"가
            // 됩니다. 내부 필드 이름(GptRepair)을 화면 글자로 그대로 쓴 탓이었습니다.
            details.Add($"파티션 테이블: {gpt.Description}");
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

        // 원본이 BIOS 전용 배치였으면 사본도 그렇습니다. 요즘 PC(특히 NVMe)에서는 부팅하지
        // 않으므로 GPT/UEFI 변환을 제안합니다. 되돌릴 수 없는 변경이라 자동으로 하지 않습니다.
        UefiConvertAvailable = ResultIsSuccess && UefiConverter.NeedsConversion(report.Source);

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
            UefiConvertAvailable = false;
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

    // --- 업데이트 확인 -----------------------------------------------------

    private string? _latestReleaseUrl;

    /// <summary>새 버전이 있는지. 화면 상단 배너 표시 여부.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>알림에 보여줄 최신 버전 문자열(예: "0.8.0").</summary>
    [ObservableProperty]
    private string _updateVersionText = "";

    /// <summary>GitHub Releases에서 새 버전을 조용히 확인합니다. 실패는 무시합니다.</summary>
    public async Task CheckForUpdatesAsync()
    {
        Version current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);
        UpdateInfo info = await UpdateChecker.CheckAsync(current);
        if (!info.Available) return;

        _latestReleaseUrl = info.ReleaseUrl;
        UpdateVersionText = info.LatestVersion ?? "";
        UpdateAvailable = true;
        _logger.LogInformation("새 버전 발견: {Version}", info.LatestVersion);
    }

    /// <summary>최신 릴리스 페이지를 기본 브라우저로 엽니다(다운로드는 사용자가 진행).</summary>
    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (string.IsNullOrEmpty(_latestReleaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "업데이트 페이지를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;
}
