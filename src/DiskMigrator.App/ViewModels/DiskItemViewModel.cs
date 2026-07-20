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

    /// <summary>
    /// 지우면 곤란한 디스크임을 알리는 배지 — 빨강으로 표시합니다.
    /// </summary>
    /// <remarks>
    /// 실행 중인 Windows·부팅 요소·페이지 파일이 있는 디스크는 덮어쓰면 이 PC가 못 켜집니다.
    /// 단순한 사실 표시(<see cref="InfoBadges"/>)와 색을 갈라, 진짜 위험만 빨강이 되게 합니다.
    /// </remarks>
    public IReadOnlyList<string> WarningBadges
    {
        get
        {
            var badges = new List<string>();

            if (Disk.IsSystemDisk) badges.Add("시스템");
            if (Disk.IsBootDisk && !Disk.IsSystemDisk) badges.Add("부팅");
            if (Disk.HasPageFile && !Disk.IsSystemDisk) badges.Add("페이지 파일");
            if (Disk.IsReadOnly) badges.Add("읽기 전용");

            return badges;
        }
    }

    /// <summary>위험과 무관한 사실 표시 — 파랑으로 표시합니다.</summary>
    public IReadOnlyList<string> InfoBadges =>
        Disk.IsRemovable ? ["착탈식"] : [];

    public bool HasWarningBadges => WarningBadges.Count > 0;
    public bool HasInfoBadges => InfoBadges.Count > 0;

    public string WarningBadgeText => string.Join(" · ", WarningBadges);
    public string InfoBadgeText => string.Join(" · ", InfoBadges);

    /// <summary>이 디스크가 가진 드라이브 문자 — 목록에서 디스크를 알아보는 가장 빠른 단서입니다.</summary>
    /// <remarks>
    /// 파티션 요약 안에 섞어 두면 앞에 EFI·MSR이 있는 디스크는 문자가 줄 한가운데로 밀려
    /// 위치가 항목마다 달라집니다. 눈이 훑을 기준선이 없어져 알아보기 어려우므로
    /// 요약에서 떼어내 항상 줄 맨 앞에 둡니다.
    /// </remarks>
    public string DriveLetterText
    {
        get
        {
            var letters = Disk.Partitions
                .Select(p => p.DriveLetter)
                .Where(l => l is not null)
                .Select(l => $"{l}:");

            return string.Join(" ", letters);
        }
    }

    /// <summary>드라이브 문자가 없는 디스크도 칸 너비는 유지해 아래 줄이 어긋나지 않게 합니다.</summary>
    public bool HasDriveLetters => DriveLetterText.Length > 0;

    /// <summary>이 디스크를 덮어쓰면 사라지는 것들을 한 줄로 요약합니다.</summary>
    public string PartitionSummary
    {
        get
        {
            if (Disk.Partitions.Count == 0) return "파티션 없음 (초기화되지 않음)";

            var parts = Disk.Partitions.Select(p =>
            {
                string label = p.VolumeLabel is not null ? $"“{p.VolumeLabel}” " : "";
                return $"{label}{p.FileSystem ?? "RAW"} {SizeFormatter.Format(p.LengthBytes)}";
            });

            return string.Join("   |   ", parts);
        }
    }

    public override string ToString() => $"[{DeviceNumber}] {Model}";
}
