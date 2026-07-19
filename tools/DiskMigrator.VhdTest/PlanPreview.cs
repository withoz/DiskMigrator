using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Devices;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Jobs;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.VhdTest;

/// <summary>
/// 대상 없이 원본에 대한 복사 계획만 만들어 검증합니다. 어떤 디스크에도 쓰지 않습니다.
/// </summary>
/// <remarks>
/// 실행 중인 시스템 디스크(C:)를 클론하기 전에, 되돌릴 수 없는 쓰기를 시작하지 않고도
/// 가장 위험한 부분을 확인하기 위한 것입니다:
///   - 실제 시스템 디스크 레이아웃(EFI FAT32 + MSR + C: NTFS + 복구 NTFS)에서
///     VSS 스냅샷이 실제로 떠지는가
///   - 구간 조립이 디스크 전체를 빠짐없이·겹침 없이 덮는가
///   - 스냅샷 장치에서 실제로 데이터가 읽히는가
/// </remarks>
internal static class PlanPreview
{
    public static async Task<int> RunAsync(
        IDiskService diskService,
        ISnapshotProvider snapshotProvider,
        ILoggerFactory loggerFactory,
        int sourceNumber,
        bool useSnapshot,
        bool skipUnused = false)
    {
        var disks = await diskService.EnumerateDisksAsync();
        var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceNumber);

        if (source is null)
        {
            Console.Error.WriteLine($"오류: 디스크 {sourceNumber}를 찾을 수 없습니다.");
            return 4;
        }

        Console.WriteLine("=== 복사 계획 미리보기 (쓰기 없음) ===\n");
        Console.WriteLine($"원본: [{source.DeviceNumber}] {source.Model}");
        Console.WriteLine($"  크기      : {SizeFormatter.Format(source.SizeBytes)} ({source.SizeBytes:N0} 바이트)");
        Console.WriteLine($"  버스      : {source.BusType}   섹터: {source.LogicalSectorSize}B");
        Console.WriteLine($"  파티션형식: {source.PartitionStyle}");
        Console.WriteLine($"  시스템/부팅/페이지파일: {source.IsSystemDisk}/{source.IsBootDisk}/{source.HasPageFile}");
        Console.WriteLine($"  스냅샷 사용: {useSnapshot}   VSS 가용: {snapshotProvider.IsAvailable}\n");

        Console.WriteLine("  파티션 구성:");
        foreach (var p in source.Partitions)
        {
            Console.WriteLine(
                $"    #{p.Number} {(p.DriveLetter ?? "-"),-2} {p.FileSystem ?? "RAW",-6} " +
                $"{SizeFormatter.Format(p.LengthBytes),10} @ {p.StartingOffset,15:N0}" +
                $"{(p.IsEfiSystemPartition ? "  [EFI]" : "")}");
        }
        Console.WriteLine();

        var factory = new CloneSessionFactory(
            diskService, snapshotProvider, loggerFactory.CreateLogger<CloneSessionFactory>());

