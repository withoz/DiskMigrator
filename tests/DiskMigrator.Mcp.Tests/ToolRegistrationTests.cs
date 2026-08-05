using System.ComponentModel;
using System.Reflection;
using DiskMigrator.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 도구가 <b>실제로 등록되고 Claude가 이해할 수 있는 설명을 갖췄는지</b> 확인합니다.
/// </summary>
/// <remarks>
/// 도구를 만들어 놓고 특성을 빠뜨리면 조용히 목록에서 사라집니다 — 빌드도 테스트도 통과하는데
/// Claude에게는 그 도구가 없는 것과 같습니다. 설명이 부실한 것도 비슷합니다. 도구는 Claude가
/// <b>읽고 스스로 고르는</b> 것이라, 설명이 곧 인터페이스입니다.
/// </remarks>
public class ToolRegistrationTests
{
    private static MethodInfo[] ToolMethods =>
        typeof(ReadOnlyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

    /// <summary>1단계에서 갖추기로 한 도구가 전부 있는지 — 계획서 §6.1.</summary>
    [Fact]
    public void 계획한_1단계_도구가_모두_등록되어_있다()
    {
        var names = ToolMethods
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string expected in new[]
        {
            "list_disks", "inspect_disk",
            "check_boot_readiness", "read_boot_drivers", "read_fast_startup",
            "analyze_boot_trace", "audit_esp",
            "inspect_image", "check_hardware_compatibility",
            "save_diagnostic", "load_diagnostic", "diff_diagnostics",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    /// <summary>
    /// 모든 도구에 설명이 있어야 합니다. Claude는 설명을 읽고 언제 무엇을 부를지 정합니다.
    /// </summary>
    [Fact]
    public void 모든_도구에_쓸모_있는_설명이_붙어_있다()
    {
        foreach (var m in ToolMethods)
        {
            string name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            var desc = m.GetCustomAttribute<DescriptionAttribute>();

            Assert.True(desc is not null, $"{name}: 설명이 없습니다.");

            // 한 줄짜리 설명으로는 Claude가 언제 써야 할지 판단할 수 없습니다.
            Assert.True(desc!.Description.Length >= 80,
                $"{name}: 설명이 너무 짧습니다({desc.Description.Length}자).");
        }
    }

    /// <summary>디스크를 받는 도구는 인자에도 설명이 있어야 합니다.</summary>
    [Fact]
    public void 도구_인자에도_설명이_붙어_있다()
    {
        foreach (var m in ToolMethods)
        {
            string name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name!;

            foreach (var p in m.GetParameters())
            {
                // CancellationToken은 프레임워크가 채우므로 설명이 필요 없습니다.
                if (p.ParameterType == typeof(CancellationToken)) continue;

                Assert.True(p.GetCustomAttribute<DescriptionAttribute>() is not null,
                    $"{name}의 인자 '{p.Name}'에 설명이 없습니다.");
            }
        }
    }

    /// <summary>
    /// 도구 이름은 snake_case로 통일합니다 — 뒤섞이면 Claude가 이름을 잘못 부를 수 있습니다.
    /// </summary>
    [Fact]
    public void 도구_이름은_snake_case_규칙을_따른다()
    {
        foreach (var m in ToolMethods)
        {
            string name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            Assert.Matches("^[a-z][a-z0-9_]*$", name);
        }
    }

    /// <summary>
    /// 하이브·파일을 읽는 도구는 "실행 중인 디스크에는 못 쓴다"는 사실을 설명에 담아야 합니다.
    /// </summary>
    /// <remarks>
    /// 이 제약을 모르면 Claude가 시스템 디스크를 지목했다가 오류를 받고, 원인을 엉뚱한 데서 찾습니다.
    /// 미리 알려 주면 처음부터 올바른 디스크를 고릅니다.
    /// </remarks>
    [Fact]
    public void 하이브를_읽는_도구는_제약을_설명에_밝힌다()
    {
        foreach (string toolName in new[] { "read_boot_drivers", "read_fast_startup", "analyze_boot_trace" })
        {
            var m = ToolMethods.Single(x => x.GetCustomAttribute<McpServerToolAttribute>()!.Name == toolName);
            string desc = m.GetCustomAttribute<DescriptionAttribute>()!.Description;

            Assert.Contains("cannot target the disk", desc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
