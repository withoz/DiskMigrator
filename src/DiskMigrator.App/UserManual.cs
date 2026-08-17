using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DiskMigrator.App;

/// <summary>
/// 앱 옆에 함께 설치되는 사용설명서를 찾아 엽니다.
/// </summary>
/// <remarks>
/// <b>왜 화면에 길을 두는가.</b> 이 앱은 안내를 화면에 늘어놓지 않고 설명서로 보냅니다 —
/// 화면이 지저분해지면 정작 중요한 것(어느 디스크가 지워지는지)이 묻히기 때문입니다.
/// 그런데 <b>설명서로 가는 길이 화면에 없으면</b> 그 약속이 반만 지켜집니다. 사용자는
/// 설명서가 있다는 것조차 모른 채 시작 메뉴를 뒤져야 합니다(2026-08-17에 실제로 그렇게 됐습니다).
///
/// <para>⚠ <see cref="AppContext.BaseDirectory"/>가 아니라 <see cref="Environment.ProcessPath"/>를
/// 봅니다. 이 앱은 단일 파일로 배포되어 <b>임시 폴더에 풀린 뒤</b> 실행되므로 BaseDirectory는
/// 그 임시 폴더를 가리킵니다 — 거기에는 설명서가 없습니다. 중계기 찾기가 이미 겪은 자리입니다.</para>
/// </remarks>
public static class UserManual
{
    private const string Korean = "manual.html";
    private const string English = "manual-en.html";

    /// <summary>
    /// 지금 언어에 맞는 설명서의 전체 경로. 없으면 null.
    /// </summary>
    /// <remarks>
    /// 없으면 <b>화면에 링크를 띄우지 않습니다.</b> 눌러도 아무 일이 없는 글자는 앱이
    /// 고장 난 것처럼 보이게 합니다 — 개발용 빌드처럼 설명서가 함께 있지 않은 경우입니다.
    ///
    /// <para>고른 언어의 파일이 없으면 다른 쪽이라도 엽니다. 읽을 수 없는 것보다는
    /// 다른 언어로라도 읽는 편이 낫습니다.</para>
    /// </remarks>
    public static string? Find()
    {
        string? folder = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(folder)) return null;

        bool korean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

        foreach (string name in korean ? new[] { Korean, English } : [English, Korean])
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>기본 브라우저로 엽니다. 실패해도 조용히 넘어갑니다.</summary>
    /// <remarks>
    /// 브라우저가 없는 환경(부팅 USB 등)에서는 열리지 않습니다. 그것 때문에 앱이 멈추거나
    /// 오류 상자를 띄우면 안 됩니다 — 지금 하려던 일은 디스크 작업이지 설명서 읽기가 아닙니다.
    /// </remarks>
    public static void Open()
    {
        if (Find() is not { } path) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 브라우저가 없거나 연결이 없는 환경. 시작 메뉴에도 같은 바로가기가 있습니다.
        }
    }
}
