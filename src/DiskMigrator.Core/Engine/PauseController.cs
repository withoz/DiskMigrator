namespace DiskMigrator.Core.Engine;

/// <summary>
/// 작업을 안전한 지점(버퍼 경계)에서만 일시정지시키는 게이트.
/// </summary>
/// <remarks>
/// 쓰기 도중에 멈추면 반쪽짜리 블록이 남으므로, 엔진은 한 버퍼를 끝까지 쓴 뒤에만
/// 이 게이트를 확인합니다.
/// </remarks>
public sealed class PauseController : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);

    public bool IsPaused => !_gate.IsSet;

    public void Pause() => _gate.Reset();

    public void Resume() => _gate.Set();

    /// <summary>일시정지 상태면 재개될 때까지 블록합니다. 취소되면 예외를 던집니다.</summary>
    public void WaitIfPaused(CancellationToken ct)
    {
        if (_gate.IsSet) return;
        _gate.Wait(ct);
    }

    public void Dispose() => _gate.Dispose();
}
