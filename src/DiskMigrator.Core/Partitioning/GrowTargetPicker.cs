using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Partitioning;

/// <summary>
/// 남는 공간이 생겼을 때 <b>사람이 넓히려 했을 파티션</b>을 고릅니다.
/// </summary>
/// <remarks>
/// <b>왜 필요한가.</b> 예전에는 화면이 후보 목록의 첫 항목을 골랐습니다. 후보는 디스크 순서로
/// 담기므로 첫 항목은 언제나 <c>시스템 예약</c>(479 MB)이었고, 크기 칸에는 그 파티션의 현재
/// 크기가 들어갔습니다 — 즉 <b>[파티션 조정]을 골라도 기본 상태는 "아무것도 안 함"</b>이었습니다.
/// 2026-08-13에 <b>만든 사람조차</b> 그대로 시작할 뻔했습니다.
///
/// <para><b>규칙.</b> <c>C:</c>가 붙은 파티션을 먼저 찾고, 없으면 <b>가장 큰</b> 것을 씁니다.
/// 실행 중이 아닌 디스크를 복제할 때는 Windows 파티션에 <c>C:</c>가 아닌 문자가 붙거나 아예
/// 없을 수 있는데, 그런 디스크에서도 Windows가 든 파티션이 가장 큰 것이 사실상 예외가 없습니다.
/// 반대로 <c>시스템 예약</c>·<c>복구</c>는 언제나 작습니다.</para>
///
/// <para><b>왜 화면이 아니라 여기인가.</b> 이 규칙이 틀리면 사용자는 엉뚱한 파티션이 커진
/// 디스크를 갖게 됩니다. 화면 코드에 두면 시험할 방법이 없어, 다음에 누가 손댈 때 조용히
/// 무너집니다 — <see cref="FreeSpacePlanner"/>에서 이미 겪은 일입니다.</para>
/// </remarks>
public static class GrowTargetPicker
{
    /// <param name="candidates">넓힐 수 있는 파티션들(화면이 NTFS만 넘겨줍니다).</param>
    /// <returns>고른 파티션. 후보가 없으면 null.</returns>
    public static PartitionInfo? Preferred(IReadOnlyList<PartitionInfo> candidates)
    {
        if (candidates.Count == 0) return null;

        var withC = candidates.FirstOrDefault(
            p => string.Equals(p.DriveLetter, "C", StringComparison.OrdinalIgnoreCase));

        // MaxBy는 같은 크기가 여럿이면 먼저 나온 것을 돌려줍니다 — 디스크 순서라 앞쪽입니다.
        return withC ?? candidates.MaxBy(p => p.LengthBytes);
    }
}
