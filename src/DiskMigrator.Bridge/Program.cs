using System.Runtime.InteropServices;
using DiskMigrator.Bridge;

// Claude 데스크톱 앱이 이 실행 파일을 켜고 표준입출력으로 이야기합니다.
// 표준입력이 닫히면(=Claude가 종료하면) 중계도 끝납니다.
//
// 인자를 요구하지 않습니다 — 이 실행 파일은 중계 말고 하는 일이 없습니다. 앱이 설정에
// 적어 두는 --mcp-stdio 인자는 받아도 그만인 표시로 남겨 둡니다(사람이 설정 파일을 봤을 때
// 무엇을 하는 프로그램인지 알아볼 수 있게).

// ⚠ 사람이 두 번 눌러 켠 경우.
//
// 앱과 같은 아이콘을 달아 두었으므로, 앱인 줄 알고 이것을 누르는 일이 생깁니다. 그때 이
// 프로그램은 넘겨받을 말이 없어 <b>조용히 사라집니다</b> — 사용자에게는 "눌렀는데 아무 일도
// 안 일어나는 프로그램"으로 보이고, 그런 파일은 십중팔구 지워집니다. 지우면 앱의 Claude
// 관련 버튼이 함께 사라집니다.
//
// 넘겨받을 통로가 없다는 것으로 사람이 켰음을 알 수 있습니다.
if (!Console.IsInputRedirected)
{
    NativeMethods.MessageBox(IntPtr.Zero,
        "이 프로그램은 직접 실행하는 것이 아닙니다.\n\n" +
        "DiskMigrator-X와 Claude 사이에서 말을 옮기는 역할만 하며, 필요할 때 Claude가 알아서 켭니다.\n\n" +
        "디스크를 복제하거나 백업하시려면 옆에 있는 DiskMigratorX.exe를 실행하십시오.\n\n" +
        "이 파일을 지우면 앱의 Claude 관련 기능을 쓸 수 없게 됩니다.",
        "DiskMigrator-X",
        NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
    return 0;
}

return await StdioBridge.RunAsync();

// 창을 하나 띄우자고 WPF나 WinForms를 통째로 끌어들이지 않습니다 — 이 실행 파일은
// 작아야 합니다(Claude가 켤 때마다 로드됩니다).
// LibraryImport가 아니라 DllImport입니다 — 앞의 것은 프로젝트에 안전하지 않은 코드를
// 허용해야 하는데, 창 하나 띄우자고 그 문을 열지는 않습니다.
static class NativeMethods
{
    internal const uint MB_OK = 0x0;
    internal const uint MB_ICONINFORMATION = 0x40;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
