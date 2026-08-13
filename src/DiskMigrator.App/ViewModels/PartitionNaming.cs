using DiskMigrator.App.Localization;
using DiskMigrator.Core.Models;

namespace DiskMigrator.App.ViewModels;

/// <summary>
/// 파티션을 <b>사람이 부르는 이름</b>으로 — 한곳에서만 정합니다.
/// </summary>
/// <remarks>
/// <b>왜 모으나.</b> 막대 범례는 "복구"라고 부르는데 그 아래 칩은 같은 것을
/// <c>파티션 4 NTFS — 1 GB</c>라고 불렀습니다. 사용자는 <b>다른 것인 줄 압니다.</b>
/// 파티션 번호는 사람이 쓰지 않는 정보이고, 파일 시스템 이름은 무엇에 쓰는 칸인지
/// 알려 주지 않습니다.
///
/// <para>2026-08-13에 이 화면에서 <b>만든 사람이 잘못된 파티션을 골랐습니다</b> —
/// 첫 항목이 <c>시스템 예약</c>이었는데 이름만 보고는 그것이 무엇인지 알기 어려웠습니다.</para>
/// </remarks>
public static class PartitionNaming
{
    /// <summary>Microsoft 예약 파티션(MSR)의 GPT 타입.</summary>
    public static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    /// <summary>무엇에 쓰는 칸인지 — "EFI 시스템" · "MSR (예약)" · "복구" · 파일 시스템 이름.</summary>
    public static string Role(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return Strings.Get("RoleEfi");
        if (p.GptPartitionType == MicrosoftReserved) return Strings.Get("RoleMsr");
        if (p.IsWindowsRecovery) return Strings.Get("RoleRecovery");
        return p.FileSystem ?? "RAW";
    }

    /// <summary>막대 안에 넣을 짧은 이름 — 좁은 조각에 긴 글자는 안 들어갑니다.</summary>
    public static string ShortRole(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EFI";
        if (p.GptPartitionType == MicrosoftReserved) return "MSR";
        if (p.IsWindowsRecovery) return Strings.Get("RoleRecovery");
        return p.FileSystem ?? "RAW";
    }

    /// <summary>
    /// 범례·칩에 쓰는 이름. 드라이브 문자와 볼륨 이름이 있으면 그것이, 없으면 역할이 이름입니다.
    /// </summary>
    public static string Title(PartitionInfo p)
    {
        string letter = p.DriveLetter is { } dl ? $"{dl}: " : "";
        string label = string.IsNullOrWhiteSpace(p.VolumeLabel) ? "" : $"{p.VolumeLabel} ";
        string title = $"{letter}{label}".Trim();
        return title.Length == 0 ? Role(p) : title;
    }

    /// <summary>
    /// 넓힐 파티션을 고르는 칩에 적을 이름.
    /// </summary>
    /// <remarks>
    /// <c>C:</c>는 <b>Windows</b>라고 부릅니다. 사용자가 넓히려는 것은 "C 드라이브"가 아니라
    /// "윈도우가 들어 있는 칸"이고, 그렇게 불러야 <c>시스템 예약</c>·<c>복구</c>와 한눈에
    /// 구분됩니다.
    /// </remarks>
    public static string ChipName(PartitionInfo p) =>
        string.Equals(p.DriveLetter, "C", StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("PartitionWindows")
            : Title(p);

    /// <summary>
    /// 이 파티션을 넓혀도 <b>Windows 공간은 그대로</b>인지 — 화면이 미리 말해 줍니다.
    /// </summary>
    /// <remarks>
    /// 복구·예약 칸을 넓히는 것이 잘못은 아닙니다. 다만 <b>모르고 그러는 일</b>이 잦고,
    /// 그때 사용자는 복제를 마친 뒤에야 C:가 그대로인 것을 봅니다 — 되돌리려면 처음부터
    /// 다시 해야 합니다.
    /// </remarks>
    public static bool IsSideRole(PartitionInfo p) =>
        p.IsEfiSystemPartition ||
        p.GptPartitionType == MicrosoftReserved ||
        p.IsWindowsRecovery ||
        (p.DriveLetter is null && !string.IsNullOrWhiteSpace(p.VolumeLabel));
}
