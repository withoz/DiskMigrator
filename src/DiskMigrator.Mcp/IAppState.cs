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

    /// <summary>진행 상황(실행 중이 아니면 Running=false).</summary>
    OperationProgress GetProgress();

    /// <summary>
    /// 진행 중인 작업의 취소를 <b>요청</b>합니다. 즉시 멈춘다는 보장은 없으며,
    /// 안전한 지점에서 정리한 뒤 끝납니다.
    /// </summary>
    void RequestCancel();
}