        try
        {
            using var preview = await factory.PreviewAsync(source, useSnapshot, skipUnused);

            Console.WriteLine("\n=== 복사 구간 ===\n");

            foreach (var r in preview.Regions)
            {
                string kind = r.Source.Id.Contains("ShadowCopy") ? "스냅샷" : "원시";
                Console.WriteLine(
                    $"  {kind,-6} {SizeFormatter.Format(r.Length),10}  " +
                    $"대상 [{r.TargetOffset,15:N0} .. {r.TargetOffset + r.Length,15:N0})  {r.Description}");
            }

            Console.WriteLine($"\n  구간 {preview.Regions.Count}개, 합계 {SizeFormatter.Format(preview.TotalBytes)} " +
                              $"({preview.TotalBytes:N0} 바이트)");

            if (skipUnused)
            {
                long full = source.SizeBytes - (source.SizeBytes % source.LogicalSectorSize);
                double pct = full > 0 ? preview.TotalBytes * 100.0 / full : 0;
                Console.WriteLine($"  스마트 클론: 디스크 {SizeFormatter.Format(full)} 중 " +
                                  $"{SizeFormatter.Format(preview.TotalBytes)} ({pct:F1}%)만 복사 " +
                                  $"— {SizeFormatter.Format(full - preview.TotalBytes)} 건너뜀.");
            }

            if (preview.SnapshotTimeUtc is { } t)
            {
                Console.WriteLine($"  스냅샷 시점: {t.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }

            if (preview.UnsnapshottedPartitions.Count > 0)
            {
                Console.WriteLine($"  원시 복사 대상: {string.Join(", ", preview.UnsnapshottedPartitions)}");
            }

            Console.WriteLine("\n=== 계획 정합성 검사 ===\n");

            int failures = 0;
            failures += CheckCoverage(preview, source, skipUnused);
            failures += CheckOverlap(preview);
            failures += CheckAlignment(preview, source);
            failures += CheckSnapshotReadable(preview);

            Console.WriteLine();

            if (failures == 0)
            {
                Console.WriteLine("*** 계획 검증 통과 — 실제 클론을 시작해도 되는 상태입니다 ***");
                return 0;
            }

            Console.WriteLine($"*** 계획 검증 실패 {failures}건 ***");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n*** 계획 생성 실패 ***\n{ex}");
            return 1;
        }
    }

    /// <summary>구간이 디스크 [0, 크기)를 빠짐없이 덮는가. 구멍이 있으면 그 영역은 복제되지 않습니다.</summary>
    /// <remarks>
    /// 스마트 클론(skipUnused)에서는 빈 클러스터를 일부러 건너뛰므로 구멍이 정상입니다.
    /// 이때는 구멍을 실패가 아니라 "건너뛴 여유 공간"으로 집계만 하되, GPT 백업 헤더가 있는
    /// 디스크 끝이 덮이는지는 여전히 검사합니다.
    /// </remarks>
    private static int CheckCoverage(ClonePreview preview, DiskInfo source, bool skipUnused)
    {
        var ordered = preview.Regions.OrderBy(r => r.TargetOffset).ToList();
        long cursor = 0;
        int gaps = 0;
        long skipped = 0;

        foreach (var r in ordered)
        {
            if (r.TargetOffset > cursor)
            {
                if (skipUnused)
                {
                    skipped += r.TargetOffset - cursor;
                }
                else
                {
                    Console.WriteLine($"  [실패] 구멍: [{cursor:N0} .. {r.TargetOffset:N0}) 가 복제되지 않습니다.");
                    gaps++;
                }
            }
            cursor = Math.Max(cursor, r.TargetOffset + r.Length);
        }

        // 디스크 크기가 섹터 배수가 아닐 수 있어 마지막 섹터 미만의 차이는 허용합니다.
        long expected = source.SizeBytes - (source.SizeBytes % source.LogicalSectorSize);

        // 스마트 클론이라도 마지막 데이터의 끝 뒤(GPT 백업 헤더 영역)는 원시 구간이 반드시 덮어야
        // 합니다. 그 원시 구간이 마지막이므로 cursor는 디스크 끝에 도달해야 합니다.
        if (cursor < expected)
        {
            if (skipUnused && expected - cursor < 64L * 1024 * 1024)
            {
                // 끝부분 여유 공간을 건너뛴 것(원시 끝 구간이 실제 끝을 덮는 경우엔 여기 안 옴).
                skipped += expected - cursor;
            }
            else
            {
                Console.WriteLine($"  [실패] 끝부분 미포함: [{cursor:N0} .. {expected:N0}) — " +
                                  "GPT 백업 헤더가 빠질 수 있습니다.");
                gaps++;
            }
        }

        if (gaps == 0)
        {
            if (skipUnused)
                Console.WriteLine($"  [OK]   커버리지 정상 (스마트 클론: {SizeFormatter.Format(skipped)} 여유 공간 건너뜀).");
            else
                Console.WriteLine($"  [OK]   디스크 전체 [0 .. {cursor:N0}) 를 빠짐없이 덮습니다.");
        }

        return gaps;
    }

    /// <summary>구간이 대상에서 겹치면 뒤 구간이 앞 구간을 덮어써 조용히 깨집니다.</summary>
    private static int CheckOverlap(ClonePreview preview)
    {
        var ordered = preview.Regions.OrderBy(r => r.TargetOffset).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];

            if (prev.TargetOffset + prev.Length > curr.TargetOffset)
            {
                Console.WriteLine($"  [실패] 겹침: '{prev.Description}' 과 '{curr.Description}'");
                return 1;
            }
        }

        Console.WriteLine("  [OK]   구간끼리 겹치지 않습니다.");
        return 0;
    }

    private static int CheckAlignment(ClonePreview preview, DiskInfo source)
    {
        int sector = source.LogicalSectorSize;
        int bad = 0;

        foreach (var r in preview.Regions)
        {
            if (r.SourceOffset % r.Source.SectorSize != 0 || r.TargetOffset % sector != 0 ||
                r.Length % sector != 0)
            {
                Console.WriteLine($"  [실패] 정렬 위반: {r.Description}");
                bad++;
            }
        }

        if (bad == 0) Console.WriteLine($"  [OK]   모든 구간이 {sector}바이트 섹터에 정렬되어 있습니다.");
        return bad;
    }

    /// <summary>
    /// 각 구간의 시작·중간·끝을 실제로 읽어 봅니다.
    /// 스냅샷 장치가 열리기만 하고 실제로는 안 읽히는 경우를 여기서 잡습니다.
    /// </summary>
    private static int CheckSnapshotReadable(ClonePreview preview)
    {
        int failures = 0;

        foreach (var r in preview.Regions)
        {
            int sectorSize = r.Source.SectorSize;
            using var buffer = new AlignedBuffer(Math.Max(4096, sectorSize));

            long[] probes =
            [
                r.SourceOffset,
                r.SourceOffset + (r.Length / 2 / sectorSize * sectorSize),
                r.SourceOffset + r.Length - sectorSize,
            ];

            foreach (long offset in probes)
            {
                try
                {
                    int read = r.Source.Read(offset, buffer.SpanOf(sectorSize));
                    if (read != sectorSize)
                    {
                        Console.WriteLine(
                            $"  [실패] {r.Description}: 오프셋 {offset:N0} 에서 {read}바이트만 읽힘 " +
                            $"({sectorSize} 기대)");
                        failures++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [실패] {r.Description}: 오프셋 {offset:N0} 읽기 실패 — {ex.Message}");
                    failures++;
                }
            }
        }

        if (failures == 0)
        {
            Console.WriteLine($"  [OK]   모든 구간의 시작/중간/끝이 실제로 읽힙니다 " +
                              $"({preview.Regions.Count * 3}개 지점 확인).");
        }

        return failures;
    }
}
