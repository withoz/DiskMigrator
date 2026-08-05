using System.IO;

namespace DiskMigrator.App;

/// <summary>
/// 이 앱이 무엇이고 자기 파일을 어디에 두는지 — <b>한곳에서</b> 정합니다.
/// </summary>
/// <remarks>
/// DiskMigrator-X는 수동 버전 DiskMigrator와 **같은 PC에 함께 설치됩니다.** 두 앱이 같은
/// 데이터 폴더를 쓰면 한쪽의 언어 설정과 EULA 동의가 다른 쪽에 새고, 로그가 한 파일에 섞여
/// 문제를 신고받았을 때 어느 앱의 것인지 알 수 없게 됩니다.
///
/// <para>2026-08-05에 실제로 그 일이 있었습니다 — 같은 로그 파일 하나에 X의 MCP 호출 186줄과
/// 수동 버전의 부팅 검사 10줄이 함께 남았습니다.</para>
///
/// <para>폴더 이름이 세 파일에 각각 하드코딩돼 있었기에 여기로 모읍니다. 흩어져 있으면
/// 다음 이름 변경에서 반드시 하나를 빠뜨립니다.</para>
/// </remarks>
public static class AppIdentity
{
    /// <summary>사용자에게 보이는 제품명.</summary>
    public const string ProductName = "DiskMigrator-X";

    /// <summary>
    /// 설정·로그·동의 표시를 두는 폴더. <c>%LocalAppData%\DiskMigrator-X</c>.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    /// <summary>이번 실행의 로그가 쌓이는 폴더.</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
}
