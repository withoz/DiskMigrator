using Xunit;

namespace DiskMigrator.Mcp.Tests;

/// <summary>
/// 실제 서버를 띄우는 시험들을 <b>한 줄로 세웁니다.</b>
/// </summary>
/// <remarks>
/// xUnit은 클래스가 다르면 병렬로 돌립니다. 그런데 <c>McpHost</c>는 정해진 포트 범위에서
/// 빈 자리를 찾아 묶으므로, 두 시험이 동시에 "비었다"고 판단한 뒤 같은 포트에 묶으려다
/// <c>address already in use</c>로 깨집니다. 실제로 그렇게 깨졌습니다.
///
/// <para>앱이 이미 그 범위의 포트를 쓰고 있을 때 더 잘 드러납니다 — 남은 자리가 줄어
/// 충돌 확률이 올라가기 때문입니다. 시험이 개발 환경에 따라 되기도 안 되기도 하면
/// 아무도 시험을 믿지 않게 되므로, 아예 순차로 돌립니다.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostTestCollection
{
    public const string Name = "mcp-host";
}
