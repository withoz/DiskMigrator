using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DiskMigrator.Windows.Pe;

/// <summary>
/// 지금 <b>부팅 USB(WinPE) 안에서</b> 돌고 있는지.
/// </summary>
/// <remarks>
/// <b>왜 한곳에 모으나.</b> 같은 판별이 세 곳에 각각 적혀 있었습니다(시작 시 EULA 건너뛰기,
/// 파일 선택 창, 진단 리포트). 조건이 바뀌면 한 곳을 빠뜨리고, 그러면 <b>어떤 화면은
/// WinPE인 줄 알고 어떤 화면은 모르는</b> 상태가 됩니다.
///
/// <para><b>WinPE에서 달라지는 것.</b> 램디스크라 껐다 켜면 사라지고, Node.js도 인터넷도
/// 없는 것이 보통입니다. 그래서 <b>Claude Code를 설치할 수 없습니다</b> — 그런데 부팅 USB는
/// 윈도우가 아예 안 켜질 때 쓰는 마지막 수단이라, 가장 절박한 순간입니다. 그 자리에서는
/// 앱이 Claude 없이 온전해야 합니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WinPeEnvironment
{
    /// <summary>
    /// WinPE는 <c>SYSTEM\CurrentControlSet\Control\MiniNT</c> 키가 있는 것으로 알아봅니다.
    /// </summary>
    /// <remarks>
    /// 한 번 읽고 기억합니다 — 실행 중에 바뀌지 않습니다.
    /// </remarks>
    public static bool IsWinPe { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            return Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT") is not null;
        }
        catch
        {
            // 레지스트리를 못 읽으면 보통 윈도우로 봅니다 — WinPE에서는 읽힙니다.
            return false;
        }
    }
}
