using System.Globalization;
using System.Runtime.CompilerServices;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 테스트 어셈블리 전체의 UI 문화권을 한국어로 고정합니다.
/// </summary>
/// <remarks>
/// 엔진 메시지는 L.T(한국어, 영어) 이중언어라 실행 문화권에 따라 달라집니다. 일부 테스트가
/// 메시지 본문(예: "들어가지 않습니다")을 검사하므로, 영어 OS/CI에서도 같은 결과가 나오도록
/// 여기서 ko-KR로 못 박습니다. xUnit은 테스트를 여러 스레드에서 돌리므로
/// DefaultThreadCurrentUICulture로 새 스레드까지 포함해 고정합니다.
/// </remarks>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void PinKorean()
    {
        var ko = new CultureInfo("ko-KR");
        CultureInfo.DefaultThreadCurrentUICulture = ko;
        CultureInfo.CurrentUICulture = ko;
    }
}
