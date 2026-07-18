using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Jobs;

namespace DiskMigrator.Cli;

internal static class Report
{
    public static void PrintDisk(string label, DiskInfo disk)
    {
        var flags = new List<string>();
        if (disk.IsSystemDisk) flags.Add("시스템");
        if (disk.IsBootDisk) flags.Add("부팅");
        if (disk.HasPageFile) flags.Add("페이지파일");
        if (disk.IsRemovable) flags.Add("착탈식");
        if (disk.IsReadOnly) flags.Add("읽기전용");

        Console.WriteLine($"{label}: [{disk.DeviceNumber}] {disk.Model}" +
                          (flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : ""));
        Console.WriteLine($"  {SizeFormatter.Format(disk.SizeBytes)} ({disk.SizeBytes:N0} 바이트)   " +
                          $"{disk.BusType}   섹터 {disk.LogicalSectorSize}B   {disk.PartitionStyle}   " +
                          $"S/N {disk.SerialNumber ?? "-"}");

        foreach (var p in disk.Partitions)
        {
            Console.WriteLine(
                $"    #{p.Number} {(p.DriveLetter ?? "-"),-2} {p.FileSystem ?? "RAW",-6} " +
                $"{SizeFormatter.Format(p.LengthBytes),10} @ {p.StartingOffset,15:N0}" +
                $"{(p.IsEfiSystemPartition ? "  [EFI]" : "")}");
        }

        if (disk.Partitions.Count == 0) Console.WriteLine("    (파티션 없음)");

        Console.WriteLine();
    }

    private static string _lastPhase = "";

    public static void PrintProgress(CloneProgress p)
    {
        if (p.Phase != _lastPhase)
        {
            _lastPhase = p.Phase;
            Console.WriteLine($"\n--- {p.Phase} 단계 ---");
        }

        string eta = p.Eta is { } e ? SizeFormatter.FormatDuration(e) : "계산 중";

        Console.WriteLine(
            $"  {DateTime.Now:HH:mm:ss}  {p.Percent,5:F1}%  " +
            $"{SizeFormatter.Format(p.BytesProcessed),10} / {SizeFormatter.Format(p.TotalBytes),-10} " +
            $"{SizeFormatter.FormatSpeed(p.SpeedBytesPerSecond),12}  " +
            $"남은 {eta,-12} 경과 {SizeFormatter.FormatDuration(p.Elapsed),-12} {p.CurrentRegion}");
    }

    public static void PrintResult(CloneJobReport report)
    {
        var r = report.Result;

        Console.WriteLine("=== 결과 ===\n");
        Console.WriteLine($"  상태        : {r.Outcome}");
        Console.WriteLine($"  복사        : {SizeFormatter.Format(r.BytesCopied)} ({r.BytesCopied:N0} 바이트)");
        Console.WriteLine($"  소요 시간   : {SizeFormatter.FormatDuration(r.Duration)}");
        Console.WriteLine($"  평균 속도   : {SizeFormatter.FormatSpeed(r.AverageSpeedBytesPerSecond)}");
        Console.WriteLine($"  검증        : {r.VerificationPassed switch { true => "통과", false => "실패", null => "수행 안 함" }}");
        Console.WriteLine($"  불량 섹터   : {r.BadSectors.Count}건");

        if (report.SnapshotTimeUtc is { } t)
        {
            Console.WriteLine($"  스냅샷 시점 : {t.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                              "(이 시점 이후 변경분은 복제되지 않음)");
        }

        if (report.UnsnapshottedPartitions.Count > 0)
        {
            Console.WriteLine($"  원시 복사   : {string.Join(", ", report.UnsnapshottedPartitions)}");
        }

        if (report.GptRepair is { } gpt)
        {
            Console.WriteLine($"  GPT 보정    : {gpt.Description}");
        }

        if (report.UniversalRestore is { } ur)
        {
            Console.WriteLine($"  새 하드웨어  : {ur.Message}");
        }

        if (r.VerificationMismatches.Count > 0)
        {
            Console.WriteLine($"\n  불일치 구간 {r.VerificationMismatches.Count}개:");
            foreach (var (offset, length) in r.VerificationMismatches.Take(20))
            {
                Console.WriteLine($"    오프셋 {offset:N0} 길이 {length:N0}");
            }
        }

        if (r.BadSectors.Count > 0)
        {
            Console.WriteLine($"\n  불량 섹터 {r.BadSectors.Count}개 (앞 20개):");
            foreach (var bad in r.BadSectors.Take(20))
            {
                Console.WriteLine($"    오프셋 {bad.Offset:N0} — {bad.Region}");
            }
        }

        if (r.ErrorMessage is { } err) Console.WriteLine($"\n  메시지: {err}");

        Console.WriteLine();
        Console.WriteLine(r.Outcome switch
        {
            CloneOutcome.Completed => "*** 클론 성공 ***",
            CloneOutcome.CompletedWithBadSectors => "*** 클론 완료 (불량 섹터 있음) ***",
            CloneOutcome.Cancelled => "*** 취소됨 — 대상 디스크는 불완전합니다 ***",
            _ => "*** 실패 ***",
        });
    }
}
