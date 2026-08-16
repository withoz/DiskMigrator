namespace DiskMigrator.Mcp;

/// <summary>진행 중인 작업의 상태.</summary>
/// <param name="Running">지금 무언가 실행 중인지.</param>
/// <param name="Phase">현재 단계(복사·검증 등).</param>
/// <param name="Percent">진행률 0~100.</param>
/// <param name="CurrentRegion">지금 처리 중인 구간 설명.</param>
/// <param name="BytesText">처리량 / 전체.</param>
/// <param name="SpeedText">속도.</param>
/// <param name="EtaText">남은 시간.</param>
public sealed record OperationProgress(
    bool Running,
    string? Phase,
    double Percent,
    string? CurrentRegion,
    string? BytesText,
    string? SpeedText,
    string? EtaText);

/// <summary>
/// 앱의 상태를 <b>읽고</b>, 취소만 요청할 수 있는 통로.
/// </summary>
/// <remarks>
/// MCP 도구가 앱 뷰모델을 통째로 잡으면 화면의 무엇이든 바꿀 수 있게 됩니다 — 대상 선택이나
/// 모델명 입력란까지. <see cref="IDiskReader"/>와 같은 이유로 표면을 좁힙니다.
///
/// <para>여기에 <b>시작·실행에 해당하는 것은 없습니다.</b> 취소만 있는 이유는 그것이 안전한
/// 방향이기 때문입니다 — 멈추는 것은 되돌릴 수 없는 피해를 만들지 않습니다.</para>
/// </remarks>
public interface IAppState
{
    /// <summary>지금 클론·백업·복원 등을 실행 중인지. 참이면 새 제안을 받지 않습니다.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// 지금 화면에서 <b>VSS 스냅샷 사용</b>이 켜져 있는지.
    /// </summary>
    /// <remarks>
    /// 안전 판정이 이 값에 따라 갈립니다(실행 중인 시스템을 스냅샷 없이 복제하면 경고가 붙습니다).
    /// 예전에는 <c>evaluate_safety</c>가 늘 "켜져 있다"고 가정했고, 사용자가 화면에서 그것을 껐다면
    /// <b>Claude와 화면이 다른 판정을 내놓았습니다.</b> 그러면 사용자는 둘 중 어느 쪽을 믿어야
    /// 할지 모릅니다 — 안전 판정에서 그것은 그냥 틀린 것보다 나쁩니다.
    ///
    /// <para>읽기 전용입니다. 도구가 이 값을 <b>바꿀 수는 없습니다</b> — 옵션을 정하는 것은 사람입니다.</para>
    /// </remarks>
    bool UseSnapshot { get; }

    /// <summary>진행 상황(실행 중이 아니면 Running=false).</summary>
    OperationProgress GetProgress();

    /// <summary>
    /// 진행 중인 작업의 취소를 <b>요청</b>합니다. 즉시 멈춘다는 보장은 없으며,
    /// 안전한 지점에서 정리한 뒤 끝납니다.
    /// </summary>
    void RequestCancel();
}
