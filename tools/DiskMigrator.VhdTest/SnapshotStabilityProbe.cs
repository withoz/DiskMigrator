using System.Diagnostics;
using System.Security.Cryptography;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Snapshots;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.VhdTest;

/// <summary>
/// VSS 스냅샷이 시간이 지나면서 값이 바뀌는지 측정합니다. 디스크에 쓰지 않습니다.
/// </summary>
/// <remarks>
/// 실기에서 스냅샷을 제대로 썼는데도 검증 불일치 230건이 났습니다. 원인 후보:
/// (1) VSS가 스냅샷 생성 직후 FilesNotToSnapshot(pagefile.sys/hiberfil.sys)을 비동기로
///     지워서, 복제 시점과 검증 시점의 스냅샷 값이 달라짐 → 양성.
/// (2) 긴 클론 동안 섀도 저장소가 넘쳐 스냅샷이 붕괴 → 진짜 문제.
///
/// 이 도구는 스냅샷을 뜬 직후 블록별 해시를 찍고, 잠깐 기다렸다가 다시 찍어
/// "우리가 아무것도 안 썼는데 바뀐 블록"을 찾습니다. 바뀐 블록이 한 곳(수 GB)에
/// 몰려 있으면 pagefile류(양성)이고, 넓게 퍼져 있으면 붕괴입니다.
/// </remarks>
internal static class SnapshotStabilityProbe
{
    private const int BlockSize = 16 * 1024 * 1024; // 16MB 블록 단위로 해시

    public static async Task<int> RunAsync(
        WindowsDiskService diskService,
        VssSnapshotProvider snapshotProvider,
        ILogger logger,
        int diskNumber,
        int waitSeconds)
    {
        var disks = await diskService.EnumerateDisksAsync();
        var disk = disks.FirstOrDefault(d => d.DeviceNumber == diskNumber);
        if (disk is null) { Console.Error.WriteLine($"디스크 {diskNumber}를 찾을 수 없습니다."); return 4; }

        var volumes = disk.Partitions
            .Where(p => p.VolumeGuidPath is not null &&
                        (string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (volumes.Count == 0) { Console.Error.WriteLine("스냅샷 가능한 NTFS 볼륨이 없습니다."); return 4; }

        Console.WriteLine("=== VSS 스냅샷 안정성 측정 (쓰기 없음) ===\n");
        Console.WriteLine($"디스크 [{disk.DeviceNumber}] {disk.Model}");
        foreach (var v in volumes)
            Console.WriteLine($"  볼륨: {v.DriveLetter ?? "-"} {v.FileSystem} {SizeFormatter.Format(v.LengthBytes)}");
        Console.WriteLine($"대기 시간: {waitSeconds}초\n");

        if (!snapshotProvider.IsAvailable) { Console.Error.WriteLine("VSS를 사용할 수 없습니다."); return 3; }

        using var snapshots = await snapshotProvider.CreateSnapshotSetAsync(
            volumes.Select(v => v.VolumeGuidPath!).ToList());

        Console.WriteLine($"스냅샷 생성됨: {snapshots.CreatedUtc.ToLocalTime():HH:mm:ss}\n");

        int totalChangedBlocks = 0;

        foreach (var v in volumes)
        {
            if (!snapshots.SnapshotDevicePaths.ContainsKey(v.VolumeGuidPath!)) continue;

            Console.WriteLine($"--- 볼륨 {v.DriveLetter ?? "-"} ({SizeFormatter.Format(v.LengthBytes)}) ---");

            using var device = snapshots.OpenSnapshotRead(v.VolumeGuidPath!);

            Console.WriteLine($"  1차 해시 중... ({SizeFormatter.Format(device.Length)})");
            var sw = Stopwatch.StartNew();
            var first = HashBlocks(device);
            Console.WriteLine($"  1차 완료: {first.Count}블록, {sw.Elapsed.TotalSeconds:F0}초");

            Console.WriteLine($"  {waitSeconds}초 대기 (스냅샷이 이 사이에 바뀌는지 확인)...");
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

            Console.WriteLine("  2차 해시 중...");
            var second = HashBlocks(device);

            // 바뀐 블록 찾기
            var changed = new List<long>();
            foreach (var (offset, hash) in first)
            {
                if (second.TryGetValue(offset, out var h2) && !h2.AsSpan().SequenceEqual(hash))
                    changed.Add(offset);
            }
            changed.Sort();

            totalChangedBlocks += changed.Count;

            Console.WriteLine($"  결과: 전체 {first.Count}블록 중 {changed.Count}블록이 바뀜 " +
                              $"({SizeFormatter.Format((long)changed.Count * BlockSize)})");

            if (changed.Count > 0)
            {
                // 연속 구간으로 묶어서 몰려 있는지 본다
                var ranges = MergeRanges(changed, BlockSize);
                Console.WriteLine($"  바뀐 영역 {ranges.Count}개 구간:");
                foreach (var (start, end) in ranges.Take(20))
                {
                    Console.WriteLine($"    볼륨 오프셋 {start:N0} .. {end:N0} " +
                                      $"({SizeFormatter.Format(end - start)})");
                }
                Console.WriteLine(ranges.Count == 1
                    ? "  → 한 곳에 몰림: pagefile.sys/hiberfil.sys 등 양성일 가능성 높음"
                    : ranges.Count <= 5
                        ? "  → 소수 구간에 몰림: 특수 파일(양성) 가능성"
                        : "  → 넓게 퍼짐: 스냅샷 붕괴(diff 영역 부족) 의심");
            }
            else
            {
                Console.WriteLine("  → 변화 없음: 이 대기 시간 동안 스냅샷이 안정적");
            }
            Console.WriteLine();
        }

        Console.WriteLine("=== 종합 ===");
        Console.WriteLine(totalChangedBlocks == 0
            ? $"{waitSeconds}초 동안 스냅샷이 완전히 안정적이었습니다. " +
              "실기의 230건 불일치는 더 긴 시간(40분) 동안의 변화이거나 다른 원인일 수 있습니다."
            : $"스냅샷이 시간에 따라 바뀌었습니다 (총 {totalChangedBlocks}블록). " +
              "이것이 검증 불일치의 원인입니다 — 위 위치가 pagefile류면 양성입니다.");

        return 0;
    }

    private static Dictionary<long, byte[]> HashBlocks(IBlockDevice device)
    {
        var result = new Dictionary<long, byte[]>();
        using var buffer = new AlignedBuffer(BlockSize);
        long length = device.Length - (device.Length % device.SectorSize);

        for (long offset = 0; offset + device.SectorSize <= length; offset += BlockSize)
        {
            int toRead = (int)Math.Min(BlockSize, length - offset);
            toRead -= toRead % device.SectorSize;
            if (toRead == 0) break;

            var span = buffer.SpanOf(toRead);
            try
            {
                int read = device.Read(offset, span);
                if (read <= 0) continue;
                result[offset] = SHA256.HashData(span[..read]);
            }
            catch (IOException)
            {
                // 읽기 실패한 블록은 건너뜁니다.
            }
        }

        return result;
    }

    private static List<(long Start, long End)> MergeRanges(List<long> offsets, int blockSize)
    {
        var ranges = new List<(long, long)>();
        foreach (long o in offsets)
        {
            if (ranges.Count > 0 && ranges[^1].Item2 == o)
                ranges[^1] = (ranges[^1].Item1, o + blockSize);
            else
                ranges.Add((o, o + blockSize));
        }
        return ranges;
    }
}
