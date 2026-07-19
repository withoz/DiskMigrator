using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;

namespace DiskMigrator.App.ViewModels;

/// <summary>리사이즈에서 확대할 파티션을 고르는 콤보박스 항목.</summary>
public sealed class PartitionChoiceViewModel(PartitionInfo partition)
{
    public PartitionInfo Partition { get; } = partition;

    public int Number => Partition.Number;

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
