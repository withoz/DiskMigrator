using System.Diagnostics;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Util;

namespace DiskMigrator.Core.Engine;

/// <summary>검증 결과.</summary>
/// <param name="Passed">불일치가 하나도 없으면 true.</param>
/// <param name="Mismatches">원본과 대상이 다른 (오프셋, 길이) 목록. 대상 디스크 기준.</param>
/// <param name="BytesVerified">실제로 비교한 바이트 수.</param>
/// <param name="BytesSkipped">원본을 읽을 수 없어 비교하지 못한 바이트 수.</param>
public sealed record VerificationOutcome(
    bool Passed,
    IReadOnlyList<(long Offset, long Length)> Mismatches,
    long BytesVerified,
    long BytesSkipped);

/// <summary>
/// 복제가 끝난 뒤 원본과 대상을 실제로 다시 읽어 비교합니다.
/// </summary>
/// <remarks>
/// 계획서는 블록 해시(SHA-256/CRC) 비교를 제안했지만, 원본과 대상이 모두 같은 PC에
/// 붙어 있는 이 상황에서는 바이트 직접 비교가 더 낫습니다. I/O 비용은 동일한데
/// 해시 충돌 가능성이 아예 없고 CPU도 덜 씁니다. 해시는 서로 다른 시점·기계 간
/// 비교가 필요할 때(이미지 파일 백업, Phase 6) 의미가 있습니다.
/// </remarks>
public static class Verifier
{
    /// <summary>
    /// 대상을 다시 읽어, 복제 때 기록해 둔 해시와 비교합니다.
    /// </summary>
    /// <remarks>
    /// 원본(스냅샷)을 다시 읽지 않습니다. 클라이언트 Windows에서 VSS 섀도 저장소가 스냅샷
    /// 대상 볼륨 자신에 놓이면서 시간에 따라 스냅샷 값이 바뀌는(무해한) 드리프트가 생기는데,
    /// 원본 재읽기 방식은 이를 가짜 불일치로 잡습니다. 대신 "우리가 실제로 쓴 데이터"의 해시와
    /// 대상을 비교하면, 매체 불량이나 쓰기 오류만 정확히 잡고 드리프트엔 영향받지 않습니다.
    /// </remarks>
    internal static VerificationOutcome VerifyAgainstHashes(
        ClonePlan plan,
        CloneOptions options,
        CopyHashLog hashLog,
        IProgress<CloneProgress>? progress,
        PauseController? pause,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        using var buffer = new AlignedBuffer(options.BufferSize);

        var reporter = new ProgressReporter(progress, options.ProgressInterval, stopwatch, plan.TotalBytes);
        var mismatches = new List<(long, long)>();
        long verified = 0;
        long processed = 0;

        foreach (var block in hashLog.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            pause?.WaitIfPaused(ct);

            var span = buffer.SpanOf(block.Length);
            int read = CloneEngine.ReadWithRetry(plan.Target, block.TargetOffset, span, options, ct);

            if (read != block.Length || !CopyHashLog.Matches(block, span[..read]))
            {
                // 블록 단위로 불일치. 인접 블록은 하나의 구간으로 합쳐 리포트를 읽기 쉽게 합니다.
                if (mismatches.Count > 0)
                {
                    var (lastOffset, lastLength) = mismatches[^1];
                    if (lastOffset + lastLength == block.TargetOffset)
                    {
                        mismatches[^1] = (lastOffset, lastLength + block.Length);
                    }
                    else
                    {
                        mismatches.Add((block.TargetOffset, block.Length));
                    }
                }
                else
                {
                    mismatches.Add((block.TargetOffset, block.Length));
                }
            }
            else
            {
                verified += block.Length;
            }

            processed += block.Length;
            reporter.Report(L.T("검증", "Verifying"), L.T("대상 무결성 확인", "Checking target integrity"),
                processed, block.TargetOffset + block.Length, 0);
        }

        reporter.ReportFinal(L.T("검증", "Verifying"), L.T("대상 무결성 확인", "Checking target integrity"),
            processed, plan.Target.Length, 0);

        return new VerificationOutcome(mismatches.Count == 0, mismatches, verified, 0);
    }

