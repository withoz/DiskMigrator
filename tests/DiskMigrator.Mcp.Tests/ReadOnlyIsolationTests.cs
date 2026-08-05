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
}
