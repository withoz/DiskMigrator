using DiskMigrator.App.Localization;
using DiskMigrator.Core.Util;

namespace DiskMigrator.App.ViewModels;

/// <summary>축소 복원에서 고를 수 있는 이미지 내 NTFS 파티션 하나.</summary>
/// <remarks>
/// 복원할 이미지를 고르면 그 안의 NTFS 파티션들을 읽어 이 목록을 만듭니다. 사용자가 하나를 골라
/// 목표 크기를 정하면, 복원 시 그 파티션을 줄여(뒤 파티션은 왼쪽으로 당겨) 더 작은 대상에 맞춥니다.
/// </remarks>
public sealed class ShrinkPartitionChoice(int number, long currentBytes, string? driveLetter, string? fileSystem)
{
    /// <summary>파티션 번호(<see cref="Core.Models.PartitionInfo.Number"/>).</summary>
    public int Number { get; } = number;

    /// <summary>현재 파티션 크기(바이트). 목표는 이보다 작아야 합니다.</summary>
    public long CurrentBytes { get; } = currentBytes;

    /// <summary>드롭다운에 보일 이름 — "파티션 2 · C: · NTFS · 930 GB".</summary>
    public string DisplayLabel { get; } =
        $"{Strings.Get("PartitionWord")} {number} · " +
        $"{(string.IsNullOrEmpty(driveLetter) ? "" : $"{driveLetter}: · ")}{fileSystem ?? "NTFS"} · " +
        SizeFormatter.Format(currentBytes);
}