    internal static VerificationOutcome Verify(
        ClonePlan plan,
        CloneOptions options,
        IProgress<CloneProgress>? progress,
        PauseController? pause,
        IReadOnlyList<BadSectorRecord> badSectors,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        using var sourceBuffer = new AlignedBuffer(options.BufferSize);
        using var targetBuffer = new AlignedBuffer(options.BufferSize);

        // 복제 중 0으로 채운 섹터는 원본을 여전히 읽을 수 없으므로 비교 대상에서 뺍니다.
        var zeroFilledOffsets = badSectors.Select(b => b.Offset).ToHashSet();

        var reporter = new ProgressReporter(progress, options.ProgressInterval, stopwatch, plan.TotalBytes);
        var mismatches = new List<(long, long)>();
        long verified = 0;
        long skipped = 0;
        long processed = 0;

        foreach (var region in plan.Regions)
        {
            // 라이브 디스크에서 원시로 읽는 구간(EFI 등)은 복제~검증 사이에 소스가 바뀌므로
            // 다시 읽어 비교하면 가짜 불일치가 납니다. 검증에서 제외합니다.
            if (!region.IsStableForVerification)
            {
                skipped += region.Length;
                processed += region.Length;
                reporter.Report(L.T("검증", "Verifying"),
                    L.T($"{region.Description} (검증 제외 — 라이브 소스)",
                        $"{region.Description} (skipped — live source)"),
                    processed, region.TargetOffset + region.Length, badSectors.Count);
                continue;
            }

            long position = 0;

            while (position < region.Length)
            {
                ct.ThrowIfCancellationRequested();
                pause?.WaitIfPaused(ct);

                int chunk = (int)Math.Min(options.BufferSize, region.Length - position);
                long sourceOffset = region.SourceOffset + position;
                long targetOffset = region.TargetOffset + position;

                var sourceSpan = sourceBuffer.SpanOf(chunk);
                var targetSpan = targetBuffer.SpanOf(chunk);

                int sourceRead;
                try
                {
                    sourceRead = CloneEngine.ReadWithRetry(region.Source, sourceOffset, sourceSpan, options, ct);
                }
                catch (IOException)
                {
                    // 원본이 읽히지 않는 구간은 검증할 방법이 없습니다. 실패가 아니라 "검증 불가"입니다.
                    skipped += chunk;
                    position += chunk;
                    processed += chunk;
                    reporter.Report(L.T("검증", "Verifying"), region.Description, processed, targetOffset, badSectors.Count);
                    continue;
                }

                int targetRead = CloneEngine.ReadWithRetry(plan.Target, targetOffset, targetSpan, options, ct);

                int comparable = Math.Min(sourceRead, targetRead);

                if (!sourceSpan[..comparable].SequenceEqual(targetSpan[..comparable]))
                {
                    CompareSectorBySector(
                        region, sourceOffset, targetOffset,
                        sourceSpan[..comparable], targetSpan[..comparable],
                        zeroFilledOffsets, mismatches, ref verified, ref skipped);
                }
                else
                {
                    verified += comparable;
                }

                position += comparable;
                processed += comparable;

                reporter.Report(L.T("검증", "Verifying"), region.Description, processed, targetOffset, badSectors.Count);
            }
        }

        reporter.ReportFinal(L.T("검증", "Verifying"), plan.Regions[^1].Description, processed, plan.Target.Length, badSectors.Count);

        return new VerificationOutcome(mismatches.Count == 0, mismatches, verified, skipped);
    }

    /// <summary>
    /// 블록 비교가 실패했을 때 섹터 단위로 좁혀서, 진짜 불일치한 섹터만 골라냅니다.
    /// 일부러 0으로 채운 불량 섹터는 불일치로 세지 않습니다.
    /// </summary>
    private static void CompareSectorBySector(
        CopyRegion region,
        long sourceBlockOffset,
        long targetBlockOffset,
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> target,
        HashSet<long> zeroFilledOffsets,
        List<(long, long)> mismatches,
        ref long verified,
        ref long skipped)
    {
        int sectorSize = region.Source.SectorSize;
        int sectorCount = source.Length / sectorSize;

        for (int i = 0; i < sectorCount; i++)
        {
            int start = i * sectorSize;
            long sourceSectorOffset = sourceBlockOffset + start;
            long targetSectorOffset = targetBlockOffset + start;

            if (zeroFilledOffsets.Contains(sourceSectorOffset))
            {
                skipped += sectorSize;
                continue;
            }

            if (source.Slice(start, sectorSize).SequenceEqual(target.Slice(start, sectorSize)))
            {
                verified += sectorSize;
                continue;
            }

            // 인접한 불일치 섹터는 하나의 구간으로 합쳐 리포트를 읽기 쉽게 만듭니다.
            if (mismatches.Count > 0)
            {
                var (lastOffset, lastLength) = mismatches[^1];
                if (lastOffset + lastLength == targetSectorOffset)
                {
                    mismatches[^1] = (lastOffset, lastLength + sectorSize);
                    continue;
                }
            }

            mismatches.Add((targetSectorOffset, sectorSize));
        }
    }
}
