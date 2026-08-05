using System.Reflection;
using DiskMigrator.Core.Models;
using DiskMigrator.Mcp;
using DiskMigrator.Mcp.Proposals;
using DiskMigrator.Mcp.Tools;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 3단계 확인 게이트 — 계획서 §6.3에서 "반드시 시험할 것"으로 못 박은 항목들입니다.
/// </summary>
/// <remarks>
/// 여기서부터는 되돌릴 수 없는 작업이 걸립니다. "지금은 안전하다"가 아니라
/// <b>"구조적으로 불가능하다"</b>를 확인해야 합니다.
/// </remarks>
public class ProposalGateTests
{
    private static DiskInfo Disk(int n, string model, long size = 500_000_000_000, string? serial = "S1") => new()
    {
        DeviceNumber = n,
        Model = model,
        SerialNumber = serial,
        SizeBytes = size,
        LogicalSectorSize = 512,
        PartitionStyle = PartitionStyle.Gpt,
    };

    // --- 게이트의 핵심: Claude는 확인란에 닿을 수 없다 -----------------------

    /// <summary>
    /// 제안 도구가 뷰모델이나 쓰기 서비스를 받지 않아야 합니다.
    /// </summary>
    /// <remarks>
    /// 뷰모델을 잡으면 <c>ConfirmationText</c>(모델명 입력란)를 채울 수 있고, 그 순간 2차 관문이
    /// 무너집니다. 받을 수 있는 것은 제안 저장소와 읽기 전용 앱 상태뿐입니다.
    /// </remarks>
    [Fact]
    public void 제안_도구는_확인란에_닿는_통로를_받지_않는다()
    {
        var ctor = typeof(ProposalTools).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(Core.Abstractions.IDiskService), paramTypes);
        Assert.Contains(typeof(IDiskReader), paramTypes);
        Assert.Contains(typeof(ProposalStore), paramTypes);
        Assert.Contains(typeof(IAppState), paramTypes);
    }

    /// <summary>
    /// 앱 상태 통로에 실행을 시작하는 메서드가 없어야 합니다 — 취소만 있습니다.
    /// </summary>
    [Fact]
    public void 앱_상태_통로에는_실행_메서드가_없다()
    {
        var names = typeof(IAppState).GetMembers().Select(m => m.Name).ToArray();

        foreach (string forbidden in new[] { "Start", "Execute", "Run", "Confirm", "Apply", "Clone" })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // 취소는 있어야 합니다 — 멈추는 것은 안전한 방향입니다.
        Assert.Contains(names, n => n.Contains("Cancel", StringComparison.Ordinal));
    }

    /// <summary>
    /// 제안 저장소에도 "실행"에 해당하는 것이 없어야 합니다.
    /// 적용 표시(MarkApplied)는 앱이 사용자의 클릭을 받은 뒤 부르는 것이며, 그 자체로는
    /// 아무것도 실행하지 않습니다.
    /// </summary>
    [Fact]
    public void 제안_저장소는_아무것도_실행하지_않는다()
    {
        var methods = typeof(ProposalStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        foreach (string forbidden in new[] { "Start", "Execute", "Run", "Write", "Clone" })
        {
            Assert.DoesNotContain(methods, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- 제안의 수명 ---------------------------------------------------------

    [Fact]
    public void 새_제안은_기존_제안을_밀어낸다()
    {
        var store = new ProposalStore();
        var first = store.Propose(Disk(0, "A"), Disk(1, "B"), "첫 번째", true, true, false);
        var second = store.Propose(Disk(0, "A"), Disk(2, "C", serial: "S2"), "두 번째", true, true, false);

        Assert.Equal(ProposalStatus.Superseded, store.Find(first.Id)!.Status);
        Assert.Equal(ProposalStatus.Pending, store.Find(second.Id)!.Status);
        Assert.Equal(second.Id, store.Current!.Id);
    }

    [Fact]
    public void 적용하면_대기_상태가_아니게_되고_카드가_사라진다()
    {
        var store = new ProposalStore();
        var p = store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, true);

        var applied = store.MarkApplied();

        Assert.NotNull(applied);
        Assert.Equal(ProposalStatus.Applied, store.Find(p.Id)!.Status);
        Assert.Null(store.Current);   // 카드는 사라집니다
    }

    /// <summary>
    /// 적용은 "값이 채워졌다"이지 "실행됐다"가 아닙니다. 모델명 요구는 그대로 남습니다.
    /// </summary>
    [Fact]
    public void 적용해도_모델명_요구는_사라지지_않는다()
    {
        var store = new ProposalStore();
        store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, needsTypedConfirmation: true);

        var applied = store.MarkApplied();

        Assert.True(applied!.NeedsTypedConfirmation);
    }

    [Fact]
    public void 무시하면_카드가_사라진다()
    {
        var store = new ProposalStore();
        var p = store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, false);

        store.MarkDismissed();

        Assert.Equal(ProposalStatus.Dismissed, store.Find(p.Id)!.Status);
        Assert.Null(store.Current);
    }

    [Fact]
    public void 이미_처리된_제안은_다시_적용되지_않는다()
    {
        var store = new ProposalStore();
        store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, false);

        Assert.NotNull(store.MarkApplied());
        Assert.Null(store.MarkApplied());   // 두 번째는 없습니다
    }

    // --- 디스크가 바뀌면 무효 -----------------------------------------------

    /// <summary>
    /// 제안 후 대상을 뽑았다 다른 디스크를 꽂으면 무효가 되어야 합니다.
    /// 장치 번호는 그대로여도 정체가 다르면 같은 디스크가 아닙니다.
    /// </summary>
    [Fact]
    public void 대상이_다른_디스크로_바뀌면_제안이_무효가_된다()
    {
        var store = new ProposalStore();
        var p = store.Propose(Disk(0, "A"), Disk(1, "B", serial: "S-OLD"), "이유", true, true, false);

        // 같은 번호(1)지만 시리얼이 다른 디스크로 교체됨
        store.InvalidateIfDisksChanged([Disk(0, "A"), Disk(1, "B", serial: "S-NEW")]);

        Assert.Equal(ProposalStatus.Expired, store.Find(p.Id)!.Status);
        Assert.Null(store.Current);
    }

    [Fact]
    public void 대상이_빠지면_제안이_무효가_된다()
    {
        var store = new ProposalStore();
        var p = store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, false);

        store.InvalidateIfDisksChanged([Disk(0, "A")]);   // 대상이 사라짐

        Assert.Equal(ProposalStatus.Expired, store.Find(p.Id)!.Status);
    }

    [Fact]
    public void 디스크가_그대로면_제안이_유지된다()
    {
        var store = new ProposalStore();
        var p = store.Propose(Disk(0, "A"), Disk(1, "B"), "이유", true, true, false);

        // 번호만 바뀌고 정체는 같은 경우 — USB를 다른 포트에 꽂았을 때
        store.InvalidateIfDisksChanged([Disk(3, "A"), Disk(5, "B")]);

        Assert.Equal(ProposalStatus.Pending, store.Find(p.Id)!.Status);
        Assert.NotNull(store.Current);
    }

    // --- 만료 ---------------------------------------------------------------

    [Fact]
    public void 시간이_지나면_만료한다()
    {
        var p = new CloneProposal("id", new(0, "A", "S", 1), new(1, "B", "S", 1),
            "이유", true, true, false, DateTime.UtcNow.AddMinutes(-11), ProposalStatus.Pending);

        Assert.True(p.IsExpiredAt(DateTime.UtcNow));
        Assert.False(p.IsLiveAt(DateTime.UtcNow));
    }

    [Fact]
    public void 시간_안이면_살아_있다()
    {
        var p = new CloneProposal("id", new(0, "A", "S", 1), new(1, "B", "S", 1),
            "이유", true, true, false, DateTime.UtcNow.AddMinutes(-3), ProposalStatus.Pending);

        Assert.False(p.IsExpiredAt(DateTime.UtcNow));
        Assert.True(p.IsLiveAt(DateTime.UtcNow));
    }

    // --- 지문 대조 -----------------------------------------------------------

    [Fact]
    public void 지문은_번호가_아니라_정체로_대조한다()
    {
        var fp = DiskFingerprint.Of(Disk(1, "Samsung SSD", 500, "ABC"));

        Assert.True(fp.Matches(Disk(7, "Samsung SSD", 500, "ABC")));    // 번호만 다름 → 같은 디스크
        Assert.False(fp.Matches(Disk(1, "Samsung SSD", 500, "XYZ")));   // 시리얼 다름
        Assert.False(fp.Matches(Disk(1, "Samsung SSD", 999, "ABC")));   // 크기 다름
        Assert.False(fp.Matches(Disk(1, "Other SSD", 500, "ABC")));     // 모델 다름
    }
}
