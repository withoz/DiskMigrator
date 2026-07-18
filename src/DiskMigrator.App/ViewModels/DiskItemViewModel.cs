using CommunityToolkit.Mvvm.ComponentModel;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;

namespace DiskMigrator.App.ViewModels;

/// <summary>디스크 목록에 표시되는 항목 하나.</summary>
public sealed partial class DiskItemViewModel(DiskInfo disk) : ObservableObject
{
    public DiskInfo Disk { get; } = disk;

    public int DeviceNumber => Disk.DeviceNumber;

    public string Model => Disk.Model;

    public string SizeText => SizeFormatter.Format(Disk.SizeBytes);

    public string DetailText
    {
        get
        {
            var parts = new List<string> { $"디스크 {Disk.DeviceNumber}", BusText, Disk.PartitionStyle.ToString().ToUpperInvariant() };

            if (Disk.SerialNumber is { } serial) parts.Add($"S/N {serial}");

            return string.Join("  ·  ", parts);
        }
    }

    private string BusText => Disk.BusType switch
    {
        DiskBusType.Nvme => "NVMe",
        DiskBusType.Sata => "SATA",
        DiskBusType.Usb => "USB",
        DiskBusType.Sas => "SAS",
        DiskBusType.RAID => "RAID",
        DiskBusType.Virtual or DiskBusType.FileBackedVirtual => "가상",
        DiskBusType.Unknown => "알 수 없음",
        _ => Disk.BusType.ToString().ToUpperInvariant(),
    };

    /// <summary>목록에서 눈에 띄게 표시할 경고 배지들.</summary>
    public IReadOnlyList<string> Badges
    {
        get
        {
            var badges = new List<string>();

            if (Disk.IsSystemDisk) badges.Add("시스템");
            if (Disk.IsBootDisk && !Disk.IsSystemDisk) badges.Add("부팅");
            if (Disk.HasPageFile && !Disk.IsSystemDisk) badges.Add("페이지 파일");
            if (Disk.IsReadOnly) badges.Add("읽기 전용");
            if (Disk.IsRemovable) badges.Add("착탈식");

            return badges;
        }
    }

    public bool HasBadges => Badges.Count > 0;

    public string BadgeText => string.Join(" · ", Badges);

    /// <summary>이 디스크를 덮어쓰면 사라지는 것들을 한 줄로 요약합니다.</summary>
    public string PartitionSummary
    {
        get
        {
            if (Disk.Partitions.Count == 0) return "파티션 없음 (초기화되지 않음)";

            var parts = Disk.Partitions.Select(p =>
            {
                string letter = p.DriveLetter is not null ? $"{p.DriveLetter}: " : "";
                string label = p.VolumeLabel is not null ? $"“{p.VolumeLabel}” " : "";
                return $"{letter}{label}{p.FileSystem ?? "RAW"} {SizeFormatter.Format(p.LengthBytes)}";
            });

            return string.Join("   |   ", parts);
        }
    }

    public override string ToString() => $"[{DeviceNumber}] {Model}";
}
