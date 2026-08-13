using CommunityToolkit.Mvvm.ComponentModel;
using DiskMigrator.App.Localization;
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

    /// <summary>
    /// 칩에 적을 이름 — <c>Windows (C:) 231.32 GB</c> · <c>복구 1 GB</c>.
    /// </summary>
    /// <remarks>
    /// 예전에는 <c>파티션 3 (C:) NTFS — 231.32 GB</c>였습니다. 파티션 번호는 사람이 쓰지
    /// 않는 정보이고, 파일 시스템 이름은 <b>무엇에 쓰는 칸인지</b> 알려 주지 않습니다.
    /// 바로 위 막대 범례는 같은 것을 이미 "복구"라고 부르고 있었습니다 — 같은 것을 두 이름으로
    /// 부르면 사용자는 다른 것인 줄 압니다.
    /// </remarks>
    public string Label => $"{PartitionNaming.ChipName(Partition)} {SizeFormatter.Format(Partition.LengthBytes)}";
}
