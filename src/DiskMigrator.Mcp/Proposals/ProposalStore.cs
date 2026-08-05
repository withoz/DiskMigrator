using DiskMigrator.Core.Models;

namespace DiskMigrator.Mcp.Proposals;

/// <summary>제안이 바뀌었을 때 앱에 알리는 이벤트 인자.</summary>
public sealed class ProposalChangedEventArgs(CloneProposal? current) : EventArgs
{
    /// <summary>지금 사용자에게 보여줄 제안. 없으면 null(카드를 숨깁니다).</summary>
    public CloneProposal? Current { get; } = current;
}

/// <summary>
/// 지금 대기 중인 제안 하나를 들고 있습니다. MCP 스레드와 UI 스레드가 함께 만지므로 잠급니다.
/// </summary>
/// <remarks>
/// <b>제안은 한 번에 하나만 존재합니다.</b> 카드가 쌓이면 사용자가 어느 것을 보고 있는지 알 수 없고,
/// 그 상태에서 [적용]을 누르면 무엇에 동의한 것인지 불분명해집니다. 새 제안이 오면 기존 것은
/// <see cref="ProposalStatus.Superseded"/>가 됩니다.
///
/// <para>이 저장소는 <b>아무것도 실행하지 않습니다.</b> 제안을 보관하고 상태를 기록할 뿐이며,
/// 실제 값을 채우는 것은 앱이 사용자의 [적용]을 받은 뒤에 하는 일입니다.</para>
/// </remarks>
public sealed class ProposalStore
{
    private readonly object _gate = new();
    private CloneProposal? _current;
    private readonly Dictionary<string, CloneProposal> _history = [];

    /// <summary>제안이 생기거나 상태가 바뀌면 발생합니다. 앱이 카드를 보이고 숨기는 데 씁니다.</summary>
    public event EventHandler<ProposalChangedEventArgs>? Changed;

    /// <summary>지금 대기 중인 제안(만료됐으면 null).</summary>
    public CloneProposal? Current
    {
        get
        {
            lock (_gate)
            {
                ExpireIfNeeded(DateTime.UtcNow);
                return _current;
            }
        }
    }

    /// <summary>
    /// 새 제안을 올립니다. 기존 제안이 있으면 <see cref="ProposalStatus.Superseded"/>로 밀어냅니다.
    /// </summary>
    /// <param name="source">읽을 디스크(복제·백업). 복원·부팅 복구에서는 null.</param>
    /// <param name="target">쓸 디스크(복제·복원·부팅 복구). 백업에서는 null.</param>
    /// <param name="imagePath">이미지 파일 경로(백업·복원).</param>
    public CloneProposal Propose(
        ProposalKind kind, DiskInfo? source, DiskInfo? target, string? imagePath, string reason,
        bool useSnapshot, bool verifyAfterCopy, bool needsTypedConfirmation)
    {
        var proposal = new CloneProposal(
            Id: Guid.NewGuid().ToString("N")[..8],
            Kind: kind,
            Source: source is null ? null : DiskFingerprint.Of(source),
            Target: target is null ? null : DiskFingerprint.Of(target),
            ImagePath: imagePath,
            Reason: reason,
            UseSnapshot: useSnapshot,
            VerifyAfterCopy: verifyAfterCopy,
            NeedsTypedConfirmation: needsTypedConfirmation,
            CreatedUtc: DateTime.UtcNow,
            Status: ProposalStatus.Pending);

        lock (_gate)
        {
            if (_current is { Status: ProposalStatus.Pending } old)
                Record(old with { Status = ProposalStatus.Superseded });

            _current = proposal;
            _history[proposal.Id] = proposal;
        }

        Raise(proposal);
        return proposal;
    }

    /// <summary>
    /// 사용자가 적용했습니다. <b>앱만 부릅니다</b> — MCP 도구에서는 부를 수 없어야 합니다.
    /// </summary>
    /// <returns>적용할 제안. 이미 사라졌거나 만료됐으면 null.</returns>
    public CloneProposal? MarkApplied()
    {
        CloneProposal? applied = null;
        lock (_gate)
        {
            ExpireIfNeeded(DateTime.UtcNow);
            if (_current is { Status: ProposalStatus.Pending } p)
            {
                applied = p with { Status = ProposalStatus.Applied };
                Record(applied);
                _current = null;   // 카드는 사라지고, 값은 앱이 채웁니다
            }
        }
        if (applied is not null) Raise(null);
        return applied;
    }

    /// <summary>사용자가 무시했습니다.</summary>
    public void MarkDismissed()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_current is { Status: ProposalStatus.Pending } p)
            {
                Record(p with { Status = ProposalStatus.Dismissed });
                _current = null;
                changed = true;
            }
        }
        if (changed) Raise(null);
    }

    /// <summary>
    /// 디스크 구성이 바뀌었으면 제안을 무효화합니다.
    /// </summary>
    /// <remarks>
    /// 제안을 만든 뒤 사용자가 USB를 바꿔 꽂았을 수 있습니다. 장치 번호는 그대로인데 다른
    /// 디스크일 수 있으므로, 모델·시리얼·크기로 정체를 확인합니다.
    /// </remarks>
    public void InvalidateIfDisksChanged(IReadOnlyList<DiskInfo> disks)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_current is { Status: ProposalStatus.Pending } p)
            {
                // 제안에 없는 쪽(백업의 대상, 복원의 원본)은 확인할 것이 없으므로 통과입니다.
                bool sourceOk = p.Source is null || disks.Any(d => p.Source.Matches(d));
                bool targetOk = p.Target is null || disks.Any(d => p.Target.Matches(d));

                if (!sourceOk || !targetOk)
                {
                    Record(p with { Status = ProposalStatus.Expired });
                    _current = null;
                    changed = true;
                }
            }
        }
        if (changed) Raise(null);
    }

    /// <summary>제안 상태를 조회합니다(Claude가 결과를 확인하는 통로).</summary>
    public CloneProposal? Find(string id)
    {
        lock (_gate)
        {
            ExpireIfNeeded(DateTime.UtcNow);
            return _history.GetValueOrDefault(id);
        }
    }

    /// <summary>잠금 안에서만 부릅니다.</summary>
    private void ExpireIfNeeded(DateTime utcNow)
    {
        if (_current is { Status: ProposalStatus.Pending } p && p.IsExpiredAt(utcNow))
        {
            Record(p with { Status = ProposalStatus.Expired });
            _current = null;
        }
    }

    private void Record(CloneProposal p) => _history[p.Id] = p;

    private void Raise(CloneProposal? current) =>
        Changed?.Invoke(this, new ProposalChangedEventArgs(current));
}
