using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Registry;

/// <summary>
/// 이 디스크에 <b>최대 절전 이미지</b>(<c>hiberfil.sys</c>)가 남아 있는지 — <b>한곳에서만</b> 답합니다.
/// </summary>
/// <remarks>
/// <b>왜 중요한가.</b> Windows는 "시스템 종료"를 눌러도 커널 상태를 이 파일에 저장합니다
/// (빠른 시작). 그 상태는 <b>원래 PC의 하드웨어를 전제</b>하므로, 사본을 다른 컴퓨터에 꽂으면
/// 복원에 실패하고 <b>아무 글자 없는 검은 화면에서 멈춥니다.</b> 2026-07 실기에서 며칠을
/// 잡아먹은 원인이고, 지금은 안전 판정과 부팅 복구가 함께 보는 값입니다.
///
/// <para><b>왜 한곳에 모으나.</b> 같은 질문에 답하는 코드가 화면과 도구에 따로 있었습니다 —
/// 화면은 파일 존재만 보고, 도구는 <see cref="FastStartupState"/>를 거쳐 보고 실패하면 파일로
/// 되돌아갔습니다. 오늘은 두 답이 같지만, <b>한쪽만 고치는 날</b>이 오면 Claude가 "안전하다"고
/// 하는데 화면은 경고를 띄우게 됩니다. 사용자는 둘 중 어느 쪽을 믿어야 할지 모릅니다.</para>
///
/// <para><b>확인하지 못한 것은 "없음"으로 둡니다.</b> 볼륨이 마운트되지 않았거나 접근이 막히면
/// false입니다 — 읽지 못한 것을 문제로 단정하지 않습니다. 이 제품이 부팅 검사에서 배운 규칙입니다.</para>
/// </remarks>
public static class HibernationImage
{
    /// <summary>이 디스크의 Windows 폴더에 재개 이미지가 있는지.</summary>
    public static bool IsPresent(DiskInfo disk)
    {
        try
        {
            string? windowsRoot = BootReadinessCheck.ResolveInput(disk).WindowsRoot;
            return IsPresentAt(windowsRoot);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>볼륨 루트를 이미 알고 있을 때.</summary>
    /// <param name="windowsRoot">
    /// ⚠ 이름과 달리 <b>Windows가 설치된 볼륨의 루트</b>입니다 — <c>C:\Windows</c>가 아니라
    /// <c>C:\</c>. <see cref="BootCheckInput.WindowsRoot"/>가 그렇게 정의돼 있고,
    /// <c>hiberfil.sys</c>도 볼륨 루트에 있습니다. null이면 Windows를 찾지 못한 것이므로 false.
    /// </param>
    public static bool IsPresentAt(string? windowsRoot)
    {
        if (string.IsNullOrWhiteSpace(windowsRoot)) return false;

        try
        {
            return File.Exists(Path.Combine(windowsRoot, "hiberfil.sys"));
        }
        catch
        {
            return false;
        }
    }
}
