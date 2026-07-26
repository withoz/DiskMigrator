using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Devices;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;
using DiskMigrator.Core.Safety;
using DiskMigrator.Windows.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>
/// 원본/대상 디스크로부터 실행 가능한 <see cref="CloneSession"/>을 조립합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CloneSessionFactory(
    IDiskService diskService,
    ISnapshotProvider snapshotProvider,
    ILogger<CloneSessionFactory>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<CloneSessionFactory>.Instance;

    /// <summary>
    /// 클론 세션을 만듭니다. 이 메서드는 대상 디스크의 볼륨을 잠그므로,
    /// 반드시 SafetyGuard 검사와 사용자 확인을 마친 뒤에 호출해야 합니다.
    /// </summary>
    /// <param name="source">읽을 디스크.</param>
    /// <param name="target">덮어쓸 디스크. 모든 데이터가 파괴됩니다.</param>
    /// <param name="useSnapshot">
    /// true면 원본의 NTFS 볼륨을 VSS로 스냅샷해서 읽습니다. 실행 중인 시스템 디스크에는 필수이고,
    /// 마운트된 데이터 디스크에도 권장합니다 — 복제 중 파일이 바뀌면 결과물이 깨집니다.
    /// </param>
    /// <summary>
    /// 대상에 아무것도 쓰지 않고, 원본에 대한 복사 계획만 만들어 봅니다.
    /// </summary>
    /// <remarks>
    /// 실행 중인 시스템 디스크를 클론하는 건 되돌릴 수 없는 작업인데, 정작 위험한 부분
    /// (VSS 스냅샷이 실제로 떠지는가, EFI·MSR·복구 파티션이 섞인 실제 레이아웃에서
    /// 구간 조립이 맞는가, 스냅샷 장치가 읽히는가)은 대상 없이도 전부 확인할 수 있습니다.
    /// 몇 시간짜리 클론을 시작하기 전에 몇 초 만에 검증하기 위한 경로입니다.
    ///
    /// 스냅샷을 만들었다가 지우므로 완전한 읽기 전용은 아니지만, 어떤 디스크에도 쓰지 않습니다.
    /// </remarks>
    public async Task<ClonePreview> PreviewAsync(
        DiskInfo source,
        bool useSnapshot,
        bool skipUnusedBlocks = false,
        ResizeLayout? resizeLayout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var resources = new List<IDisposable>();

        try
        {
            var sourceDevice = diskService.OpenRead(source);
            resources.Add(sourceDevice);

            var (regions, snapshots, unsnapshotted) =
                await BuildRegionsAsync(source, sourceDevice, useSnapshot, skipUnusedBlocks, resizeLayout, resources, ct);

            _logger.LogInformation(
                "계획 미리보기: 구간 {Count}개, 총 {Bytes:N0}바이트, 스냅샷 {Snapshot}",
                regions.Count, regions.Sum(r => r.Length), snapshots is null ? "미사용" : "사용");

            return new ClonePreview(resources)
            {
                Source = source,
                Regions = regions,
                SnapshotTimeUtc = snapshots?.CreatedUtc,
                UnsnapshottedPartitions = unsnapshotted,
            };
        }
        catch
        {
            DisposeAll(resources);
            throw;
        }
    }

    public async Task<CloneSession> CreateAsync(
        DiskInfo source,
        DiskInfo target,
        bool useSnapshot,
        bool skipUnusedBlocks = false,
        ResizeLayout? resizeLayout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // 쓰기 직전 마지막 관문: 사용자가 확인한 그 디스크가 지금도 같은 디스크인지 재확인합니다.
        // 사용자가 UI에서 확인하는 사이에 USB를 뽑았다 꽂으면 장치 번호가 재배정될 수 있습니다.
        await AssertTargetStillSafeAsync(source, target, useSnapshot, ct);

        var resources = new List<IDisposable>();

        try
        {
            var sourceDevice = diskService.OpenRead(source);
            resources.Add(sourceDevice);

            var (regions, snapshots, unsnapshotted) =
                await BuildRegionsAsync(source, sourceDevice, useSnapshot, skipUnusedBlocks, resizeLayout, resources, ct);

            // 대상을 여는 순간 볼륨이 잠기고 마운트 해제됩니다. 가장 마지막에 합니다.
            var targetDevice = diskService.OpenWriteExclusive(target);
            resources.Add(targetDevice);

            var plan = new ClonePlan
            {
                Target = targetDevice,
                Name = $"[{source.DeviceNumber}] {source.Model} → [{target.DeviceNumber}] {target.Model}",
                Regions = regions,
            };

            plan.Validate();

            _logger.LogInformation(
                "클론 세션 준비 완료: 구간 {Count}개, 총 {Bytes:N0}바이트, 스냅샷 {Snapshot}",
                regions.Count, plan.TotalBytes, snapshots is null ? "미사용" : "사용");

            return new CloneSession(resources)
            {
                Plan = plan,
                TargetDevice = targetDevice,
                SnapshotTimeUtc = snapshots?.CreatedUtc,
                UnsnapshottedPartitions = unsnapshotted,
            };
        }
        catch
        {
            DisposeAll(resources);
            throw;
        }
    }

    /// <summary>원본과 스냅샷 설정에 따라 복사 구간을 만듭니다. CreateAsync와 PreviewAsync가 공유합니다.</summary>
    private async Task<(List<CopyRegion> Regions, ISnapshotSet? Snapshots, List<string> Unsnapshotted)>
        BuildRegionsAsync(
            DiskInfo source,
            IBlockDevice sourceDevice,
            bool useSnapshot,
            bool skipUnusedBlocks,
            ResizeLayout? resizeLayout,
            List<IDisposable> resources,
            CancellationToken ct)
    {
        ISnapshotSet? snapshots = null;
        var unsnapshotted = new List<string>();

        if (useSnapshot)
        {
            snapshots = await TryCreateSnapshotsAsync(source, ct);
            if (snapshots is not null) resources.Add(snapshots);
        }

        List<CopyRegion> regions;
        if (resizeLayout is not null)
        {
            // 리사이즈: 파티션을 새 오프셋으로 복사하고 GPT는 클론 후 다시 씁니다(원시 복사 없음).
            regions = BuildResizeRegions(
                source, sourceDevice, snapshots, resizeLayout, skipUnusedBlocks, resources, unsnapshotted);
        }
        else
        {
            regions = snapshots is null
                ? BuildOfflineRegions(source, sourceDevice)
                : BuildSnapshotRegions(source, sourceDevice, snapshots, skipUnusedBlocks, resources, unsnapshotted);
        }

        return (regions, snapshots, unsnapshotted);
    }

    private static void DisposeAll(List<IDisposable> resources)
    {
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            try { resources[i].Dispose(); } catch { /* 정리 중 오류는 무시 */ }
        }
    }

    private async Task AssertTargetStillSafeAsync(
        DiskInfo source, DiskInfo target, bool useSnapshot, CancellationToken ct)
    {
        var current = await diskService.EnumerateDisksAsync(ct);

        var freshTarget = current.FirstOrDefault(d => d.DeviceNumber == target.DeviceNumber);
        SafetyGuard.AssertTargetUnchanged(target, freshTarget);

        var freshSource = current.FirstOrDefault(d => d.DeviceNumber == source.DeviceNumber);
        if (freshSource is null)
        {
            throw new SafetyViolationException(
                $"원본 디스크 [{source.DeviceNumber}] {source.Model} 이(가) 더 이상 존재하지 않습니다.");
        }

        // 규칙 전체를 다시 돌립니다. UI에서 통과했더라도 상황이 바뀌었을 수 있습니다.
        var report = SafetyGuard.Evaluate(freshSource, freshTarget!, diskService.IsElevated, useSnapshot);

        if (!report.CanProceed)
        {
            string reasons = string.Join("; ", report.Blockers.Select(b => b.Message));
            throw new SafetyViolationException(L.T(
                $"안전 검사에 실패해 작업을 시작하지 않습니다: {reasons}",
                $"Safety checks failed, so the operation was not started: {reasons}"));
        }
    }

    /// <summary>
    /// 원본 디스크의 NTFS 볼륨들을 하나의 스냅샷 세트로 만듭니다.
    /// </summary>
    private async Task<ISnapshotSet?> TryCreateSnapshotsAsync(DiskInfo source, CancellationToken ct)
    {
        if (!snapshotProvider.IsAvailable)
        {
            _logger.LogWarning("VSS를 사용할 수 없어 원시 읽기로 진행합니다.");
            return null;
        }

        // VSS는 NTFS/ReFS만 지원합니다. EFI 파티션(FAT32)은 스냅샷 대상이 아닙니다.
        var volumes = source.Partitions
            .Where(p => p.VolumeGuidPath is not null)
            .Where(p => p.FileSystem is not null &&
                        (p.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                         p.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.VolumeGuidPath!)
            .ToList();

        if (volumes.Count == 0)
        {
            _logger.LogInformation("원본에 스냅샷 가능한 볼륨이 없어 원시 읽기로 진행합니다.");
            return null;
        }

        try
        {
            return await snapshotProvider.CreateSnapshotSetAsync(volumes, ct);
        }
        catch (Exception ex)
        {
            // 스냅샷 실패는 치명적입니다. 조용히 원시 읽기로 넘어가면 사용자는
            // "일관된 클론을 받았다"고 믿지만 실제로는 깨진 디스크를 받게 됩니다.
            throw new InvalidOperationException(
                "VSS 스냅샷을 만들지 못했습니다. 실행 중인 디스크를 스냅샷 없이 복제하면 " +
                "결과물이 일관되지 않아 부팅되지 않을 수 있으므로 작업을 중단합니다. " +
                $"원인: {ex.Message}", ex);
        }
    }

    /// <summary>오프라인 클론: 디스크 전체를 하나의 구간으로 그대로 복사합니다.</summary>
    private static List<CopyRegion> BuildOfflineRegions(DiskInfo source, IBlockDevice sourceDevice) =>
    [
        new CopyRegion
        {
            Source = sourceDevice,
            SourceOffset = 0,
            TargetOffset = 0,
            Length = AlignDown(sourceDevice.Length, sourceDevice.SectorSize),
            Description = L.T($"디스크 전체 ({source.Model})", $"Whole disk ({source.Model})"),
        },
    ];

    /// <summary>
    /// 핫 클론: 파티션 데이터는 스냅샷에서, 나머지(파티션 테이블·간격·GPT 백업 헤더)는
    /// 원시 디스크에서 읽는 구간 목록을 만듭니다.
    /// </summary>
    /// <remarks>
    /// VSS는 <b>볼륨</b> 스냅샷이지 디스크 스냅샷이 아닙니다. 그래서 디스크 이미지를
    /// 조각으로 나눠, 각 조각을 가장 적절한 출처에서 읽어야 합니다:
    ///
    ///   [MBR/GPT 헤더][파티션1][간격][파티션2] ... [GPT 백업 헤더]
    ///    └ 원시        └ 스냅샷  └ 원시  └ 스냅샷      └ 원시
    ///
    /// 파티션 테이블은 클론 중에 바뀌지 않으므로 원시로 읽어도 안전합니다.
    /// </remarks>
    private List<CopyRegion> BuildSnapshotRegions(
        DiskInfo source,
        IBlockDevice sourceDevice,
        ISnapshotSet snapshots,
        bool skipUnusedBlocks,
        List<IDisposable> resources,
        List<string> unsnapshotted)
    {
        var regions = new List<CopyRegion>();
        int sectorSize = sourceDevice.SectorSize;
        long diskLength = AlignDown(sourceDevice.Length, sectorSize);
        long cursor = 0;

        foreach (var partition in source.Partitions.OrderBy(p => p.StartingOffset))
        {
            // 파티션 앞의 영역(첫 파티션 앞이면 MBR/GPT 헤더, 중간이면 정렬 간격).
            if (partition.StartingOffset > cursor)
            {
                regions.Add(RawRegion(sourceDevice, cursor, partition.StartingOffset - cursor,
                    cursor == 0
                        ? L.T("파티션 테이블 및 부트 영역", "Partition table and boot area")
                        : L.T($"파티션 {partition.Number} 앞 간격", $"Gap before partition {partition.Number}")));
            }

            long partitionEnd = Math.Min(partition.EndOffset, diskLength);
            long partitionLength = partitionEnd - partition.StartingOffset;

            if (partitionLength <= 0)
            {
                cursor = Math.Max(cursor, partitionEnd);
                continue;
            }

            IBlockDevice? snapshotDevice = TryOpenSnapshot(partition, snapshots, resources);

            if (snapshotDevice is null)
            {
                unsnapshotted.Add(Describe(partition));
                regions.Add(RawRegion(sourceDevice, partition.StartingOffset, partitionLength,
                    L.T($"{Describe(partition)} — 원시 복사", $"{Describe(partition)} — raw copy")));
            }
            else
            {
                // 스냅샷 장치가 보고하는 크기를 그대로 믿으면 안 됩니다. VSS 섀도 복사본은
                // 길이를 볼륨 전체 크기로 보고하면서도 마지막 몇 KB는 읽기가 0을 돌려줍니다
                // (실기에서 관측: 1,056,899,072바이트 볼륨의 마지막 4,096바이트가 읽히지 않음).
                // 그대로 계획에 넣으면 복제가 끝에서 실패합니다. 실제로 읽히는 경계를 찾습니다.
                long fromSnapshot = ReadableLengthProbe.Probe(snapshotDevice, partitionLength);

                if (fromSnapshot < partitionLength)
                {
                    _logger.LogInformation(
                        "{Region}: 스냅샷에서 읽을 수 있는 길이는 {Readable:N0}바이트로, " +
                        "파티션 길이 {Partition:N0}바이트보다 {Gap:N0}바이트 짧습니다. " +
                        "나머지는 원시 디스크에서 복사합니다.",
                        Describe(partition), fromSnapshot, partitionLength, partitionLength - fromSnapshot);
                }

                if (fromSnapshot > 0)
                {
                    AddSnapshotDataRegions(
                        regions, partition, snapshotDevice, snapshots, fromSnapshot, skipUnusedBlocks,
                        targetBaseOffset: partition.StartingOffset);
                }

                // 볼륨이 파티션보다 짧으면 남는 꼬리는 원시로 채웁니다.
                long tail = partitionLength - fromSnapshot;
                if (tail > 0)
                {
                    regions.Add(RawRegion(sourceDevice, partition.StartingOffset + fromSnapshot, tail,
                        $"{Describe(partition)} — 꼬리 영역"));
                }
            }

            cursor = partitionEnd;
        }

        // 마지막 파티션 뒤 영역. GPT 백업 헤더가 여기 있으므로 반드시 복사해야 합니다.
        if (cursor < diskLength)
        {
            regions.Add(RawRegion(sourceDevice, cursor, diskLength - cursor,
                "디스크 끝 영역 (GPT 백업 헤더 포함)"));
        }

        return regions;
    }

    /// <summary>
    /// 리사이즈 클론: 각 파티션을 <b>새 오프셋</b>으로 복사합니다.
    /// </summary>
    /// <remarks>
    /// 일반 클론과 두 가지가 다릅니다.
    /// <list type="number">
    /// <item>각 파티션 데이터의 <b>대상 오프셋</b>이 배치(<see cref="ResizeLayout"/>)가 정한 새 위치입니다.
    ///   파티션 <b>내용</b>은 1:1 그대로 복사하고(확대된 여분 공간은 비운 채로 두었다가 클론 후
    ///   NTFS를 확장), 스마트 클론·스냅샷 로직은 동일하게 적용합니다.</item>
    /// <item>파티션 테이블은 원본을 그대로 복사한 뒤(엔트리 GUID·타입·이름 보존), 클론이 끝나면
    ///   <see cref="GptRewriter"/>로 엔트리 위치를 새 배치에 맞게 다시 씁니다. 그래서 파티션 사이의
    ///   정렬 간격이나 원본 끝의 GPT 백업 헤더는 복사하지 않습니다(백업은 재작성이 만듭니다).</item>
    /// </list>
    /// </remarks>
    private List<CopyRegion> BuildResizeRegions(
        DiskInfo source,
        IBlockDevice sourceDevice,
        ISnapshotSet? snapshots,
        ResizeLayout layout,
        bool skipUnusedBlocks,
        List<IDisposable> resources,
        List<string> unsnapshotted)
    {
        var regions = new List<CopyRegion>();
        int sectorSize = sourceDevice.SectorSize;
        long diskLength = AlignDown(sourceDevice.Length, sectorSize);

        var ordered = source.Partitions.OrderBy(p => p.StartingOffset).ToList();
        long firstStart = ordered.Count > 0 ? ordered[0].StartingOffset : 0;

        // 파티션 테이블 + 첫 파티션 앞 영역(보호 MBR·GPT 헤더·엔트리 배열). 위치는 그대로.
        // 클론 후 GptRewriter가 엔트리를 새 배치로 고치므로 검증에서는 제외합니다.
        if (firstStart > 0)
        {
            regions.Add(Segment(sourceDevice, 0, 0, firstStart,
                L.T("파티션 테이블 및 부트 영역", "Partition table and boot area"), stableForVerification: false));
        }

        foreach (var partition in ordered)
        {
            var placed = layout.Partitions.FirstOrDefault(p => p.SourceNumber == partition.Number)
                ?? throw new InvalidOperationException(
                    $"파티션 {partition.Number}이(가) 리사이즈 배치에 없습니다.");
            long newStart = placed.StartingOffset;

            long partitionEnd = Math.Min(partition.EndOffset, diskLength);
            long partitionLength = partitionEnd - partition.StartingOffset;
            if (partitionLength <= 0) continue;

            IBlockDevice? snapshotDevice =
                snapshots is null ? null : TryOpenSnapshot(partition, snapshots, resources);

            if (snapshotDevice is null)
            {
                // 스냅샷 없음(오프라인이거나 VSS 불가 파일시스템) → 원본에서 통째로 새 오프셋에 복사.
                if (snapshots is not null) unsnapshotted.Add(Describe(partition));
                regions.Add(Segment(sourceDevice, partition.StartingOffset, newStart, partitionLength,
                    L.T($"{Describe(partition)} — 원시 복사", $"{Describe(partition)} — raw copy"),
                    stableForVerification: snapshots is null));
            }
            else
            {
                long fromSnapshot = ReadableLengthProbe.Probe(snapshotDevice, partitionLength);

                if (fromSnapshot > 0)
                {
                    // snapshotDevice가 non-null이면 snapshots도 non-null입니다(위 삼항의 else 분기).
                    AddSnapshotDataRegions(
                        regions, partition, snapshotDevice, snapshots!, fromSnapshot, skipUnusedBlocks,
                        targetBaseOffset: newStart);
                }

                long tail = partitionLength - fromSnapshot;
                if (tail > 0)
                {
                    regions.Add(Segment(sourceDevice, partition.StartingOffset + fromSnapshot,
                        newStart + fromSnapshot, tail,
                        $"{Describe(partition)} — 꼬리 영역", stableForVerification: false));
                }
            }
        }

        return regions;
    }

    /// <summary>임의의 원본→대상 오프셋으로 복사하는 구간을 만듭니다.</summary>
    private static CopyRegion Segment(
        IBlockDevice device, long sourceOffset, long targetOffset, long length,
        string description, bool stableForVerification) =>
        new()
        {
            Source = device,
            SourceOffset = sourceOffset,
            TargetOffset = targetOffset,
            Length = length,
            Description = description,
            IsStableForVerification = stableForVerification,
        };

    /// <summary>
    /// 스냅샷 파티션 데이터를 복사 구간으로 추가합니다. 스마트 클론이 켜지고 NTFS면 할당
    /// 비트맵을 읽어 <b>사용 중인 런만</b> 구간으로 만들고, 아니면 파티션 전체를 한 구간으로 만듭니다.
    /// </summary>
    /// <remarks>
    /// 비트맵 읽기가 실패하면(비-NTFS·FSCTL 미지원·오류) 통째 복사로 안전하게 되돌립니다.
    /// 스마트 클론은 어디까지나 최적화이므로, 실패해도 정확한 복제가 우선입니다.
    /// </remarks>
    private void AddSnapshotDataRegions(
        List<CopyRegion> regions,
        PartitionInfo partition,
        IBlockDevice snapshotDevice,
        ISnapshotSet snapshots,
        long fromSnapshot,
        bool skipUnusedBlocks,
        long targetBaseOffset)
    {
        bool isNtfs = partition.FileSystem is not null &&
                      partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

        if (skipUnusedBlocks && isNtfs &&
            partition.VolumeGuidPath is { } vol &&
            snapshots.SnapshotDevicePaths.TryGetValue(vol, out string? snapshotPath))
        {
            try
            {
                // 4KB 클러스터 기준 1MB 미만 빈틈은 이어 붙여 IO 조각화를 줄입니다.
                var reader = new VolumeBitmapReader(_logger);
                var runs = reader.ReadRuns(snapshotPath, coalesceGapBytes: 1L << 20, capBytes: fromSnapshot);

                if (runs.Count > 0)
                {
                    long used = runs.Sum(r => r.LengthBytes);
                    _logger.LogInformation(
                        "{Region}: 스마트 클론 — 사용 영역 {Runs}런 / {Used:N0}바이트만 복사 " +
                        "(파티션 {Full:N0}바이트 중).",
                        Describe(partition), runs.Count, used, fromSnapshot);

                    foreach (var run in runs)
                    {
                        regions.Add(new CopyRegion
                        {
                            Source = snapshotDevice,
                            SourceOffset = run.OffsetBytes,
                            TargetOffset = targetBaseOffset + run.OffsetBytes,
                            Length = run.LengthBytes,
                            Description = L.T($"{Describe(partition)} — 스냅샷(사용 블록)",
                                              $"{Describe(partition)} — snapshot (used blocks)"),
                        });
                    }
                    return;
                }

                _logger.LogWarning(
                    "{Region}: 할당 비트맵에서 사용 런을 찾지 못해 통째 복사로 되돌립니다.",
                    Describe(partition));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Region}: 할당 비트맵 읽기 실패 — 통째 복사로 안전하게 되돌립니다.",
                    Describe(partition));
            }
        }

        // 기본(통째 복사) 또는 스마트 클론 폴백.
        regions.Add(new CopyRegion
        {
            Source = snapshotDevice,
            SourceOffset = 0,
            TargetOffset = targetBaseOffset,
            Length = fromSnapshot,
            Description = L.T($"{Describe(partition)} — 스냅샷", $"{Describe(partition)} — snapshot"),
        });
    }

    /// <summary>
    /// 파티션의 스냅샷 장치를 엽니다. 스냅샷이 아예 없으면(VSS 미지원 파일 시스템) null을 돌려
    /// 호출자가 원시 복사로 처리하게 합니다.
    /// </summary>
    /// <remarks>
    /// 스냅샷이 <b>있는데 열지 못하는</b> 경우는 조용히 원시 복사로 넘어가면 안 됩니다.
    /// 그러면 사용자는 "스냅샷을 써서 일관된 사본을 받았다"고 믿지만 실제로는 살아 있는
    /// 볼륨을 그대로 읽은 결과물을 받습니다 — 시스템 디스크라면 부팅되지 않습니다.
    /// 조용한 성능 저하가 아니라 조용한 <b>정합성 파괴</b>이므로 작업을 중단합니다.
    /// </remarks>
    private IBlockDevice? TryOpenSnapshot(
        PartitionInfo partition, ISnapshotSet snapshots, List<IDisposable> resources)
    {
        if (partition.VolumeGuidPath is null) return null;
        if (!snapshots.SnapshotDevicePaths.ContainsKey(partition.VolumeGuidPath)) return null;

        try
        {
            var device = snapshots.OpenSnapshotRead(partition.VolumeGuidPath);
            resources.Add(device);
            return device;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"파티션 {partition.Number}" +
                $"{(partition.DriveLetter is null ? "" : $" ({partition.DriveLetter}:)")} 의 " +
                $"VSS 스냅샷은 만들어졌지만 읽기 위해 열지 못했습니다. " +
                "스냅샷 없이 원시로 읽으면 복제 중 파일이 바뀌어 일관되지 않은 사본이 되므로 " +
                $"작업을 중단합니다. 원인: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 라이브 디스크에서 원시로 읽는 구간을 만듭니다.
    /// </summary>
    /// <remarks>
    /// 핫 클론에서 이 헬퍼가 만드는 구간(EFI·MSR·파티션 꼬리·GPT 백업 헤더)은 모두
    /// 실행 중인 라이브 디스크에서 읽습니다. 복제~검증 사이에 소스가 바뀔 수 있고,
    /// GPT 백업 헤더는 클론 후 우리가 대상에서 위치를 옮겨 다시 쓰기까지 하므로,
    /// 소스와 재비교하는 검증은 의미가 없습니다. 그래서 검증에서 제외합니다.
    /// </remarks>
    private static CopyRegion RawRegion(IBlockDevice device, long offset, long length, string description) =>
        new()
        {
            Source = device,
            SourceOffset = offset,
            TargetOffset = offset,
            Length = length,
            Description = description,
            IsStableForVerification = false,
        };

    private static string Describe(PartitionInfo partition)
    {
        string letter = partition.DriveLetter is null ? "" : $" ({partition.DriveLetter}:)";
        string fs = partition.IsEfiSystemPartition ? " [EFI]" : "";
        return L.T($"파티션 {partition.Number}{letter}{fs}", $"Partition {partition.Number}{letter}{fs}");
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
}
