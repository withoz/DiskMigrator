using System.Reflection;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Mcp;
using DiskMigrator.Mcp.Tools;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 진단 도구가 <b>쓰기 경로에 닿을 수 없음</b>을 검증합니다 — 계획서 §4·§10.
/// </summary>
/// <remarks>
/// 이 프로젝트는 디스크를 통째로 지울 수 있는 도구입니다. AI가 호출하는 계층을 여는 이상,
/// "지금은 아무도 안 부른다"로는 부족합니다. 나중에 도구를 추가하는 사람이 무심코
/// 쓰기 서비스를 끌어오는 것을 <b>테스트가 막습니다</b>.
/// </remarks>
public class ReadOnlyIsolationTests
{
    /// <summary>
    /// 읽기 전용 도구의 생성자는 읽기 통로만 받아야 합니다.
    /// <see cref="IDiskService"/>(안전 제거 등 부작용 포함)를 받으면 실패합니다.
    /// </summary>
    [Fact]
    public void ReadOnlyTools_생성자는_쓰기_가능한_서비스를_받지_않는다()
    {
        var ctor = typeof(ReadOnlyTools).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(IDiskService), paramTypes);
        Assert.Contains(typeof(IDiskReader), paramTypes);
    }

    /// <summary>
    /// 읽기 통로에는 부작용이 있는 메서드가 없어야 합니다.
    /// </summary>
    [Fact]
    public void IDiskReader에는_쓰기_메서드가_없다()
    {
        var names = typeof(IDiskReader).GetMembers().Select(m => m.Name).ToArray();

        // 부작용을 뜻하는 이름이 하나라도 있으면 설계가 샌 것입니다.
        foreach (string forbidden in new[] { "Remove", "Write", "Delete", "Clone", "Restore", "Format", "Offline" })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// <see cref="IDiskService"/>의 위험한 메서드들이 읽기 통로로 새어 나오지 않았는지
    /// <b>이름을 직접 지목해</b> 확인합니다.
    /// </summary>
    /// <remarks>
    /// 이 목록은 추측이 아닙니다. <c>OpenWriteExclusive</c>는 주석에 "대상 디스크의 기존 데이터를
    /// 파괴합니다"라고 적혀 있고, <c>SafeRemoveAsync</c>는 볼륨을 내리고 디스크를 오프라인으로 바꿉니다.
    /// 진단 도구가 이런 것에 닿을 수 있으면 계획서 §4의 첫 원칙이 무너집니다.
    /// </remarks>
    [Fact]
    public void 위험한_메서드는_읽기_통로에_없다()
    {
        var readerMembers = typeof(IDiskReader).GetMembers().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (string dangerous in new[]
        {
            nameof(IDiskService.OpenWriteExclusive),   // 디스크를 파괴할 수 있는 핸들
            nameof(IDiskService.RefreshDiskProperties),
            nameof(IDiskService.SafeRemoveAsync),      // 볼륨 디스마운트·오프라인 전환
        })
        {
            Assert.DoesNotContain(dangerous, readerMembers);
        }

        // 반대로 읽기에 필요한 것은 있어야 합니다.
        Assert.Contains(nameof(IDiskService.EnumerateDisksAsync), readerMembers);
    }

    /// <summary>
    /// 진단 도구가 참조하는 필드에 쓰기 서비스가 숨어 있지 않아야 합니다.
    /// (생성자만 보면 뒤에서 정적 접근으로 끌어오는 경우를 놓칩니다.)
    /// </summary>
    [Fact]
    public void ReadOnlyTools_필드에_쓰기_서비스가_없다()
    {
        var fieldTypes = typeof(ReadOnlyTools)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDiskService), fieldTypes);
    }

    /// <summary>
    /// <b>도구 클래스 전부</b>가 쓰기 서비스에 닿지 않아야 합니다.
    /// </summary>
    /// <remarks>
    /// 앞의 시험들은 <see cref="ReadOnlyTools"/> 하나만 봤습니다. 그런데 도구 클래스는 셋이고
    /// (읽기·계획·제안), 다음에 넷째를 추가하는 사람은 앞의 시험을 보고 "이미 막혀 있구나"라고
    /// 생각할 것입니다 — 정작 자기 클래스는 검사받지 않는데도.
    ///
    /// <para>그래서 <b>이름으로 찾지 않고</b> 어셈블리에서 도구 클래스를 전부 긁어 옵니다.
    /// 새 도구 클래스를 만들면 자동으로 이 시험의 대상이 됩니다.</para>
    /// </remarks>
    [Fact]
    public void 모든_도구_클래스가_쓰기_서비스에_닿지_않는다()
    {
        var toolTypes = typeof(ReadOnlyTools).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                        t.GetMethods().Any(m => m.GetCustomAttributes()
                            .Any(a => a.GetType().Name.Contains("McpServerTool", StringComparison.Ordinal))))
            .ToList();

        // 셋(읽기·계획·제안)은 있어야 합니다. 0개면 찾는 방법이 깨진 것이지 안전한 것이 아닙니다.
        Assert.True(toolTypes.Count >= 3, $"도구 클래스를 {toolTypes.Count}개만 찾았습니다 — 검사 방법을 확인하십시오.");

        foreach (var t in toolTypes)
        {
            var reachable = t.GetConstructors()
                .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         .Select(f => f.FieldType))
                .ToList();

            Assert.DoesNotContain(typeof(IDiskService), reachable);
        }
    }

    /// <summary>
    /// 제안 도구는 <b>화면에 카드를 띄울 뿐</b> 실행을 시작할 수 없어야 합니다.
    /// </summary>
    /// <remarks>
    /// 이 제품의 약속은 "Claude는 읽기만 한다"입니다. 그 약속이 성립하려면 <b>시작을 부르는
    /// 통로 자체가 없어야</b> 합니다 — 확인 절차가 있어서가 아니라, 부를 것이 없어서.
    ///
    /// <para><see cref="IAppState"/>가 앱과 도구를 잇는 유일한 창구이므로 여기만 지키면 됩니다.
    /// 취소(<c>RequestCancel</c>)는 허용합니다 — 하던 일을 멈추는 것은 파괴가 아닙니다.</para>
    /// </remarks>
    [Fact]
    public void 도구는_작업을_시작시킬_수_없다()
    {
        var names = typeof(IAppState).GetMembers().Select(m => m.Name).ToArray();

        foreach (string forbidden in new[]
        {
            "Start", "Begin", "Run", "Execute", "Apply", "Confirm", "Clone", "Restore", "Backup", "Repair",
        })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // 멈추는 것과 들여다보는 것만 있어야 합니다.
        Assert.Contains(nameof(IAppState.RequestCancel), names);
        Assert.Contains(nameof(IAppState.GetProgress), names);
    }
}
