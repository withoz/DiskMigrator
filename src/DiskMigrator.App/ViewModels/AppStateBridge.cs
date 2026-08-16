using System.Runtime.Versioning;
using DiskMigrator.Core.Util;
using DiskMigrator.Mcp;

namespace DiskMigrator.App.ViewModels;

/// <summary>
/// MCP 도구가 앱 상태를 읽을 수 있게 하는 다리. <b>읽기와 취소 요청만</b> 통과시킵니다.
/// </summary>
/// <remarks>
/// 뷰모델을 통째로 넘기면 도구가 화면의 무엇이든 바꿀 수 있습니다 — 대상 선택도, 모델명
/// 입력란도. 계획서 §6.3의 게이트를 지키려면 표면이 좁아야 하므로, 필요한 것만 꺼내 보여주는
/// 얇은 어댑터를 둡니다.
///
/// <para>감싸는 뷰모델은 private이며 밖으로 노출되지 않습니다. 이 클래스에 값을 <b>쓰는</b>
/// 메서드를 추가하지 마십시오 — 그 순간 게이트가 무너집니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppStateBridge(MainViewModel vm) : IAppState
{
    public bool IsBusy => vm.Stage == AppStage.Running || vm.IsPeBuilding;

    /// <summary>화면의 'VSS 스냅샷 사용' 체크 상태 그대로. 안전 판정이 이 값에 따라 갈립니다.</summary>
    public bool UseSnapshot => vm.UseSnapshot;

    public OperationProgress GetProgress()
    {
        if (!IsBusy)
            return new OperationProgress(false, null, 0, null, null, null, null);

        return new OperationProgress(
            Running: true,
            Phase: Empty(vm.ProgressPhase),
            Percent: vm.ProgressPercent,
            CurrentRegion: Empty(vm.ProgressRegion),
            BytesText: Empty(vm.ProgressBytes),
            SpeedText: Empty(vm.ProgressSpeed),
            EtaText: Empty(vm.ProgressEta));
    }

    /// <summary>
    /// 취소를 <b>요청</b>합니다. 엔진이 안전한 지점에서 정리한 뒤 멈추므로 즉시 중단은 아닙니다.
    /// </summary>
    /// <remarks>
    /// 실행을 시작하는 통로는 없고 멈추는 통로만 둔 이유는, 멈추는 것이 안전한 방향이기
    /// 때문입니다 — 되돌릴 수 없는 피해를 만들지 않습니다.
    /// </remarks>
    public void RequestCancel() => vm.CancelCommand.Execute(null);

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
