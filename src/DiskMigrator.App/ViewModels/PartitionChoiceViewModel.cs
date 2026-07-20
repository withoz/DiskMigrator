using CommunityToolkit.Mvvm.ComponentModel;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;

namespace DiskMigrator.App.ViewModels;

/// <summary>리사이즈에서 확대할 파티션을 고르는 항목(막대 아래 칩).</summary>
public sealed partial class PartitionChoiceViewModel(PartitionInfo partition) : ObservableObject
{
    public PartitionInfo Partition { get; } = partition;

    public int Number => Partition.Number;

    /// <summary>이 항목이 선택됐는지. 칩(RadioButton)이 양방향으로 묶입니다.</summary>
    /// <remarks>
    /// 라디오 그룹은 꺼질 때도 false를 써 보냅니다. 그때 반대로 선택을 지우면 클릭 한 번에
    /// 아무것도 선택되지 않는 상태가 되므로, <b>true가 될 때만</b> 화면 뷰모델에 알립니다.
    /// </remarks>
    [ObservableProperty] private bool _isSelected;

    /// <summary>선택됐을 때 화면 뷰모델에 알리는 통로. 목록을 채우는 쪽이 붙여 줍니다.</summary>
    public Action<PartitionChoiceViewModel>? Selected { get; init; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) Selected?.Invoke(this);
    }

    public string Label
    {
        get
        {
            string letter = Partition.DriveLetter is null ? "" : $"({Partition.DriveLetter}:) ";
            string fs = Partition.FileSystem ?? "RAW";
            string label = string.IsNullOrWhiteSpace(Partition.VolumeLabel) ? "" : $" \"{Partition.VolumeLabel}\"";
            return $"파티션 {Partition.Number} {letter}{fs}{label} — {SizeFormatter.Format(Partition.LengthBytes)}";
        }
    }
}
