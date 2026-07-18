using System.Runtime.CompilerServices;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Safety;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;

// 실제 하드웨어에서 저수준 계층이 올바로 동작하는지 확인하는 진단 도구입니다.
// 읽기만 하며, 어떤 디스크에도 쓰지 않습니다.

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 진단 하위 명령 — 검증 실패 원인을 측정으로 좁히기 위한 것들입니다. 모두 읽기 전용입니다.
if (args.Length > 0 && args[0] == "--diff" && args.Length >= 5)
{
    DiskMigrator.Probe.DiffCheck.Run(
        int.Parse(args[1]), int.Parse(args[2]), long.Parse(args[3]), long.Parse(args[4]));
    return 0;
}

if (args.Length > 0 && args[0] == "--dump" && args.Length >= 3)
{
    DiskMigrator.Probe.DiffCheck.Dump(
        int.Parse(args[1]), long.Parse(args[2]), args.Length >= 4 ? int.Parse(args[3]) : 512);
    return 0;
}

Console.WriteLine("=== 네이티브 구조체 크기 검증 ===");
Console.WriteLine("Win32 헤더와 크기가 어긋나면 파티션 정보를 잘못 읽게 되므로 먼저 확인합니다.\n");

var expected = new (string Name, int Actual, int Expected)[]
{
    ("DISK_GEOMETRY_EX", SizeOf<DiskMigrator.Windows.Interop.DISK_GEOMETRY_EX>(), 32),
    ("STORAGE_DEVICE_DESCRIPTOR", SizeOf<DiskMigrator.Windows.Interop.STORAGE_DEVICE_DESCRIPTOR>(), 36),
    ("DISK_EXTENT", SizeOf<DiskMigrator.Windows.Interop.DISK_EXTENT>(), 24),
    ("PARTITION_INFORMATION_MBR", SizeOf<DiskMigrator.Windows.Interop.PARTITION_INFORMATION_MBR>(), 24),
    ("PARTITION_INFORMATION_GPT", SizeOf<DiskMigrator.Windows.Interop.PARTITION_INFORMATION_GPT>(), 112),
    ("PARTITION_INFORMATION_EX", SizeOf<DiskMigrator.Windows.Interop.PARTITION_INFORMATION_EX>(), 144),
    ("DRIVE_LAYOUT_INFORMATION_EX", SizeOf<DiskMigrator.Windows.Interop.DRIVE_LAYOUT_INFORMATION_EX>(), 48),
    ("STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR", SizeOf<DiskMigrator.Windows.Interop.STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>(), 28),
    // SET_DISK_ATTRIBUTES의 Version 필드에 이 크기를 넣습니다. 틀리면 오프라인 IOCTL이
    // ERROR_INVALID_PARAMETER로 조용히 실패해, 대상 볼륨 오염 버그가 되살아납니다.
    ("SET_DISK_ATTRIBUTES", SizeOf<DiskMigrator.Windows.Interop.SET_DISK_ATTRIBUTES>(), 40),
    ("GET_DISK_ATTRIBUTES", SizeOf<DiskMigrator.Windows.Interop.GET_DISK_ATTRIBUTES>(), 16),
};

bool allOk = true;
foreach (var (name, actual, want) in expected)
{
    bool ok = actual == want;
    allOk &= ok;
    Console.WriteLine($"  {(ok ? "OK  " : "실패")} {name,-38} {actual,4} 바이트 (기대 {want})");
}

Console.WriteLine(allOk ? "\n구조체 레이아웃 정상.\n" : "\n*** 구조체 레이아웃 불일치! ***\n");

Console.WriteLine("=== 디스크 열거 ===\n");

var service = new WindowsDiskService();
Console.WriteLine($"관리자 권한: {(service.IsElevated ? "예" : "아니오 — 일부 정보가 빠질 수 있습니다")}\n");

var disks = await service.EnumerateDisksAsync();

foreach (var disk in disks)
{
    var flags = new List<string>();
    if (disk.IsSystemDisk) flags.Add("시스템");
    if (disk.IsBootDisk) flags.Add("부팅");
    if (disk.HasPageFile) flags.Add("페이지파일");
    if (disk.IsRemovable) flags.Add("착탈식");
    if (disk.IsReadOnly) flags.Add("읽기전용");

    string flagText = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : "";

    Console.WriteLine($"[{disk.DeviceNumber}] {disk.Model}{flagText}");
    Console.WriteLine($"     크기      : {SizeFormatter.Format(disk.SizeBytes)} ({disk.SizeBytes:N0} 바이트)");
    Console.WriteLine($"     시리얼    : {disk.SerialNumber ?? "-"}");
    Console.WriteLine($"     버스      : {disk.BusType}");
    Console.WriteLine($"     섹터      : 논리 {disk.LogicalSectorSize} / 물리 {disk.PhysicalSectorSize}");
    Console.WriteLine($"     파티션형식: {disk.PartitionStyle}");

    foreach (var p in disk.Partitions)
    {
        string letter = p.DriveLetter is null ? "  " : $"{p.DriveLetter}:";
        string esp = p.IsEfiSystemPartition ? " [EFI]" : "";
        string active = p.IsActive ? " [활성]" : "";
        string free = p.FreeSpaceBytes is { } f ? $", 여유 {SizeFormatter.Format(f)}" : "";

        Console.WriteLine(
            $"       #{p.Number} {letter} {p.FileSystem ?? "-",-6} " +
            $"{SizeFormatter.Format(p.LengthBytes),10} @ {p.StartingOffset,14:N0}{free}{esp}{active}");
    }

    Console.WriteLine();
}

DiskMigrator.Probe.ReadPathCheck.Run(disks);

Console.WriteLine("=== SafetyGuard 판정 (모든 원본 → 대상 조합) ===\n");

foreach (var source in disks)
{
    foreach (var target in disks)
    {
        var report = SafetyGuard.Evaluate(source, target, service.IsElevated);

        string verdict = report.CanProceed
            ? report.NeedsTypedConfirmation ? "확인 필요" : "진행 가능"
            : "차단";

        Console.WriteLine($"[{source.DeviceNumber}] → [{target.DeviceNumber}] : {verdict}");

        foreach (var issue in report.Issues.Where(i => i.Severity >= SafetySeverity.RequiresConfirmation))
        {
            Console.WriteLine($"      {issue.Severity}: {issue.Code}");
        }
    }
}

return 0;

static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();
