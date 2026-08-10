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
using DiskMigrator.Windows.Pe;
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

/// <summary>작업 종류. 시작 화면 위쪽에서 고릅니다.</summary>
public enum AppMode
{
    /// <summary>디스크 → 디스크.</summary>
    Clone,
    /// <summary>디스크 → 이미지 파일(.vhdx).</summary>
    Backup,
    /// <summary>이미지 파일(.vhdx) → 디스크.</summary>
    Restore,
    /// <summary>클론/복원해 둔 디스크의 부팅 구성 검사·복구(독립 도구 — 복제 없이).</summary>
    FixBoot,
    /// <summary>부팅 USB(WinPE) 만들기 — 안 켜지는 PC를 구조하는 응급 도구.</summary>
    BootUsb,
    /// <summary>Claude 연결 — 이 앱의 진단을 Claude가 읽을 수 있게 여는 로컬 통로.</summary>
    Assistant,
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

        RefreshSnapshotAvailability();
    }

    /// <summary>
    /// 실행 중인 앱의 버전(예: <c>v1.4.0</c>). 헤더에 항상 보이게 두어, 어느 버전으로 작업했는지
    /// 나중에도 확인할 수 있게 합니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 시작 화면(스플래시)에만 있어 1.5초 뒤 사라졌습니다 — 실행 중에는 물론이고
    /// 로그를 봐도 버전을 알 수 없어, 문제를 신고받아도 어느 빌드인지 특정할 수 없었습니다.
    /// 어셈블리에서 직접 읽으므로 버전을 올리면 자동으로 따라옵니다(csproj 한 곳만 고치면 됨).
    /// </remarks>
    public static string AppVersion { get; } = FormatVersion();

    private static string FormatVersion()
    {
        var v = typeof(MainViewModel).Assembly.GetName().Version;
        return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// VSS 사용 가능 여부를 다시 진단해 <see cref="IsSnapshotAvailable"/>·<see cref="SnapshotUnavailableText"/>를
    /// 갱신합니다. 새로고침 때마다 부릅니다 — 사용자가 서비스를 켜고 새로고침하면 바로 반영되도록.
    /// </summary>
    /// <remarks>
    /// 쓸 수 없게 되면 관련 옵션을 <b>끄고</b>, 다시 쓸 수 있게 되면 <b>켭니다</b>. 화면의 안내는
    /// "런타임을 설치한 뒤 새로고침을 누르십시오"라고 말하므로, 새로고침 후에는 옵션이 실제로
    /// 켜져 있어야 합니다 — 잠금만 풀리고 체크는 꺼진 채로 두면 안내대로 했는데도 스냅샷 없이
    /// 복사가 진행됩니다.
    /// </remarks>
    private void RefreshSnapshotAvailability()
    {
        var vss = _snapshotProvider.Diagnose();
        bool was = IsSnapshotAvailable;
        IsSnapshotAvailable = vss.Available;
        SnapshotUnavailableText = vss.Available
            ? ""
            : vss.Hint is null ? vss.Reason ?? "" : $"{vss.Reason} {vss.Hint}";

        // 첫 진단이거나 가용 여부가 바뀐 경우에만 옵션을 맞춥니다 — 사용자가 일부러 끈 선택을
        // 새로고침 때마다 되돌리지 않기 위함입니다.
        if (!_snapshotAvailabilityKnown || was != vss.Available)
        {
            UseSnapshot = vss.Available;
            // 스마트 클론은 스냅샷 볼륨의 할당 정보를 읽어야 하므로 스냅샷이 있을 때만 켭니다.
            SkipUnusedBlocks = vss.Available;
            _snapshotAvailabilityKnown = true;
        }

        if (!vss.Available)
            _logger.LogWarning("VSS 사용 불가: {Reason} / {Hint}", vss.Reason, vss.Hint);
    }

    /// <summary>VSS 가용성을 한 번이라도 진단했는지(첫 진단에서만 옵션 기본값을 정하기 위함).</summary>
    private bool _snapshotAvailabilityKnown;

    // --- Claude 연결 (로컬 MCP 통로) --------------------------------------

    private DiskMigrator.Mcp.McpHost? _mcpHost;
    private DiskMigrator.Mcp.Proposals.ProposalStore? _proposalStore;

    /// <summary>연결 통로가 열려 있는지.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpToggleLabel))]
    private bool _mcpRunning;

    /// <summary>Claude 설정에 넣을 주소. 꺼져 있으면 빈 문자열.</summary>
    [ObservableProperty] private string _mcpUrl = "";

    /// <summary>접근 토큰. 꺼져 있으면 빈 문자열.</summary>
    [ObservableProperty] private string _mcpToken = "";

    /// <summary>
    /// 토큰이 실린 주소 — 헤더를 못 보내는 도구에서 씁니다.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Claude 앱의 "커스텀 커넥터 추가" 화면에서는 쓸 수 없습니다.</b> 그 화면은
    /// 주소가 <c>https</c>로 시작할 것을 요구하는데, 이 통로는 이 컴퓨터 안에서만 통하는
    /// 평문 <c>http</c>라 등록 자체가 거절됩니다(2026-08-10 실기 확인).
    ///
    /// <para>처음에는 "그 화면에 토큰 칸이 없어서 막힌다"고 보고 이 주소를 만들었는데,
    /// 실제로 넣어 보니 토큰 이전에 프로토콜에서 막혔습니다. 진단이 절반만 맞았던 것입니다.
    /// 기능 자체는 서버에서 정상 동작하므로(HTTP 200 확인) 남겨 두되, <b>용도를 사실대로</b>
    /// 적습니다.</para>
    ///
    /// <para><b>이 줄에는 열쇠가 들어 있습니다.</b> 로그에는 남지 않게 막아 두었습니다.</para>
    /// </remarks>
    [ObservableProperty] private string _mcpConnectorUrl = "";

    /// <summary>
    /// 실제로 연결되는 방법 — 그대로 붙여 넣어 실행하는 명령 한 줄.
    /// </summary>
    /// <remarks>
    /// 앱은 지금까지 "Claude의 MCP 설정에 넣으십시오"라고만 했습니다. <b>어디에 어떻게
    /// 넣는지는 말하지 않았습니다.</b> 컴퓨터를 잘 모르는 사용자가 대상인데, 실제로 설정
    /// 화면을 열어 본 사용자가 그 앞에서 멈췄습니다.
    ///
    /// <para>이 한 줄이 유일하게 확인된 방법입니다(실기: <c>claude-code 2.1.223</c>이
    /// 서버에 붙은 것을 로그로 확인).</para>
    /// </remarks>
    [ObservableProperty] private string _mcpAddCommand = "";

    /// <summary>안내·오류 문구.</summary>
    [ObservableProperty] private string _mcpStatusText = "";

    /// <summary>
    /// 디스크 시리얼·볼륨 레이블을 가리지 않고 보낼지. <b>기본은 가립니다.</b>
    /// </summary>
    /// <remarks>
    /// 진단 결과는 대화 기록에 남습니다. 사용자가 의도치 않게 공유하는 일을 막으려면
    /// 가리는 쪽이 기본이어야 하고, 필요할 때만 사용자가 직접 켜야 합니다.
    /// </remarks>
    [ObservableProperty] private bool _mcpShareDetails;

    /// <summary>
    /// Claude가 지금까지 무엇을 물었는지 — 최신이 위로.
    /// </summary>
    /// <remarks>
    /// 앱은 "읽기만 합니다"라고 말하면서 정작 무엇을 읽었는지는 보여주지 않았습니다.
    /// 그 말을 확인할 방법이 사용자에게 없었다는 뜻입니다. 파일 로그에도 남지만,
    /// 로그를 열어 보라고 하는 것은 답이 아닙니다.
    /// </remarks>
    public ObservableCollection<McpActivityViewModel> McpActivities { get; } = [];

    public bool HasMcpActivity => McpActivities.Count > 0;

    private readonly DiskMigrator.Mcp.McpActivityLog _mcpActivityLog = new();

    public string McpToggleLabel => McpRunning
        ? Strings.Get("McpStop")
        : Strings.Get("McpStart");

    /// <summary>연결 통로를 켜고 끕니다.</summary>
    [RelayCommand]
    private async Task ToggleMcpAsync()
    {
        try
        {
            if (_mcpHost is { IsRunning: true })
            {
                await _mcpHost.StopAsync();
                McpRunning = false;
                McpUrl = "";
                McpToken = "";
                McpConnectorUrl = "";
                McpAddCommand = "";
                McpStatusText = Strings.Get("McpStoppedHint");
                _logger.LogInformation("Claude 연결 통로를 닫았습니다.");
                return;
            }

            if (_mcpHost is null)
            {
                _proposalStore = new DiskMigrator.Mcp.Proposals.ProposalStore();

                // 제안이 오거나 사라지면 카드를 보이고 숨깁니다. MCP 스레드에서 오는
                // 이벤트이므로 UI 스레드로 넘겨야 합니다.
                //
                // ⚠ 반드시 BeginInvoke(비동기)입니다. Invoke는 MCP 스레드가 UI를 기다리게
                //   하는데, 앱을 닫을 때 UI 스레드는 ShutdownMcpAsync를 동기로 기다립니다.
                //   그 순간 처리 중인 호출이 있으면 서로를 기다려 앱이 멈춥니다.
                //   화면 갱신은 몇 밀리초 늦어도 되지만, 교착은 강제 종료 말고 길이 없습니다.
                _proposalStore.Changed += (_, e) =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Proposal = e.Current);

                _mcpActivityLog.Recorded += (_, a) =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        McpActivities.Insert(0, new McpActivityViewModel(a));
                        while (McpActivities.Count > DiskMigrator.Mcp.McpActivityLog.Capacity)
                            McpActivities.RemoveAt(McpActivities.Count - 1);
                        OnPropertyChanged(nameof(HasMcpActivity));
                    });

                _mcpHost = new DiskMigrator.Mcp.McpHost(
                    _diskService, _proposalStore, new AppStateBridge(this), _loggerFactory,
                    _mcpActivityLog);
            }

            // 지난번 토큰·포트를 이어 씁니다 — 사용자가 Claude 설정에 넣어 둔 값이 그대로
            // 통해야 재시작할 때마다 다시 붙여 넣지 않습니다.
            var stored = McpTokenStore.Load();
            var reuse = stored is null ? null : new DiskMigrator.Mcp.McpReuse(stored.Token, stored.Port);

            var status = await _mcpHost.StartAsync(McpShareDetails, reuse);

            McpRunning = status.Running;
            McpUrl = status.Url ?? "";
            McpToken = status.Token ?? "";
            McpConnectorUrl = status.ConnectorUrl ?? "";

            // 실제로 되는 방법을 그대로 쓸 수 있게 만들어 둡니다 — 사용자가 조립하지 않아도 되게.
            McpAddCommand = status.Running
                ? $"claude mcp add --transport http diskmigrator-x {status.Url} " +
                  $"--header \"Authorization: Bearer {status.Token}\""
                : "";
            McpStatusText = Strings.Get(stored is null ? "McpRunningHint" : "McpRunningReusedHint");

            // 실제로 열린 포트를 보관합니다 — 지난번 포트가 막혀 다른 번호로 열렸을 수 있습니다.
            if (status is { Running: true, Token: { } t, Url: { } u } && TryParsePort(u) is { } p)
                McpTokenStore.Save(t, p);

            _logger.LogInformation("Claude 연결 통로를 열었습니다: {Url} (토큰 {Reused})",
                status.Url, stored is null ? "새로 발급" : "이어 씀");
        }
        catch (Exception ex)
        {
            // 통로를 못 열어도 앱의 다른 기능은 그대로 쓸 수 있어야 합니다.
            _logger.LogError(ex, "Claude 연결 통로 전환에 실패했습니다.");
            McpRunning = false;
            McpStatusText = Strings.Format("McpFailFmt", ex.Message);
        }
    }

    /// <summary>주소 문자열에서 포트를 꺼냅니다. 형식이 다르면 null.</summary>
    private static int? TryParsePort(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : null;

    /// <summary>
    /// 토큰을 버리고 새로 발급합니다 — 토큰이 샜다고 생각될 때.
    /// </summary>
    /// <remarks>
    /// 새 토큰을 쓰려면 통로를 다시 열어야 하고, 사용자는 Claude 설정의 값을 한 번 더
    /// 바꿔야 합니다. 그래서 자동으로 하지 않고 사용자가 명시적으로 누를 때만 합니다.
    /// </remarks>
    [RelayCommand]
    private async Task ResetMcpTokenAsync()
    {
        McpTokenStore.Clear();

        // 열려 있었다면 새 토큰으로 다시 엽니다 — 안 그러면 방금 버린 토큰으로 계속 통합니다.
        if (_mcpHost is { IsRunning: true })
        {
            await _mcpHost.StopAsync();
            McpRunning = false;
            await ToggleMcpAsync();
        }

        McpStatusText = Strings.Get("McpTokenReset");
        _logger.LogInformation("MCP 토큰을 새로 발급했습니다.");
    }

    /// <summary>주소와 토큰을 클립보드로 복사합니다 — 손으로 옮겨 적지 않게.</summary>
    [RelayCommand]
    private void CopyMcpSettings()
    {
        if (!McpRunning) return;
        try
        {
            System.Windows.Clipboard.SetText($"{McpUrl}\nAuthorization: Bearer {McpToken}");
            McpStatusText = Strings.Get("McpCopied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클립보드 복사에 실패했습니다.");
            McpStatusText = Strings.Format("McpFailFmt", ex.Message);
        }
    }

    /// <summary>연결 명령 한 줄을 복사합니다 — 확인된 유일한 방법.</summary>
    [RelayCommand]
    private void CopyMcpAddCommand()
    {
        if (!McpRunning) return;
        try
        {
            System.Windows.Clipboard.SetText(McpAddCommand);
            McpStatusText = Strings.Get("McpCommandCopied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클립보드 복사에 실패했습니다.");
            McpStatusText = Strings.Format("McpFailFmt", ex.Message);
        }
    }

    /// <summary>토큰이 실린 주소만 복사합니다 — 헤더를 못 보내는 도구용.</summary>
    [RelayCommand]
    private void CopyMcpConnectorUrl()
    {
        if (!McpRunning) return;
        try
        {
            System.Windows.Clipboard.SetText(McpConnectorUrl);
            McpStatusText = Strings.Get("McpConnectorCopied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클립보드 복사에 실패했습니다.");
            McpStatusText = Strings.Format("McpFailFmt", ex.Message);
        }
    }

    /// <summary>앱을 닫을 때 통로를 정리합니다.</summary>
    public async Task ShutdownMcpAsync()
    {
        if (_mcpHost is null) return;
        await _mcpHost.DisposeAsync();
        _mcpHost = null;
    }

    // --- Claude의 제안 (3단계 확인 게이트) --------------------------------

    /// <summary>지금 화면에 떠 있는 제안. 없으면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProposal))]
    private DiskMigrator.Mcp.Proposals.CloneProposal? _proposal;

    public bool HasProposal => Proposal is not null;

    /// <summary>
    /// 사용자가 제안을 받아들였습니다 — <b>여기서 처음으로 화면 값이 채워집니다.</b>
    /// </summary>
    /// <remarks>
    /// 계획서 §6.3의 1차 관문입니다. Claude는 카드를 띄우는 데까지이고, 값을 채우는 것은
    /// 사용자가 이 버튼을 누른 뒤 앱이 하는 일입니다. <b>모델명 확인란은 여기서도 건드리지
    /// 않습니다</b> — 그것이 2차 관문이며 사람만 채울 수 있어야 합니다.
    ///
    /// <para>적용 시점에 디스크를 다시 확인합니다. 제안을 만든 뒤 사용자가 USB를 바꿔 꽂았을 수
    /// 있고, 장치 번호는 그대로인데 다른 디스크일 수 있습니다.</para>
    /// </remarks>
    [RelayCommand]
    private void ApplyProposal()
    {
        var applied = _proposalStore?.MarkApplied();
        if (applied is null) return;

        // 지문으로 다시 찾습니다 — 번호가 아니라 정체로. 제안에 없는 쪽은 null입니다.
        var source = applied.Source is null ? null : Disks.FirstOrDefault(d => applied.Source.Matches(d.Disk));
        var target = applied.Target is null ? null : Disks.FirstOrDefault(d => applied.Target.Matches(d.Disk));

        // 제안이 가리키던 디스크가 사라졌으면 채우지 않습니다 — 엉뚱한 디스크에 적용되면 안 됩니다.
        if ((applied.Source is not null && source is null) ||
            (applied.Target is not null && target is null))
        {
            _logger.LogWarning("제안 적용 실패: 디스크를 다시 찾지 못했습니다 (제안 {Id})", applied.Id);
            McpStatusText = Strings.Get("ProposalDiskGone");
            return;
        }

        switch (applied.Kind)
        {
            case DiskMigrator.Mcp.Proposals.ProposalKind.Clone:
                Mode = AppMode.Clone;
                SelectedSource = source;
                SelectedTarget = target;
                UseSnapshot = applied.UseSnapshot && IsSnapshotAvailable;
                VerifyAfterClone = applied.VerifyAfterCopy;
                break;

            case DiskMigrator.Mcp.Proposals.ProposalKind.Backup:
                Mode = AppMode.Backup;
                SelectedSource = source;
                ImagePath = applied.ImagePath ?? "";
                UseSnapshot = applied.UseSnapshot && IsSnapshotAvailable;
                break;

            case DiskMigrator.Mcp.Proposals.ProposalKind.Restore:
                Mode = AppMode.Restore;
                SelectedTarget = target;
                ImagePath = applied.ImagePath ?? "";
                break;

            case DiskMigrator.Mcp.Proposals.ProposalKind.BootRepair:
                // 검사·복구는 사용자가 직접 눌러야 합니다 — 화면만 열어 둡니다.
                Mode = AppMode.FixBoot;
                SelectedTarget = target;
                break;
        }

        // ConfirmationText는 어느 경로에서도 채우지 않습니다.
        // 사람이 직접 입력해야 시작 버튼이 살아납니다.
        _logger.LogInformation("제안 {Id}({Kind}) 적용 — 확인·시작은 사용자 몫", applied.Id, applied.Kind);
    }

    /// <summary>사용자가 제안을 무시했습니다.</summary>
    [RelayCommand]
    private void DismissProposal()
    {
        _proposalStore?.MarkDismissed();
        _logger.LogInformation("제안을 무시했습니다.");
    }

    // --- 상태 -------------------------------------------------------------

    // CanStart는 계산 속성이라 PropertyChanged만으로는 버튼이 다시 평가되지 않습니다.
    // CommunityToolkit의 RelayCommand는 WPF의 CommandManager.RequerySuggested를 듣지 않으므로,
    // CanStart에 영향을 주는 모든 속성이 StartCommand에 직접 알려야 합니다.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreImageCommand))]
    [NotifyPropertyChangedFor(nameof(CanSwitchLanguage))]
    private AppStage _stage = AppStage.Selecting;

    /// <summary>
    /// 언어 전환 가능 여부. 전환은 창을 새로 만들어 다시 그리는 방식이라, 작업(클론·백업·복원·
    /// 부팅 USB 제작) 중에 하면 진행 화면이 사라지고 작업이 화면 없이 고아로 남습니다 —
    /// 그래서 작업 중에는 토글을 비활성화합니다.
    /// </summary>
    public bool CanSwitchLanguage => Stage != AppStage.Running && !IsPeBuilding;

    /// <summary>현재 작업 종류(클론/백업/복원). 시작 화면 상단에서 전환합니다.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    [NotifyPropertyChangedFor(nameof(IsCloneMode))]
    [NotifyPropertyChangedFor(nameof(IsBackupMode))]
    [NotifyPropertyChangedFor(nameof(IsRestoreMode))]
    [NotifyPropertyChangedFor(nameof(IsFixBootMode))]
    [NotifyPropertyChangedFor(nameof(IsBootUsbMode))]
    [NotifyPropertyChangedFor(nameof(IsAssistantMode))]
    [NotifyPropertyChangedFor(nameof(ShowCloneResultActions))]
    private AppMode _mode = AppMode.Clone;

    public bool IsCloneMode => Mode == AppMode.Clone;
    public bool IsBackupMode => Mode == AppMode.Backup;
    public bool IsRestoreMode => Mode == AppMode.Restore;
    public bool IsFixBootMode => Mode == AppMode.FixBoot;
    public bool IsBootUsbMode => Mode == AppMode.BootUsb;
    public bool IsAssistantMode => Mode == AppMode.Assistant;

    /// <summary>
    /// 완료 화면의 부팅 관련 후속 작업(부팅 검사·복구 등)을 보일지. 클론·복원 성공 시 보이고
    /// 백업 모드에선 숨깁니다(백업은 대상 디스크가 없어 부팅 검사가 무의미).
    /// </summary>
    public bool ShowCloneResultActions => ResultIsSuccess && !IsBackupMode;

    /// <summary>백업 저장 경로 또는 복원 원본 경로(.vhdx).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreImageCommand))]
    private string _imagePath = "";

    /// <summary>시작 화면 상단의 모드 전환 버튼에서 부릅니다.</summary>
    [RelayCommand]
    private void SetMode(string mode)
    {
        if (!Enum.TryParse<AppMode>(mode, out var m) || m == Mode) return;
        Mode = m;
        // 부팅 복구·부팅 USB의 결과는 그 모드의 화면 내용이므로, 모드를 떠나면 지웁니다
        // (다른 디스크로 돌아왔을 때 이전 결과가 남아 있으면 오해를 부릅니다).
        ResetBootCheck();
        PeRan = false;
        PeStatus = "";
        OnPropertyChanged(nameof(BootUsbBlockedReason));
        BuildBootUsbCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 백업 저장 위치를 고릅니다(.vhdx). WinPE에서는 자체 파일 창으로 대체됩니다.
    /// 기존 파일을 골라도 덮어쓰지 않고 증분 백업으로 이어지므로, 덮어쓰기 확인은 띄우지 않습니다.
    /// </summary>
    [RelayCommand]
    private void BrowseImageSave()
    {
        var path = Views.FileDialogs.PickSave(
            Strings.Get("BackupChoosePath"), Strings.Get("VhdxFilter"), ".vhdx", "backup.vhdx",
            overwritePrompt: false);
        if (path is not null) ImagePath = path;
    }

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string? _loadError;

    public bool IsElevated { get; }

    /// <summary>이 환경에서 VSS 스냅샷을 쓸 수 있는지. 새로고침할 때마다 다시 진단합니다.</summary>
    [ObservableProperty] private bool _isSnapshotAvailable;

    /// <summary>
    /// VSS를 쓸 수 없을 때 화면에 보여줄 사유·조치 문구(빈 문자열=사용 가능, 표시 안 함).
    /// </summary>
    /// <remarks>
    /// 예전에는 체크박스가 회색으로 잠기기만 해서, 사용자는 왜 안 되는지도 어떻게 고치는지도
    /// 알 수 없었습니다(실기에서 실제로 막힘). 이유와 조치를 함께 보여 줍니다.
    /// </remarks>
    [ObservableProperty] private string _snapshotUnavailableText = "";

    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DiskItemViewModel? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreImageCommand))]
    [NotifyPropertyChangedFor(nameof(RestoreConfirmPrompt))]
    [NotifyPropertyChangedFor(nameof(ShrinkAutoText))]
    [NotifyPropertyChangedFor(nameof(HasShrinkAutoText))]
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

    /// <summary>
    /// 원본이 BIOS/MBR 전용 배치일 때, 클론 후 대상을 자동으로 GPT/UEFI로 변환할지.
    /// </summary>
    /// <remarks>
    /// MBR 사본은 NVMe·UEFI 전용 PC에서 부팅되지 않습니다(레거시 옵션 ROM 없음). 이 옵션을 켜면
    /// 완료 화면의 'UEFI로 변환' 버튼을 누를 필요 없이 클론 직후 자동으로 변환합니다.
    /// 되돌릴 수 없는 파티션 테이블 변경이라 기본은 꺼짐입니다 — 사용자가 명시적으로 켭니다.
    /// GPT 원본에는 아무 영향이 없습니다(변환 대상이 아니므로 건너뜀).
    /// </remarks>
    [ObservableProperty] private bool _autoConvertUefi;

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
    /// 원본 파티션 끝 뒤로 남는 공간이 있어 확대할 여지가 있는지. 리사이즈는 <b>GPT 원본</b>만,
    /// 그리고 원본과 대상의 <b>논리 섹터 크기가 같을 때</b>만 지원합니다(GPT 엔트리 위치가 LBA
    /// 단위라 섹터 크기가 다르면 재배치가 어긋납니다).
    /// </summary>
    public bool CanResize =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedSource.Disk.PartitionStyle is PartitionStyle.Gpt or PartitionStyle.Mbr &&
        SelectedSource.Disk.LogicalSectorSize == SelectedTarget.Disk.LogicalSectorSize &&
        SelectedTarget.Disk.SizeBytes - SelectedSourceOccupiedEnd >= DiskLayoutMap.GapNoiseThreshold &&
        !SelectedSource.Disk.HasExtendedPartition &&
        !ExceedsMbrLimit &&
        ResizablePartitions.Count > 0;

    /// <summary>
    /// 원본 파티션이 실제로 끝나는 지점(마지막 파티션의 끝). 확대·확장 여지는 원본 <b>디스크
    /// 크기</b>가 아니라 이 값을 기준으로 판단해야 합니다 — 예컨대 250GB만 쓰고 나머지가 미할당인
    /// 1TB 원본을 같은 크기 1TB 대상에 클론할 때도, 파티션 끝 뒤의 750GB를 남는 공간으로 옳게
    /// 인식하기 위함입니다(디스크 크기끼리 비교하면 "같은 크기 → 남는 공간 없음"으로 오판합니다).
    /// </summary>
    private long SelectedSourceOccupiedEnd =>
        SelectedSource is null ? 0 : DiskLayoutMap.OccupiedEnd(SelectedSource.Disk);

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
                return Strings.Format("RbNotGptFmt",
                    SelectedSource.Disk.PartitionStyle.ToString().ToUpperInvariant());
            }

            if (SelectedSource.Disk.HasExtendedPartition)
            {
                return Strings.Get("RbExtended");
            }

            if (ExceedsMbrLimit)
            {
                return Strings.Get("RbMbr2tb");
            }

            if (SelectedSource.Disk.LogicalSectorSize != SelectedTarget.Disk.LogicalSectorSize)
            {
                return Strings.Format("RbSectorMismatchFmt",
                    SelectedSource.Disk.LogicalSectorSize, SelectedTarget.Disk.LogicalSectorSize);
            }

            if (ResizablePartitions.Count == 0)
                return Strings.Get("RbNoNtfs");

            return Strings.Get("RbNoRoom");
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
            ? Strings.Get("ReagentcNote")
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
    [NotifyCanExecuteChangedFor(nameof(RestoreImageCommand))]
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

    public string PauseButtonText => IsPaused ? Strings.Get("PauseResume") : Strings.Get("PausePause");

    // --- 결과 --------------------------------------------------------------

    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveryHint))]
    [NotifyPropertyChangedFor(nameof(HasRecoveryHint))]
    [NotifyPropertyChangedFor(nameof(ShowCloneResultActions))]
    private bool _resultIsSuccess;
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private string? _logFilePath;

    // --- 부팅 구성 검사 (클론 후) ------------------------------------------

    /// <summary>부팅 구성 검사 결과 항목들.</summary>
    public ObservableCollection<BootCheckItemViewModel> BootCheckItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BootCheckCommand))]
    private bool _isBootChecking;

    // 대상이 없으면 버튼을 눌러도 조용히 아무 일도 안 하게 되므로(초보자 혼란) 비활성으로 막습니다.
    public bool CanBootCheck => !IsBootChecking && SelectedTarget is not null;

    /// <summary>검사를 한 번이라도 실행해 결과 패널을 보여줄지.</summary>
    [ObservableProperty] private bool _bootCheckRan;

    [ObservableProperty] private string _bootCheckVerdict = "";

    /// <summary>판정이 긍정(부팅 준비/가능)이면 true — 색 구분용.</summary>
    [ObservableProperty] private bool _bootCheckVerdictIsGood;

    // --- 진단 리포트 저장 (오프라인 브리지) --------------------------------
    //
    // 부팅이 막힌 PC에는 Claude도 인터넷도 없습니다. 그 PC에서 이 버튼으로 진단을 파일 하나에
    // 모아 USB로 옮기면, 정상 PC에서 그 파일을 읽어 분석할 수 있습니다. 이 버튼이 없으면
    // 진단 파일을 만드는 유일한 방법이 Claude 도구가 되어, "PE에는 Claude가 없다"는 전제와
    // 모순됩니다 — 정작 필요한 곳에서 쓸 수 없게 됩니다.

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDiagnosticCommand))]
    private bool _isCollectingDiagnostic;

    public bool CanSaveDiagnostic => !IsCollectingDiagnostic && SelectedTarget is not null;

    /// <summary>수집·저장 결과 안내(성공하면 경로 포함).</summary>
    [ObservableProperty] private string _diagnosticMessage = "";

    [ObservableProperty] private bool _diagnosticSaved;

    /// <summary>실패했으면 true — 색 구분용.</summary>
    [ObservableProperty] private bool _diagnosticFailed;

    /// <summary>
    /// 시리얼·볼륨 이름을 가리지 않고 담을지. 기본은 가립니다.
    /// </summary>
    /// <remarks>
    /// 이 파일은 남에게 보내려고 만드는 것입니다 — 포럼에 올리거나 Claude에게 보여줍니다.
    /// 기본을 노출로 두면 사용자가 의식하지 못한 채 식별 정보를 내보내게 됩니다.
    /// (Claude 연결의 공유 옵션과는 별개입니다. 저장은 저장 시점에 정합니다.)
    /// </remarks>
    [ObservableProperty] private bool _diagIncludeDetails;

    // --- 부팅 복구 (BCD 장치 참조 수정) ------------------------------------

    /// <summary>검사에서 BCD 장치 참조 문제가 잡혀 복구 버튼을 보여줄지.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForceRepair))]
    private bool _bootRepairAvailable;

    /// <summary>
    /// 검사가 <b>확인하지 못한</b> 치명 항목이 있는지(디스크 오프라인, 볼륨 미마운트 등).
    /// </summary>
    /// <remarks>
    /// "문제를 찾지 못했다"와 "확인하지 못했다"는 전혀 다릅니다. 후자를 전자로 말하면
    /// 사용자는 읽지도 못한 디스크에 쓰기를 걸게 됩니다.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForceRepair))]
    private bool _bootCheckInconclusive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairBootCommand))]
    [NotifyPropertyChangedFor(nameof(ShowForceRepair))]
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUefiConvertSection))]
    private bool _uefiConvertAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertToUefiCommand))]
    private bool _isConvertingToUefi;

    public bool CanConvertToUefi => !IsConvertingToUefi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUefiConvertSection))]
    private bool _uefiConvertRan;
    [ObservableProperty] private string _uefiConvertMessage = "";
    [ObservableProperty] private bool _uefiConvertSuccess;

    /// <summary>
    /// UEFI 변환 영역을 보여줄지 — 변환 가능(버튼 노출)하거나 이미 실행됨(결과 메시지)일 때.
    /// 자동 변환이 성공하면 UefiConvertAvailable이 꺼지므로, 결과 메시지가 사라지지 않도록
    /// Ran도 함께 봅니다.
    /// </summary>
    public bool ShowUefiConvertSection => UefiConvertAvailable || UefiConvertRan;

    // --- 대상 안전하게 제거 ------------------------------------------------
    //
    // 클론이 끝나면 세션 정리가 대상을 다시 온라인으로 올려, Windows가 복제된 볼륨을 자동
    // 마운트합니다. 그 상태에서 이동식(USB) 대상을 "안전하게 제거"하면 Windows가 "장치
    // 사용 중"이라며 막습니다. 이 버튼이 볼륨을 디스마운트하고 디스크를 오프라인으로 내려,
    // 사용자가 USB를 그대로 뽑아도 안전하게 만듭니다(디스크 관리의 "오프라인"과 같은 동작).

    /// <summary>대상이 이동식(USB)이라 "안전하게 제거" 버튼을 보여줄지.</summary>
    [ObservableProperty] private bool _safeRemoveAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SafeRemoveTargetCommand))]
    private bool _isSafeRemoving;

    public bool CanSafeRemove => !IsSafeRemoving;

    [ObservableProperty] private bool _safeRemoveRan;
    [ObservableProperty] private string _safeRemoveMessage = "";
    [ObservableProperty] private bool _safeRemoveSuccess;

    // --- 명령 --------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshDisksAsync()
    {
        IsLoading = true;
        LoadError = null;



        // VSS 상태도 함께 다시 진단합니다 — 사용자가 서비스를 켜거나 런타임을 설치한 뒤
        // 새로고침하면 앱을 다시 켜지 않아도 반영되도록.
        RefreshSnapshotAvailability();

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

            // 대기 중인 제안이 아직 같은 디스크를 가리키는지 확인합니다. 제안을 만든 뒤
            // 사용자가 USB를 바꿔 꽂았을 수 있고, 장치 번호는 그대로인데 다른 디스크일 수 있습니다.
            _proposalStore?.InvalidateIfDisksChanged(disks);

            _logger.LogInformation("디스크 {Count}개를 찾았습니다.", disks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "디스크 목록을 읽지 못했습니다.");
            LoadError = Strings.Format("LoadErrorFmt", ex.Message);
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
        OnPropertyChanged(nameof(BootUsbBlockedReason));
        BuildBootUsbCommand.NotifyCanExecuteChanged();
        BootCheckCommand.NotifyCanExecuteChanged();
        SaveDiagnosticCommand.NotifyCanExecuteChanged();
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

        // 대상이 바뀌면 '새 총 크기'에 남아 있던 값이 새 대상 범위를 벗어날 수 있습니다 —
        // 더 큰 대상에서 정한 값을 그대로 두고 작은 대상으로 바꾸면 "용량 초과"로 거부돼
        // 리사이즈가 아예 불가능한 것처럼 보입니다. 범위를 벗어난 값은 최대치로 맞춥니다.
        ClampResizeSizeToBounds();
    }

    /// <summary>'새 총 크기' 입력이 현재 대상에서 가능한 범위를 벗어나면 맞춥니다.</summary>
    private void ClampResizeSizeToBounds()
    {
        if (SelectedResizePartition is not { } choice) return;
        if (ResizeBounds(choice) is not var (min, max) || max <= min) return;
        if (!FreeSpacePlanner.TryParseSizeGb(ResizeSizeGb, out double gb) || gb <= 0) return;

        long bytes = (long)(gb * FreeSpacePlanner.BytesPerGb);

        if (bytes >= max)
        {
            // 최대 이상은 '남는 공간 전부'로 전환합니다 — GB 문자열로 반올림해 담으면 부동소수점
            // 왕복으로 실제 최대치를 미세하게 넘겨 "용량 초과"가 뜹니다. fill 모드는 정확한 최대치를
            // 씁니다.
            ResizeFillRemaining = true;
        }
        else if (bytes < min)
        {
            // 현재 크기 이하는 현재 크기로 올려 맞춥니다(축소 미지원). min은 정확히 표현됩니다.
            ResizeSizeGb = ((double)min / FreeSpacePlanner.BytesPerGb)
                .ToString("0.##", CultureInfo.CurrentCulture);
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

    /// <summary>원본 파티션 끝 뒤로 대상에 남는 공간이 생기는지(원본 꼬리 미할당까지 포함).</summary>
    public bool HasFreeSpace =>
        SelectedSource is not null && SelectedTarget is not null &&
        SelectedTarget.Disk.SizeBytes - SelectedSourceOccupiedEnd >= DiskLayoutMap.GapNoiseThreshold;

    /// <summary>"남는 공간 2.73 TB 를 어떻게 할까요" — 무엇에 대한 선택인지 바로 알 수 있게.</summary>
    public string FreeSpaceText
    {
        get
        {
            if (!HasFreeSpace) return "";
            long free = SelectedTarget!.Disk.SizeBytes - SelectedSourceOccupiedEnd;
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
    /// 넓힐 파티션이 <b>현재(최소) 크기</b>일 때 경계의 가로 위치(비율). 이 아래로는 줄일 수
    /// 없으므로 막대에 빨간 점선으로 표시해 "여기까지가 데이터 용량"임을 알립니다.
    /// </summary>
    [ObservableProperty] private double _resizeMinFraction;

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

        // 현재(최소) 크기 경계 = 넓힐 파티션 왼쪽 시작 + 현재 크기만큼의 비율.
        // 손잡이는 이 왼쪽으로 못 가며, 막대에 빨간 점선으로 이 위치를 표시합니다.
        double startFraction = segments.Take(growIndex).Sum(s => s.Fraction);
        ResizeMinFraction = ResizeBytesPerFraction > 0
            ? startFraction + min / ResizeBytesPerFraction
            : startFraction;

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
        OnPropertyChanged(nameof(BootUsbBlockedReason));
        BuildBootUsbCommand.NotifyCanExecuteChanged();
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
        ResetPostCloneActions();
        _ = RefreshDisksAsync();
    }

    /// <summary>
    /// 치명 실패가 BCD 장치 참조(0xc000000e) 하나뿐인지. 그 경우는 재서명으로 어긋난 참조만
    /// 고치면 부팅되므로 클론 직후 자동 복구가 안전합니다. 다른 치명 실패(부트로더 누락 등)가
    /// 함께 있으면 자동 복구로 해결되지 않으므로 사용자 판단에 맡깁니다.
    /// </summary>
    private bool _deviceRefIsOnlyFatalFailure;

    /// <summary>
    /// 부팅 구성 검사의 표시 상태만 초기화합니다. 후속 작업 버튼(UEFI 변환·파티션 확장·안전
    /// 제거)은 건드리지 않습니다 — 검사를 한 번 돌렸다고 그 버튼들이 사라지면 안 됩니다.
    /// </summary>
    private void ResetBootCheck()
    {
        BootCheckItems.Clear();
        BootCheckRan = false;
        BootCheckVerdict = "";
        BootCheckVerdictIsGood = false;
        BootRepairAvailable = false;
        BootCheckInconclusive = false;
        BootRepairRan = false;
        BootRepairMessage = "";
        _deviceRefIsOnlyFatalFailure = false;
    }

    /// <summary>
    /// 완료 화면의 후속 작업 버튼(UEFI 변환·파티션 확장·안전 제거) 상태를 초기화합니다.
    /// 새 선택으로 돌아가거나 새 클론을 시작할 때만 부릅니다 — 부팅 검사는 부르지 않습니다.
    /// </summary>
    private void ResetPostCloneActions()
    {
        PartitionExpandAvailable = false;
        PartitionExpandRan = false;
        PartitionExpandMessage = "";
        PartitionExpandSuccess = false;
        UefiConvertAvailable = false;
        UefiConvertRan = false;
        UefiConvertMessage = "";
        UefiConvertSuccess = false;
        SafeRemoveAvailable = false;
        SafeRemoveRan = false;
        SafeRemoveMessage = "";
        SafeRemoveSuccess = false;
    }

    /// <summary>
    /// 선택한 디스크의 부팅 진단을 파일 하나로 모아 저장합니다 — 오프라인 브리지.
    /// </summary>
    /// <remarks>
    /// 부팅이 막힌 PC에는 Claude도 인터넷도 없습니다. 그 PC에서 이 파일을 만들어 USB로 옮기면
    /// 정상 PC에서 분석할 수 있습니다. 조치 전후로 두 번 저장해 두면 무엇이 바뀌었는지도
    /// 비교할 수 있습니다.
    ///
    /// <para><b>수집은 순수 읽기입니다.</b> 진단 대상 디스크에는 아무것도 쓰지 않으며,
    /// 리포트를 그 디스크에 저장하는 것도 막습니다 — 부팅이 막힌 디스크는 이미 상태가
    /// 위태로울 수 있고, 진단이 그것을 더 건드려서는 안 됩니다.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSaveDiagnostic))]
    private async Task SaveDiagnosticAsync()
    {
        if (SelectedTarget is null) return;

        DiagnosticSaved = false;
        DiagnosticFailed = false;
        DiagnosticMessage = "";

        var target = await ResolveCurrentTargetAsync();
        if (target is null)
        {
            DiagnosticFailed = true;
            DiagnosticSaved = true;
            DiagnosticMessage = Strings.Get("TargetNotFoundAgain");
            return;
        }

        string defaultName = $"diag-disk{target.DeviceNumber}-{DateTime.Now:yyyyMMdd-HHmm}.dmdiag";
        string? path = Views.FileDialogs.PickSave(
            Strings.Get("DiagSaveTitle"), Strings.Get("DiagFileFilter"), ".dmdiag", defaultName);
        if (path is null) return;   // 사용자가 취소

        // 진단 대상 디스크에는 저장할 수 없습니다.
        if (IsOnDisk(path, target))
        {
            DiagnosticFailed = true;
            DiagnosticSaved = true;
            DiagnosticMessage = Strings.Get("DiagNotOnTarget");
            return;
        }

        IsCollectingDiagnostic = true;
        try
        {
            var collector = new DiagnosticCollector(_diskService, _loggerFactory.CreateLogger<MainViewModel>());
            var report = await Task.Run(() => collector.CollectAsync(target.DeviceNumber, DiagIncludeDetails));
            await collector.SaveAsync(report, path);

            long size = new FileInfo(path).Length;
            DiagnosticFailed = false;
            DiagnosticSaved = true;
            DiagnosticMessage = Strings.Format("DiagSavedFmt", path, size / 1024.0);
            _logger.LogInformation("진단 리포트 저장: 디스크 {Number} → {Path} ({Size:N0} bytes)",
                target.DeviceNumber, path, size);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "진단 리포트 저장에 실패했습니다.");
            DiagnosticFailed = true;
            DiagnosticSaved = true;
            DiagnosticMessage = Strings.Format("DiagFailFmt", ex.Message);
        }
        finally
        {
            IsCollectingDiagnostic = false;
        }
    }

    /// <summary>저장 경로가 진단 대상 디스크 위인지 — 드라이브 문자로 대조합니다.</summary>
    private static bool IsOnDisk(string path, DiskInfo disk)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        if (root.Length == 0) return false;

        return disk.Partitions.Any(p =>
            p.DriveLetter is { } letter &&
            root.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
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

            // 오프라인 디스크는 볼륨이 마운트되지 않아 ESP도 Windows 폴더도 "없는 것처럼"
            // 보입니다. 그것을 부팅 결함으로 읽으면 멀쩡한 디스크를 고치겠다고 덤비게 되므로,
            // 검사 결과보다 먼저 "지금은 검사 자체가 불가능하다"고 말해야 합니다.
            (BootCheckVerdict, BootCheckVerdictIsGood) = target.IsOffline
                ? (Strings.Get("VerdictDiskOffline"), false)
                : (report.WouldBoot, report.HasWarnings, anyFatalFailed) switch
                {
                    (true, false, _) => (Strings.Get("VerdictReady"), true),
                    (true, true, _) => (Strings.Get("VerdictReadyWarn"), true),
                    (false, _, true) => (Strings.Get("VerdictNoBoot"), false),
                    _ => (Strings.Get("VerdictUnknown"), false),
                };

            // 확인하지 못한 치명 항목이 있으면 "문제를 찾지 못했다"고 말할 수 없습니다.
            // 이것은 아래 '그래도 복구 실행' 안내에만 씁니다 — 그 안내의 전제가 바로
            // "검사가 끝까지 돌았고 아무 문제도 없었다"이기 때문입니다.
            BootCheckInconclusive = target.IsOffline || report.Items.Any(i =>
                i.Severity == BootCheckSeverity.Fatal && i.Passed is null);

            // 부팅 복구(BootRepair)가 고칠 수 있는 실패가 있으면 복구 버튼을 제안합니다:
            // BCD 장치 참조 불일치(0xc000000e)와 최대 절전 이미지 잔존(재개를 끄고 hiberfil 삭제).
            // 이름은 언어에 따라 바뀌므로 안정 코드로 판별합니다.
            //
            // 실제로 '찾아낸' 문제는 다른 항목을 못 봤다고 없던 일이 되지 않으므로,
            // 여기에는 위 조건을 걸지 않습니다. 걸면 고칠 수 있는 디스크에서 버튼이 사라집니다.
            BootRepairAvailable = report.Items.Any(i =>
                i.Passed == false && IsRepairableCode(i.Code));

            // 치명 실패가 전부 복구로 고칠 수 있는 항목뿐이면(다른 치명 항목은 모두 통과)
            // 클론 직후 자동 복구가 안전합니다 — 고치면 부팅되기 때문입니다.
            _deviceRefIsOnlyFatalFailure = BootRepairAvailable && report.Items.All(i =>
                i.Severity != BootCheckSeverity.Fatal ||
                i.Passed == true ||
                IsRepairableCode(i.Code));

            BootCheckRan = true;
            OnPropertyChanged(nameof(ShowForceRepair));
            _logger.LogInformation("부팅 구성 검사: {Verdict}", BootCheckVerdict);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 구성 검사에 실패했습니다.");
            BootCheckVerdict = Strings.Format("BootCheckFailFmt", ex.Message);
            BootCheckVerdictIsGood = false;
            BootCheckRan = true;
        }
        finally
        {
            IsBootChecking = false;
        }
    }

    /// <summary>
    /// 검사에서 문제를 못 찾았어도 <b>그래도 복구를 해 볼 수 있게</b> 안내·버튼을 보일지.
    /// </summary>
    /// <remarks>
    /// 부팅 구성이 모두 정상인데 실제로는 부팅이 막히는 경우가 있습니다(원본에 설치된 보안·DRM
    /// 드라이버 등 — 검사 범위 밖). 이때도 복구를 돌리면 하드웨어 독립화와 쓰기 확정이 다시
    /// 적용되므로 시도할 가치가 있습니다. 검사가 문제를 찾은 경우엔 기존 복구 버튼이 나오므로
    /// 이 안내는 숨깁니다.
    /// </remarks>
    public bool ShowForceRepair =>
        BootCheckRan && !BootCheckInconclusive && !BootRepairAvailable && !IsRepairingBoot;

    /// <summary>부팅 복구가 고칠 수 있는 검사 항목 코드인지 — 장치 참조·최대 절전 이미지.</summary>
    private static bool IsRepairableCode(string? code) =>
        code is BootReadinessCheck.CodeDeviceRef or BootReadinessCheck.CodeHibernation;

    /// <summary>
    /// 클론의 BCD 장치 참조를 이 디스크의 파티션으로 다시 설정해 0xc000000e를 고치고,
    /// 최대 절전(빠른 시작) 재개를 꺼서 hiberfil.sys 잔존도 함께 정리합니다.
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

            // 지금 실행 중인 시스템 디스크에는 쓰지 않습니다. 클론/복원 대상은 어차피 시스템
            // 디스크가 될 수 없지만, 독립 부팅 복구 도구에서는 사용자가 아무 디스크나 고를 수
            // 있으므로 여기서 막습니다 — 이 컴퓨터가 그 디스크로 이미 부팅돼 있다면 BCD는
            // 정상이고, 건드릴 이유가 없습니다.
            if (target.IsSystemDisk || target.IsBootDisk)
            {
                BootRepairMessage = Strings.Get("FixBootBlockedSystem");
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
            BootRepairMessage = Strings.Format("BootRepairFailFmt", ex.Message);
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
            PartitionExpandMessage = Strings.Format("ExpandFailFmt", ex.Message);
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
            UefiConvertMessage = Strings.Format("UefiConvertFailFmt", ex.Message);
            UefiConvertSuccess = false;
            UefiConvertRan = true;
        }
        finally
        {
            IsConvertingToUefi = false;
        }
    }

    /// <summary>
    /// 이동식 대상 디스크의 볼륨을 내리고 오프라인으로 전환해, 사용자가 USB를 안전하게 뽑을 수
    /// 있게 합니다. 복제 데이터는 이미 대상에 온전히 쓰인 뒤이므로 손상 위험은 없습니다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSafeRemove))]
    private async Task SafeRemoveTargetAsync()
    {
        if (SelectedTarget is null) return;

        IsSafeRemoving = true;
        SafeRemoveRan = false;
        try
        {
            // 클론·재연결로 장치 번호가 바뀌었을 수 있어 신원으로 다시 찾습니다. 못 찾으면
            // (이미 뽑혔거나 사라졌으면) 처음 선택했던 정보로 시도합니다.
            var target = await ResolveCurrentTargetAsync() ?? SelectedTarget.Disk;

            var result = await _diskService.SafeRemoveAsync(target);

            SafeRemoveSuccess = result.Success;
            SafeRemoveMessage = result.Success
                ? Strings.Get("SafeRemoveDone")
                : Strings.Format("SafeRemoveFailFmt", result.ErrorDetail ?? "");
            SafeRemoveRan = true;

            _logger.LogInformation(
                "대상 안전 제거: 성공={Success} {Detail}", result.Success, result.ErrorDetail ?? "");

            // 오프라인으로 내렸으면 더 할 일이 없으므로 버튼을 감춥니다.
            if (result.Success) SafeRemoveAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "대상 안전 제거에 실패했습니다.");
            SafeRemoveMessage = Strings.Format("SafeRemoveFailFmt", ex.Message);
            SafeRemoveSuccess = false;
            SafeRemoveRan = true;
        }
        finally
        {
            IsSafeRemoving = false;
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
            ShowFailure(Strings.Get("FailFreeSpaceTitle"), planError,
                Strings.Get("FailNothingWritten"));
            return;
        }

        var freeSpaceMode = plan.Mode;
        var growRequest = plan.Grow;
        _grownPartitionNumber = growRequest?.PartitionNumber;

        // 대상이 원본보다 작고 제자리(맞춤 클론)로도 안 들어가면 축소 클론으로 라우팅합니다 —
        // 내부적으로 백업→축소→복원(원본 무수정). SafetyGuard가 같은 판정으로 확인을 받았지만,
        // 대상에 쓰는 작업이므로 여기서 한 번 더 계산해 어긋나면 시작하지 않습니다.
        ShrinkCloneDecision? shrinkClone = null;
        string? shrinkTempImage = null;
        if (target.SizeBytes < source.SizeBytes &&
            !(source.PartitionStyle == PartitionStyle.Gpt && source.Partitions.Count > 0 &&
              ResizePlanner.LayoutFitsIn(source.Partitions, target.SizeBytes)))
        {
            shrinkClone = ShrinkClonePlanner.Evaluate(source.Partitions, target.SizeBytes, out string? shrinkBlocked);
            if (shrinkClone is null)
            {
                Stage = AppStage.Finished;
                ShowFailure(Strings.Get("FailSafetyTitle"),
                    shrinkBlocked ?? Strings.Get("BlockSafetyFailed"), Strings.Get("FailNothingWritten"));
                return;
            }

            // 임시 백업 이미지를 둘 곳 — 원본·대상이 아닌 디스크 중 여유가 가장 큰 볼륨.
            shrinkTempImage = FindShrinkTempImagePath(source, target);
            if (shrinkTempImage is null)
            {
                long needed = EstimateUsedBytes(source) + (10L << 30);
                Stage = AppStage.Finished;
                ShowFailure(Strings.Get("FailShrinkTempTitle"),
                    Strings.Format("ShrinkTempNoneFmt", SizeFormatter.Format(needed)),
                    Strings.Get("FailNothingWritten"));
                return;
            }
        }

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
        ResetPostCloneActions();
        BadSectorCount = 0;
        ProgressPercent = 0;
        ProgressPhase = Strings.Get("ProgPreparing");
        ProgressRegion = UseSnapshot ? Strings.Get("ProgSnapshotting") : Strings.Get("ProgLockingTarget");
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
            if (shrinkClone is not null && shrinkTempImage is not null)
            {
                // 축소 클론 — 쓰기 직전 최종 관문(복원 경로와 동일)을 밟은 뒤 실행합니다.
                var fresh = await ResolveCurrentTargetAsync();
                SafetyGuard.AssertTargetUnchanged(target, fresh);

                var shrinkSvc = new ShrinkCloneService(_diskService, _snapshotProvider, _loggerFactory);
                var shrinkReport = await shrinkSvc.RunAsync(
                    source, target, shrinkClone, shrinkTempImage,
                    UseSnapshot, UniversalRestore, options, progress, _pause, _cts.Token);

                ShowResult(new CloneJobReport
                {
                    Result = shrinkReport.Result,
                    Source = source,
                    Target = target,
                    GptRepair = shrinkReport.GptRepair,
                    UniversalRestore = shrinkReport.UniversalRestore,
                });
            }
            else
            {
                var orchestrator = new CloneOrchestrator(_diskService, _snapshotProvider, _loggerFactory);

                var report = await orchestrator.RunAsync(
                    source, target, UseSnapshot, options, UniversalRestore,
                    progress, _pause, _cts.Token);

                ShowResult(report);
            }
        }
        catch (SafetyViolationException ex)
        {
            _logger.LogError(ex, "안전 검사에 걸려 작업이 중단되었습니다.");
            ShowFailure(Strings.Get("FailSafetyTitle"), ex.Message,
                Strings.Get("FailNothingWritten"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "권한 부족으로 작업이 실패했습니다.");
            ShowFailure(Strings.Get("FailNoPrivTitle"), ex.Message,
                Strings.Get("FailNoPrivDetail"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "작업이 실패했습니다.");
            ShowFailure(Strings.Get("ResTitleFailed"), ex.Message,
                Strings.Get("FailSeeLog"));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pause?.Dispose();
            _pause = null;
            Stage = AppStage.Finished;
        }

        // 클론이 성공했으면 부팅 검사를 자동으로 실행합니다.
        //
        // 예전엔 사용자가 완료 화면에서 '부팅 구성 검사'를 눌러야만 돌았습니다. 그래서 그 단계를
        // 건너뛰고 대상을 옮기면, 원본·대상 동시 연결로 재서명돼 어긋난 BCD 장치 참조(0xc000000e)를
        // 못 잡고 부팅이 실패했습니다. 이제 클론 직후 자동으로 검사합니다. 검사는 읽기 전용이라
        // 안전하며, BootCheckAsync가 자체적으로 예외를 삼키므로 여기서 실패해도 흐름이 깨지지
        // 않습니다.
        if (ResultIsSuccess)
        {
            // 'BIOS 원본을 UEFI로 자동 변환' 옵션이 켜져 있고 원본이 BIOS 전용 배치이면(UefiConvertAvailable),
            // 부팅 검사 전에 먼저 GPT/UEFI로 변환합니다. MBR 사본은 NVMe·UEFI 전용 PC에서 부팅되지
            // 않으므로, 이 변환까지 마쳐야 검사가 실제 부팅 상태를 반영합니다. 변환은 되돌릴 수 없어
            // 옵션이 켜졌을 때만 자동 실행하며, ConvertToUefiAsync가 예외를 삼켜 흐름을 깨지 않습니다.
            if (AutoConvertUefi && UefiConvertAvailable)
            {
                _logger.LogInformation("자동 UEFI 변환: 옵션 켜짐 + BIOS 전용 원본 — 자동 변환을 실행합니다.");
                await ConvertToUefiAsync();
            }

            await BootCheckAsync();

            // 치명 실패가 BCD 장치 참조 하나뿐이면(재서명으로 인한 0xc000000e) 자동으로 복구합니다.
            //
            // 우리 주 사용자 흐름 — 원본과 대상을 함께 연결해 복제한 뒤 대상을 새 PC로 옮기는 —
            // 에서는 두 디스크의 식별자가 충돌해 Windows가 대상을 재서명하므로 이 불일치가 거의
            // 항상 발생합니다. 예전에는 사용자가 '부팅 복구'를 직접 눌러야 했고, 모르면 "복제했는데
            // 안 켜진다"가 됐습니다. BootRepair는 bcdedit /store로 클론의 BCD 저장소만 손대고
            // 라이브 시스템은 건드리지 않으며(BootRepair.Repair), 복구 성공 시 자동으로 재검사하므로
            // 자동 실행이 안전합니다. 다른 치명 실패가 함께 있으면 자동 복구로 해결되지 않으니
            // 실행하지 않고 사용자 판단에 맡깁니다.
            if (_deviceRefIsOnlyFatalFailure)
            {
                _logger.LogInformation("자동 부팅 복구: 치명 실패가 BCD 장치 참조 하나뿐 — 자동 복구를 실행합니다.");
                await RepairBootAsync();
            }
        }
    }

    private void OnProgress(CloneProgress p)
    {
        ProgressPercent = p.Percent;
        ProgressPhase = p.Phase;
        ProgressRegion = p.CurrentRegion;
        ProgressBytes = $"{SizeFormatter.Format(p.BytesProcessed)} / {SizeFormatter.Format(p.TotalBytes)}";
        ProgressSpeed = SizeFormatter.FormatSpeed(p.SpeedBytesPerSecond);
        ProgressEta = p.Eta is { } eta ? SizeFormatter.FormatDuration(eta) : Strings.Get("EtaCalculating");
        ProgressElapsed = SizeFormatter.FormatDuration(p.Elapsed);
        BadSectorCount = p.BadSectorCount;
    }

    private void ShowResult(CloneJobReport report)
    {
        var result = report.Result;
        var details = new List<string>();

        details.Add(Strings.Format("ResCopiedFmt", SizeFormatter.Format(result.BytesCopied)));
        details.Add(Strings.Format("ResDurationFmt", SizeFormatter.FormatDuration(result.Duration)));
        details.Add(Strings.Format("ResSpeedFmt", SizeFormatter.FormatSpeed(result.AverageSpeedBytesPerSecond)));

        if (report.SnapshotTimeUtc is { } snapshotTime)
        {
            details.Add(Strings.Format("ResSnapshotTimeFmt",
                snapshotTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
        }

        if (report.UnsnapshottedPartitions.Count > 0)
        {
            details.Add(Strings.Format("ResUnsnapshottedFmt", string.Join(", ", report.UnsnapshottedPartitions)));
        }

        details.Add(result.VerificationPassed switch
        {
            true => Strings.Get("ResVerifyPass"),
            false => Strings.Get("ResVerifyFail"),
            null => Strings.Get("ResVerifyNone"),
        });

        if (result.BadSectors.Count > 0)
        {
            details.Add(Strings.Format("ResBadSectorsFmt", result.BadSectors.Count));
        }

        if (report.GptRepair is { } gpt)
        {
            // 라벨을 "GPT"로 박아 두면 MBR 클론에서 "GPT: MBR 파티션 테이블을 다시 썼습니다"가
            // 됩니다. 내부 필드 이름(GptRepair)을 화면 글자로 그대로 쓴 탓이었습니다.
            details.Add(Strings.Format("ResPartTableFmt", gpt.Description));
        }

        if (report.UniversalRestore is { } ur)
        {
            details.Add(Strings.Format("ResNewHwFmt", ur.Message));
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
                                   report.Target.SizeBytes - DiskLayoutMap.OccupiedEnd(report.Source) >= DiskLayoutMap.GapNoiseThreshold &&
                                   !alreadyExpanded;

        // 원본이 BIOS 전용 배치였으면 사본도 그렇습니다. 요즘 PC(특히 NVMe)에서는 부팅하지
        // 않으므로 GPT/UEFI 변환을 제안합니다. 되돌릴 수 없는 변경이라 자동으로 하지 않습니다.
        UefiConvertAvailable = ResultIsSuccess && UefiConverter.NeedsConversion(report.Source);

        // 대상이 이동식(USB)이면 "안전하게 제거"를 제안합니다. 클론 후 대상을 다시 온라인으로
        // 올리며 복제된 볼륨이 자동 마운트돼, 그냥은 안전 제거가 "장치 사용 중"으로 막히기
        // 때문입니다. 성공·실패와 무관하게 디스크를 뽑으려면 필요하므로 결과와 상관없이 띄웁니다.
        SafeRemoveRan = false;
        SafeRemoveAvailable = report.Target.IsRemovable || report.Target.BusType == DiskBusType.Usb;

        (ResultTitle, ResultMessage) = result.Outcome switch
        {
            CloneOutcome.Completed => (
                Strings.Get("ResTitleCompleted"),
                Strings.Format("ResMsgCompletedFmt", report.Target.DeviceNumber, report.Target.Model)),

            CloneOutcome.CompletedWithBadSectors => (
                Strings.Get("ResTitleBadSectors"),
                Strings.Format("ResMsgBadSectorsFmt", result.BadSectors.Count)),

            CloneOutcome.Cancelled => (
                Strings.Get("ResTitleCancelled"),
                Strings.Format("ResMsgCancelledFmt", report.Target.DeviceNumber, report.Target.Model)),

            _ => (
                Strings.Get("ResTitleFailed"),
                result.ErrorMessage ?? Strings.Get("ResMsgUnknownError")),
        };

        // 리사이즈 클론에서 GPT 재작성이 실패하면 파티션 데이터는 새 위치에 있는데 파티션
        // 테이블은 옛 위치를 가리켜 배치가 깨진 상태입니다. 데이터가 복사됐다는 이유로 "완료"로
        // 보이면 사용자가 부팅 불가 디스크를 정상 사본으로 오인합니다. 명확한 실패·경고로 덮어씁니다.
        if (report.ResizeLayoutCorrupted)
        {
            ResultIsSuccess = false;
            PartitionExpandAvailable = false;
            UefiConvertAvailable = false;
            ResultTitle = Strings.Get("ResTitleResizeCorrupted");
            ResultMessage = Strings.Format("ResMsgResizeCorruptedFmt",
                report.Target.DeviceNumber, report.Target.Model);
        }
    }

    private void ShowFailure(string title, string message, string details)
    {
        ResultIsSuccess = false;
        ResultTitle = title;
        ResultMessage = message;
        ResultDetails = details;
    }

    // --- 이미지 백업 (디스크 → .vhdx) --------------------------------------

    public bool CanBackup =>
        Stage == AppStage.Selecting && IsElevated &&
        SelectedSource is not null && !string.IsNullOrWhiteSpace(ImagePath);

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        if (SelectedSource is null) return;

        var source = SelectedSource.Disk;
        string imagePath = ImagePath;
        // 실제로 "이번에 새로 만드는" 파일 — 전체 백업이면 고른 경로, 증분이면 새 자식 파일.
        // 서비스 호출 직전에 채워지며, 실패 정리는 이 파일만 지웁니다(기존 백업 보호).
        string producedPath = "";

        _cts = new CancellationTokenSource();
        _pause = new PauseController();

        Stage = AppStage.Running;
        IsPaused = false;
        ResetBootCheck();
        ResetPostCloneActions();
        BadSectorCount = 0;
        ProgressPercent = 0;
        ProgressPhase = Strings.Get("ProgPreparing");
        ProgressRegion = UseSnapshot ? Strings.Get("ProgSnapshotting") : "";
        ProgressBytes = ProgressSpeed = ProgressEta = ProgressElapsed = "";

        var progress = new Progress<CloneProgress>(OnProgress);

        try
        {
            var options = new CloneOptions
            {
                BadSectorPolicy = ZeroFillBadSectors
                    ? BadSectorPolicy.ZeroFillAndContinue
                    : BadSectorPolicy.Abort,
                VerifyAfterClone = VerifyAfterClone,
            };

            var svc = new ImageBackupService(_diskService, _snapshotProvider, _loggerFactory);
            CloneResult result;

            if (File.Exists(imagePath))
            {
                // 기존 백업 파일 → 증분 백업. 기존 파일은 절대 수정·삭제하지 않고, 그 이후
                // 바뀐 블록만 새 자식 파일(base-NN.vhdx)에 저장합니다.
                var chain = BackupChain.Resolve(imagePath)
                    ?? throw new InvalidOperationException(Strings.Get("BackupChainBroken"));
                producedPath = chain.ChildPath;
                _logger.LogInformation("증분 백업 라우팅: 부모 {Parent} → 자식 {Child}",
                    chain.ParentPath, chain.ChildPath);

                result = await svc.BackupIncrementalAsync(
                    source, chain.ParentPath, chain.ChildPath,
                    UseSnapshot, SkipUnusedBlocks, options, progress, _pause, _cts.Token);
            }
            else
            {
                producedPath = imagePath;
                result = await svc.BackupAsync(
                    source, imagePath, UseSnapshot, SkipUnusedBlocks, options, progress, _pause, _cts.Token);
            }

            ShowBackupResult(result, producedPath);
        }
        catch (OperationCanceledException)
        {
            // 취소된 백업은 불완전한 .vhdx를 남기므로 지웁니다(서비스가 이미 VHDX를 detach함).
            // 증분이면 자식 파일만 지웁니다 — 부모(기존 백업)는 건드리지 않았으므로 그대로 유효합니다.
            if (producedPath.Length > 0) TryDeletePartialImage(producedPath);
            ShowFailure(Strings.Get("ResTitleCancelled"), Strings.Get("BackupCancelledMsg"), "");
        }
        catch (Exception ex)
        {
            if (producedPath.Length > 0) TryDeletePartialImage(producedPath);
            _logger.LogError(ex, "이미지 백업에 실패했습니다.");
            ShowFailure(Strings.Get("ResTitleFailed"), ex.Message, Strings.Get("FailSeeLog"));
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

    /// <summary>취소·실패로 남은 불완전한 백업 이미지 파일을 지웁니다(best-effort).</summary>
    private void TryDeletePartialImage(string imagePath)
    {
        try
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "불완전한 백업 이미지를 지우지 못했습니다: {Path}", imagePath);
        }
    }

    private void ShowBackupResult(CloneResult result, string imagePath)
    {
        bool ok = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;

        ResultIsSuccess = ok;
        ResultTitle = ok ? Strings.Get("BackupDoneTitle") : Strings.Get("ResTitleFailed");
        ResultMessage = ok
            ? Strings.Format("BackupDoneMsgFmt", imagePath)
            : (result.ErrorMessage ?? Strings.Get("FailSeeLog"));

        var details = new List<string>
        {
            Strings.Format("ResCopiedFmt", SizeFormatter.Format(result.BytesCopied)),
            Strings.Format("ResDurationFmt", SizeFormatter.FormatDuration(result.Duration)),
            Strings.Format("ResSpeedFmt", SizeFormatter.FormatSpeed(result.AverageSpeedBytesPerSecond)),
        };

        try
        {
            if (File.Exists(imagePath))
                details.Add(Strings.Format("BackupImageSizeFmt",
                    SizeFormatter.Format(new FileInfo(imagePath).Length)));
        }
        catch { /* 이미지 크기 조회 실패는 무시 */ }

        details.Add(result.VerificationPassed switch
        {
            true => Strings.Get("ResVerifyPass"),
            false => Strings.Get("ResVerifyFail"),
            null => Strings.Get("ResVerifyNone"),
        });

        ResultDetails = string.Join("\n", details);
    }

    // --- 축소 복원 (대상이 이미지보다 작으면 자동으로 파티션을 줄여 복원) ------
    //
    // 별도 체크박스·크기 입력을 두지 않습니다. 클론이 대상<원본이면 자동으로 맞춤 클론을 하듯,
    // 복원도 이미지와 대상만 고르면 앱이 판단합니다: 대상이 이미지(파티션이 차지한 끝)보다 작으면
    // 가장 큰 NTFS 파티션을 필요한 만큼 자동으로 줄여 맞추고, 아니면 그대로 복원합니다.
    // 사용자에게는 무엇이 일어날지 안내 문구로만 보여 줍니다.

    /// <summary>이미지 안의 축소 가능한(NTFS) 파티션 목록(큰 것 우선). 이미지를 고르면 채워집니다.</summary>
    public ObservableCollection<ShrinkPartitionChoice> ShrinkPartitions { get; } = [];

    /// <summary>이미지 파티션이 실제로 차지한 끝(마지막 파티션 끝). 0이면 아직 안 읽음.</summary>
    private long _imageOccupiedEnd;

    /// <summary>이미지 정보를 다 읽었는지 — 읽기 전엔 복원을 시작하지 않습니다(축소 필요 여부를 모름).</summary>
    private bool _imageInfoLoaded;

    /// <summary>
    /// 자동 축소가 필요한지 계산합니다. 필요 없으면 null(그대로 복원), 필요한데 불가능하면
    /// null + <paramref name="blocked"/>에 이유.
    /// </summary>
    private (int PartitionNumber, long CurrentBytes, long NewBytes)? ResolveAutoShrink(out string? blocked)
    {
        blocked = null;
        if (SelectedTarget is null || !_imageInfoLoaded || _imageOccupiedEnd <= 0) return null;

        // 대상에서 파티션이 쓸 수 있는 마지막 경계(1MB 정렬 + 백업 GPT 예약) — ResizePlanner와 같은 규칙.
        long targetSize = SelectedTarget.Disk.SizeBytes;
        long maxEnd = targetSize - targetSize % ResizePlanner.Alignment - ResizePlanner.EndReserve;
        long deltaNeeded = _imageOccupiedEnd - maxEnd;
        if (deltaNeeded <= 0) return null;   // 대상이 충분히 큼 — 그대로 복원.

        var candidate = ShrinkPartitions.FirstOrDefault();
        if (candidate is null)
        {
            blocked = Strings.Get("ShrinkAutoNoCandidate");
            return null;
        }

        // 줄일 양을 1MB 올림으로 잡아 확실히 들어가게 합니다.
        long delta = deltaNeeded % ResizePlanner.Alignment == 0
            ? deltaNeeded
            : deltaNeeded + (ResizePlanner.Alignment - deltaNeeded % ResizePlanner.Alignment);
        long newBytes = candidate.CurrentBytes - delta;

        if (newBytes < (1L << 30))   // 1GB 밑으로 줄여야 들어가면 사실상 불가능한 대상.
        {
            blocked = Strings.Format("ShrinkAutoTooSmallFmt", SizeFormatter.Format(targetSize));
            return null;
        }

        return (candidate.Number, candidate.CurrentBytes, newBytes);
    }

    /// <summary>복원 화면의 자동 축소 안내 — 무엇이 일어날지, 또는 왜 안 되는지. 없으면 빈 문자열.</summary>
    public string ShrinkAutoText
    {
        get
        {
            if (SelectedTarget is null || string.IsNullOrWhiteSpace(ImagePath)) return "";
            if (!_imageInfoLoaded) return Strings.Get("ShrinkAutoReading");

            var auto = ResolveAutoShrink(out string? blocked);
            if (blocked is not null) return blocked;
            if (auto is not { } a) return "";
            return Strings.Format("ShrinkAutoFmt",
                a.PartitionNumber, SizeFormatter.Format(a.CurrentBytes), SizeFormatter.Format(a.NewBytes));
        }
    }

    public bool HasShrinkAutoText => ShrinkAutoText.Length > 0;

    private void RefreshShrinkAuto()
    {
        OnPropertyChanged(nameof(ShrinkAutoText));
        OnPropertyChanged(nameof(HasShrinkAutoText));
        RestoreImageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>복원할 이미지가 바뀌면 그 안의 파티션 배치를 다시 읽습니다.</summary>
    partial void OnImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(BackupChainNotice));
        _ = LoadImagePartitionsAsync();
    }

    /// <summary>
    /// 백업 저장 경로가 <b>기존 파일</b>이면 증분 백업 예고 문구(빈 문자열=숨김).
    /// 기존 백업은 덮어쓰지 않고, 바뀐 블록만 새 자식 파일에 저장한다는 것을 시작 전에 알립니다.
    /// </summary>
    public string BackupChainNotice
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return "";
            var chain = BackupChain.Resolve(ImagePath);
            return chain is null
                ? Strings.Get("BackupChainBroken")
                : Strings.Format("BackupIncNoticeFmt",
                    Path.GetFileName(chain.ParentPath), Path.GetFileName(chain.ChildPath));
        }
    }

    /// <summary>이미지 선택 직후의 빠른 확인 결과 문구(빈 문자열=숨김). 색은 <see cref="ImageCheckOk"/>가 정합니다.</summary>
    [ObservableProperty] private string _imageCheckStatus = "";

    /// <summary>빠른 확인이 통과였는지(true=초록, false=빨강).</summary>
    [ObservableProperty] private bool _imageCheckOk;

    /// <summary>고른 이미지를 잠깐 부착해 파티션 배치(차지한 끝 + NTFS 후보)를 읽습니다(볼륨 수정 없음).</summary>
    private async Task LoadImagePartitionsAsync()
    {
        ShrinkPartitions.Clear();
        _imageOccupiedEnd = 0;
        _imageInfoLoaded = false;
        ImageCheckStatus = "";
        RefreshShrinkAuto();

        if (!IsRestoreMode || string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;

        string path = ImagePath;
        try
        {
            var found = new List<ShrinkPartitionChoice>();
            long occupiedEnd = 0;
            int partitionCount = 0;
            await Task.Run(() =>
            {
                using var img = VirtualDisk.OpenAndAttach(path, readOnly: true);

                // 부착 직후엔 볼륨-파티션 연결·파일시스템 감지가 아직 안 돼 있을 수 있습니다. 짧게
                // 기다렸다가, NTFS 후보가 나올 때까지 몇 번 다시 열거합니다.
                List<ShrinkPartitionChoice> best = [];
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    System.Threading.Thread.Sleep(700);
                    var disks = _diskService.EnumerateDisksAsync().GetAwaiter().GetResult();
                    var d = disks.FirstOrDefault(x => x.DeviceNumber == img.DiskNumber);
                    if (d is null) continue;

                    if (d.Partitions.Count > 0)
                    {
                        occupiedEnd = d.Partitions.Max(p => p.EndOffset);
                        partitionCount = d.Partitions.Count;
                    }

                    var candidates = d.Partitions
                        .Where(IsShrinkCandidate)
                        .OrderByDescending(p => p.LengthBytes)
                        .Select(p => new ShrinkPartitionChoice(p.Number, p.LengthBytes, p.DriveLetter, p.FileSystem))
                        .ToList();

                    if (candidates.Count > best.Count) best = candidates;
                    // NTFS로 확실히 감지된 후보가 하나라도 있으면 더 기다리지 않습니다.
                    if (d.Partitions.Any(p => string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)))
                        break;
                }
                found = best;
            });

            // 이미지가 그새 바뀌었으면(빠르게 다른 파일 선택) 결과를 버립니다.
            if (path != ImagePath) return;

            foreach (var c in found) ShrinkPartitions.Add(c);
            _imageOccupiedEnd = occupiedEnd;

            // 빠른 확인 결과 — 부착이 됐고 파티션이 인식되면 구조는 온전합니다.
            // (파일시스템까지 보는 심층 검사는 복원 시작 시 자동으로 한 번 더 돕니다.)
            ImageCheckOk = partitionCount > 0;
            ImageCheckStatus = partitionCount > 0
                ? Strings.Format("ImageQuickOkFmt", partitionCount)
                : Strings.Get("ImageQuickBad");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "복원용 이미지 파티션 배치를 읽지 못했습니다.");
            if (path == ImagePath)
            {
                // 부착 실패 = VHDX 구조를 읽지 못함 — 손상 가능성을 바로 알립니다.
                ImageCheckOk = false;
                ImageCheckStatus = Strings.Get("ImageQuickBad");
            }
        }
        finally
        {
            if (path == ImagePath) _imageInfoLoaded = true;
            RefreshShrinkAuto();
        }
    }

    /// <summary>원본의 실사용 총량 추정(바이트). 스마트 백업 임시 이미지의 크기 예측에 씁니다.</summary>
    private static long EstimateUsedBytes(DiskInfo source) =>
        source.Partitions.Sum(p =>
            p.FreeSpaceBytes is { } free and >= 0 ? Math.Max(0, p.LengthBytes - free) : p.LengthBytes);

    /// <summary>
    /// 축소 클론의 임시 백업 이미지를 둘 경로를 고릅니다 — 원본·대상 디스크가 아닌 볼륨 중
    /// 여유 공간이 (원본 실사용 + 10GB) 이상인 곳에서 가장 여유가 큰 곳. 없으면 null.
    /// </summary>
    private string? FindShrinkTempImagePath(DiskInfo source, DiskInfo target)
    {
        long needed = EstimateUsedBytes(source) + (10L << 30);

        var best = Disks
            .Select(d => d.Disk)
            .Where(d => d.DeviceNumber != source.DeviceNumber && d.DeviceNumber != target.DeviceNumber)
            .SelectMany(d => d.Partitions)
            .Where(p => !string.IsNullOrEmpty(p.DriveLetter) && p.FreeSpaceBytes is { } f && f >= needed)
            .OrderByDescending(p => p.FreeSpaceBytes)
            .FirstOrDefault();

        if (best is null) return null;
        return Path.Combine($"{best.DriveLetter}:\\",
            $"DiskMigrator-shrink-clone-{DateTime.Now:yyyyMMdd-HHmmss}.vhdx");
    }

    // --- 부팅 USB (WinPE) 만들기 -------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildBootUsbCommand))]
    [NotifyPropertyChangedFor(nameof(CanSwitchLanguage))]
    private bool _isPeBuilding;

    /// <summary>완료(성공/실패) 후 결과 표시 여부.</summary>
    [ObservableProperty] private bool _peRan;
    [ObservableProperty] private bool _peSuccess;

    /// <summary>진행 단계·결과 메시지(한 줄).</summary>
    [ObservableProperty] private string _peStatus = "";

    /// <summary>전체 진행률(0~100). 재료 탐지 ~5% → 미디어 조립 5~65% → USB 기록 65~100%.</summary>
    [ObservableProperty] private double _peProgress;

    private CancellationTokenSource? _peCts;

    /// <summary>부팅 USB 시작 버튼이 비활성인 이유. 쓸 수 있으면 빈 문자열.</summary>
    public string BootUsbBlockedReason
    {
        get
        {
            if (SelectedTarget is null) return Strings.Get("BootUsbNoTarget");
            var t = SelectedTarget.Disk;
            if (t.IsSystemDisk || t.IsBootDisk || t.HasPageFile ||
                (t.BusType != DiskBusType.Usb && !t.IsRemovable))
            {
                return Strings.Get("BootUsbNotUsb");
            }
            if (t.SizeBytes < (2L << 30)) return Strings.Get("BootUsbTooSmall");
            if (!SafetyGuard.IsConfirmationValid(t, ConfirmationText))
                return Strings.Get("BlockConfirmIncomplete");
            return "";
        }
    }

    public bool CanBuildBootUsb =>
        Stage == AppStage.Selecting && IsElevated && !IsPeBuilding &&
        BootUsbBlockedReason.Length == 0;

    /// <summary>
    /// 부팅 USB를 만듭니다: 재료 탐지 → 미디어 조립(앱 주입) → USB 포맷·복사.
    /// USB의 기존 내용은 모두 지워집니다(모델명 확인 후에만 실행 가능).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBuildBootUsb))]
    private async Task BuildBootUsbAsync()
    {
        if (SelectedTarget is null) return;

        IsPeBuilding = true;
        PeRan = false;
        PeSuccess = false;
        PeStatus = Strings.Get("PeStatusPreparing");
        PeProgress = 0;
        _peCts = new CancellationTokenSource();
        string workRoot = Path.Combine(Path.GetTempPath(), "DiskMigrator-pe-work");

        try
        {
            // 쓰기 직전 최종 관문 — 확인한 그 USB가 지금도 같은 물리 디스크인지.
            var target = await ResolveCurrentTargetAsync();
            if (target is null)
            {
                PeStatus = Strings.Get("TargetNotFoundAgain");
                return;
            }
            SafetyGuard.AssertTargetUnchanged(SelectedTarget.Disk, target);

            var ingredients = await new WinPeIngredients(_diskService, _loggerFactory.CreateLogger("WinPe"))
                .LocateAsync(_peCts.Token);
            if (!ingredients.AllFound)
            {
                PeStatus = Strings.Get("PeNoIngredients") + "\n" +
                           string.Join("\n", ingredients.Notes.Select(n => "· " + n));
                return;
            }

            // 주입할 실행 파일 = 지금 실행 중인 이 앱(자체 포함 단일 exe 배포본).
            string appExe = Environment.ProcessPath
                ?? throw new InvalidOperationException(DiskMigrator.Core.Localization.L.T(
                    "실행 파일 경로를 확인할 수 없습니다.", "The executable path could not be determined."));

            PeProgress = 5;   // 재료 탐지 완료

            var builder = new WinPeMediaBuilder(_loggerFactory.CreateLogger<WinPeMediaBuilder>());
            builder.Progress += (step, f) => { PeStatus = step; PeProgress = 5 + f * 60; };
            // DISM 실행이 스레드를 붙잡으므로 UI가 굳지 않게 백그라운드에서 돌립니다.
            // PE 미디어 안의 폴더·exe 이름도 제품명을 따릅니다 — 수동 버전이 만든 USB와
            // 섞이지 않아야 어느 앱의 것인지 알 수 있습니다.
            var build = await Task.Run(() =>
                builder.BuildAsync(ingredients, appExe, workRoot, AppIdentity.ProductName, _peCts.Token));
            if (!build.Success)
            {
                PeStatus = build.Message;
                return;
            }

            var writer = new UsbBootWriter(_loggerFactory.CreateLogger<UsbBootWriter>());
            writer.Progress += (step, f) => { PeStatus = step; PeProgress = 65 + f * 35; };
            var write = await writer.WriteAsync(target, build.MediaRoot!, _peCts.Token);

            PeSuccess = write.Success;
            PeStatus = write.Message;
            if (write.Success) PeProgress = 100;
        }
        catch (OperationCanceledException)
        {
            PeStatus = Strings.Get("PeCancelled");
        }
        catch (SafetyViolationException ex)
        {
            _logger.LogError(ex, "부팅 USB 안전 검사 실패.");
            PeStatus = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 USB 만들기 실패.");
            PeStatus = ex.Message;
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true); }
            catch { /* 임시 폴더 정리 실패는 무해 */ }

            PeRan = true;
            IsPeBuilding = false;
            _peCts?.Dispose();
            _peCts = null;
            ConfirmationText = "";
            await RefreshDisksAsync();   // USB가 새로 포맷됐으니 목록을 갱신합니다.
        }
    }

    [RelayCommand]
    private void CancelBootUsb() => _peCts?.Cancel();

    /// <summary>MSR(Microsoft 예약) 파티션 GPT 타입 GUID.</summary>
    private static readonly Guid MsrPartitionType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    /// <summary>
    /// 축소 후보(NTFS 데이터 파티션)인지. 부착 이미지에서는 파일시스템 감지가 늦을 수 있어, 감지가
    /// 안 된 경우 ESP·MSR이 아니고 1GB 이상인 파티션도 후보로 둡니다(실제 NTFS 여부·축소 한계는
    /// diskpart가 최종 검증하므로, 목록에 잠깐 잘못 뜨더라도 안전합니다).
    /// </summary>
    private static bool IsShrinkCandidate(PartitionInfo p)
    {
        if (string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(p.FileSystem)) return false;   // FAT32 등 비-NTFS는 제외
        if (p.IsEfiSystemPartition) return false;
        if (p.GptPartitionType == MsrPartitionType) return false;
        return p.LengthBytes >= (1L << 30);                      // 1GB 미만 제외
    }

    // --- 이미지 복원 (.vhdx → 디스크) --------------------------------------

    /// <summary>복원 대상은 파괴되므로, 대상 모델명을 직접 입력하라는 안내.</summary>
    public string RestoreConfirmPrompt =>
        SelectedTarget is null ? "" : Strings.Format("ConfirmPromptFmt", SelectedTarget.Model);

    /// <summary>복원할 이미지(.vhdx)를 고릅니다. WinPE에서는 자체 파일 창으로 대체됩니다.</summary>
    [RelayCommand]
    private void BrowseImageOpen()
    {
        var path = Views.FileDialogs.PickOpen(
            Strings.Get("RestoreChoosePath"), Strings.Get("VhdxFilter"), ".vhdx");
        if (path is not null) ImagePath = path;
    }

    /// <summary>
    /// 복원 시작 가능 여부. 대상은 파괴되므로 시스템/부팅/페이지파일 디스크는 막고,
    /// 대상 모델명을 정확히 입력해야 합니다.
    /// </summary>
    public bool CanRestore
    {
        get
        {
            if (Stage != AppStage.Selecting || !IsElevated) return false;
            if (SelectedTarget is null || string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
                return false;

            var t = SelectedTarget.Disk;
            if (t.IsSystemDisk || t.IsBootDisk || t.HasPageFile || t.IsReadOnly) return false;

            // 이미지 배치를 읽기 전엔 시작하지 않습니다(축소가 필요한지 아직 모름). 대상이 작은데
            // 자동 축소도 불가능하면(blocked) 역시 시작할 수 없습니다.
            if (!_imageInfoLoaded) return false;
            ResolveAutoShrink(out string? blocked);
            if (blocked is not null) return false;

            return SafetyGuard.IsConfirmationValid(t, ConfirmationText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreImageAsync()
    {
        if (SelectedTarget is null) return;

        string imagePath = ImagePath;

        _cts = new CancellationTokenSource();
        _pause = new PauseController();

        Stage = AppStage.Running;
        IsPaused = false;
        ResetBootCheck();
        ResetPostCloneActions();
        BadSectorCount = 0;
        ProgressPercent = 0;
        ProgressPhase = Strings.Get("ProgPreparing");
        ProgressRegion = Strings.Get("ProgLockingTarget");
        ProgressBytes = ProgressSpeed = ProgressEta = ProgressElapsed = "";

        var progress = new Progress<CloneProgress>(OnProgress);

        try
        {
            // 쓰기 직전 최종 관문: 확인한 그 대상이 지금도 같은 물리 디스크인지 재확인.
            var target = await ResolveCurrentTargetAsync();
            if (target is null)
            {
                ShowFailure(Strings.Get("FailSafetyTitle"),
                    Strings.Get("TargetNotFoundAgain"), Strings.Get("FailNothingWritten"));
                return;
            }
            SafetyGuard.AssertTargetUnchanged(SelectedTarget.Disk, target);

            // 대상을 지우기 전에 이미지 무결성을 검사합니다 — 손상된 이미지로 시작하면 복원은
            // 도중에 실패하고 대상만 잃습니다. 구조(부착)·파티션 테이블·NTFS(chkdsk 읽기 전용).
            ProgressPhase = Strings.Get("ProgImageCheck");
            ProgressRegion = "";
            var inspection = await new ImageInspector(
                    _diskService, _loggerFactory.CreateLogger<ImageInspector>())
                .InspectAsync(imagePath, _cts.Token);
            foreach (var item in inspection.Items)
                _logger.LogInformation("이미지 검사 [{Result}] {Name}: {Detail}",
                    item.Passed ? "통과" : "실패", item.Name, item.Detail);
            if (!inspection.Ok)
            {
                ShowFailure(Strings.Get("FailImageCheckTitle"), inspection.Summary,
                    Strings.Get("FailNothingWritten"));
                return;
            }

            var options = new CloneOptions
            {
                BadSectorPolicy = ZeroFillBadSectors
                    ? BadSectorPolicy.ZeroFillAndContinue
                    : BadSectorPolicy.Abort,
                VerifyAfterClone = VerifyAfterClone,
            };

            var svc = new ImageRestoreService(_diskService, _loggerFactory);
            ImageRestoreReport report;
            // 대상이 이미지보다 작으면 자동 계산된 만큼 파티션을 줄여 복원합니다(사용자 개입 불필요).
            if (ResolveAutoShrink(out _) is { } auto)
            {
                _logger.LogInformation(
                    "자동 축소 복원: 파티션 {Part} {Cur:N0} → {New:N0} 바이트 (대상이 이미지보다 작음).",
                    auto.PartitionNumber, auto.CurrentBytes, auto.NewBytes);
                report = await svc.RestoreWithShrinkAsync(
                    imagePath, target, auto.PartitionNumber, auto.NewBytes, UniversalRestore,
                    options, progress, _pause, _cts.Token);
            }
            else
            {
                report = await svc.RestoreAsync(
                    imagePath, target, UniversalRestore, options, progress, _pause, _cts.Token);
            }

            ShowRestoreResult(report, target);
        }
        catch (SafetyViolationException ex)
        {
            _logger.LogError(ex, "복원 안전 검사에 걸려 작업이 중단되었습니다.");
            ShowFailure(Strings.Get("FailSafetyTitle"), ex.Message, Strings.Get("FailNothingWritten"));
        }
        catch (OperationCanceledException)
        {
            ShowFailure(Strings.Get("ResTitleCancelled"), Strings.Get("RestoreCancelledMsg"), "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "이미지 복원에 실패했습니다.");
            ShowFailure(Strings.Get("ResTitleFailed"), ex.Message, Strings.Get("FailSeeLog"));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pause?.Dispose();
            _pause = null;
            Stage = AppStage.Finished;
        }

        // 복원 후처리 — 복원은 곧 "백업 파일에서의 클론"이므로, 복원한 시스템 디스크도 다른
        // 하드웨어에서 부팅되도록 클론과 같은 절차를 밟습니다: (옵션)UEFI 자동 변환 → 부팅 검사 →
        // 자동 부팅 복구. (GPT 보정·Universal Restore는 ImageRestoreService가 이미 수행함.)
        if (ResultIsSuccess)
        {
            // 복원된 대상을 다시 열거해, BIOS 전용 배치면 UEFI 변환을 제안/자동 실행합니다.
            var fresh = await ResolveCurrentTargetAsync();
            if (fresh is not null)
                UefiConvertAvailable = UefiConverter.NeedsConversion(fresh);

            if (AutoConvertUefi && UefiConvertAvailable)
            {
                _logger.LogInformation("복원 후 자동 UEFI 변환: 옵션 켜짐 + BIOS 전용 원본 — 실행합니다.");
                await ConvertToUefiAsync();
            }

            await BootCheckAsync();

            if (_deviceRefIsOnlyFatalFailure)
            {
                _logger.LogInformation("복원 후 자동 부팅 복구: 치명 실패가 BCD 장치 참조 하나뿐 — 실행합니다.");
                await RepairBootAsync();
            }
        }
    }

    private void ShowRestoreResult(ImageRestoreReport report, DiskInfo target)
    {
        var result = report.Result;
        bool ok = result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors;

        ResultIsSuccess = ok;
        ResultTitle = ok ? Strings.Get("RestoreDoneTitle") : Strings.Get("ResTitleFailed");
        ResultMessage = ok
            ? Strings.Format("RestoreDoneMsgFmt", target.DeviceNumber, target.Model)
            : (result.ErrorMessage ?? Strings.Get("FailSeeLog"));

        var details = new List<string>
        {
            Strings.Format("ResCopiedFmt", SizeFormatter.Format(result.BytesCopied)),
            Strings.Format("ResDurationFmt", SizeFormatter.FormatDuration(result.Duration)),
            Strings.Format("ResSpeedFmt", SizeFormatter.FormatSpeed(result.AverageSpeedBytesPerSecond)),
        };

        details.Add(result.VerificationPassed switch
        {
            true => Strings.Get("ResVerifyPass"),
            false => Strings.Get("ResVerifyFail"),
            null => Strings.Get("ResVerifyNone"),
        });

        if (report.Shrink is { } s) details.Add(Strings.Format("ResShrinkFmt", s.Message));
        if (report.GptRepair is { } g) details.Add(Strings.Format("ResPartTableFmt", g.Description));
        if (report.UniversalRestore is { } u) details.Add(Strings.Format("ResNewHwFmt", u.Message));

        ResultDetails = string.Join("\n", details);

        // 후속 작업 가용성. UEFI 변환 가용성은 복원된 배치를 봐야 하므로 복원 후처리에서 재열거로
        // 판정합니다. 안전 제거·파티션 확장은 여기서 정합니다.
        SafeRemoveRan = false;
        SafeRemoveAvailable = target.IsRemovable || target.BusType == DiskBusType.Usb;
        // 대상이 이미지보다 커서 GPT 백업 헤더를 끝으로 옮겼으면, 그만큼 미할당이 생겨 확장할 수
        // 있습니다. 축소 복원은 대상을 정확히 채우도록 줄인 것이라 확장할 공간이 없습니다 —
        // 눌러 봤자 "미완료"만 나오는 버튼을 제안하지 않습니다.
        PartitionExpandAvailable = ok && report.Shrink is null && report.GptRepair is { WasRepaired: true };
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
