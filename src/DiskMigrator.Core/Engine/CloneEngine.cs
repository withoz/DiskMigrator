using System.Diagnostics;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// <see cref="ClonePlan"/>의 각 구간을 대상 장치로 복사하는 엔진.
/// </summary>
/// <remarks>
/// 플랫폼에 대해 아무것도 모릅니다. <see cref="Abstractions.IBlockDevice"/>만 다루므로
/// 실제 디스크 없이 파일 기반 장치로 전체 동작을 테스트할 수 있습니다.
/// </remarks>
public sealed class CloneEngine(ILogger<CloneEngine>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<CloneEngine>.Instance;

    /// <summary>클론을 실행합니다. 호출자의 스레드를 막지 않도록 백그라운드에서 돕니다.</summary>
    public Task<CloneResult> RunAsync(
        ClonePlan plan,
        CloneOptions options,
        IProgress<CloneProgress>? progress = null,
        PauseController? pause = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => Run(plan, options, progress, pause, ct), CancellationToken.None);
    }

    private CloneResult Run(
        ClonePlan plan,
        CloneOptions options,
        IProgress<CloneProgress>? progress,
        PauseController? pause,
        CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var badSectors = new List<BadSectorRecord>();
        long bytesCopied = 0;

        try
        {
            plan.Validate();
            options.Validate(plan.Target.SectorSize);

            _logger.LogInformation(
                "클론 시작: {Name} — 대상 {Target}, 구간 {RegionCount}개, 총 {TotalBytes:N0}바이트",
                plan.Name, plan.Target.Id, plan.Regions.Count, plan.TotalBytes);

            foreach (var region in plan.Regions)
            {
                _logger.LogInformation("구간 복사: {Region}", region);
            }

            var hashLog = new CopyHashLog();
            bytesCopied = CopyAll(plan, options, progress, pause, badSectors, hashLog, stopwatch, ct);

            plan.Target.Flush();

            bool? verificationPassed = null;
            IReadOnlyList<(long, long)> mismatches = [];

            if (options.VerifyAfterClone)
            {
                // 원본(드리프트 가능한 스냅샷)을 다시 읽지 않고, 복제 때 대상에 쓴 데이터의
                // 해시와 대상을 비교합니다. 쓰기 오류만 정확히 잡고 스냅샷 드리프트엔 영향받지 않습니다.
                var verification = Verifier.VerifyAgainstHashes(
                    plan, options, hashLog, progress, pause, stopwatch, ct);
                verificationPassed = verification.Passed;
                mismatches = verification.Mismatches;

                if (!verification.Passed)
                {
                    long mismatchBytes = verification.Mismatches.Sum(m => m.Length);

                    _logger.LogError(
                        "검증 실패: 불일치 구간 {Count}개, 총 {Bytes:N0}바이트 " +
                        "(전체의 {Percent:F6}%). 대상이 우리가 쓴 데이터를 그대로 보존하지 못했습니다 — " +
                        "매체 불량이나 쓰기 오류일 가능성이 높습니다. 앞쪽 불일치 위치를 남깁니다.",
                        verification.Mismatches.Count, mismatchBytes,
                        plan.TotalBytes > 0 ? mismatchBytes * 100.0 / plan.TotalBytes : 0);

                    // 앞쪽 40개의 대상 오프셋을, 그 오프셋이 속한 구간(파티션) 설명과 함께 남깁니다.
                    foreach (var (offset, length) in verification.Mismatches.Take(40))
                    {
                        var region = plan.Regions.FirstOrDefault(
                            r => offset >= r.TargetOffset && offset < r.TargetOffset + r.Length);
                        string where = region is null ? "구간 밖" : region.Description;
                        _logger.LogError(
                            "  불일치: 대상 오프셋 {Offset:N0} 길이 {Length:N0} — {Where}",
                            offset, length, where);
                    }
                }
            }

            stopwatch.Stop();

            var outcome = badSectors.Count > 0
                ? CloneOutcome.CompletedWithBadSectors
                : CloneOutcome.Completed;

            _logger.LogInformation(
                "클론 완료: {Bytes:N0}바이트, {Duration}, 평균 {Speed}, 불량 섹터 {Bad}건",
                bytesCopied, stopwatch.Elapsed,
                SizeFormatter.FormatSpeed(bytesCopied / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)),
                badSectors.Count);

            return new CloneResult
            {
                Outcome = verificationPassed == false ? CloneOutcome.Failed : outcome,
                BytesCopied = bytesCopied,
                Duration = stopwatch.Elapsed,
                StartedUtc = startedUtc,
                FinishedUtc = DateTime.UtcNow,
                BadSectors = badSectors,
                VerificationPassed = verificationPassed,
                VerificationMismatches = mismatches,
                ErrorMessage = verificationPassed == false
                    ? L.T("복제는 끝났지만 검증에서 원본과 대상이 일치하지 않았습니다. 대상 디스크를 신뢰하지 마십시오.",
                          "Copying finished, but verification found that the source and target do not match. Do not trust the target disk.")
                    : null,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("클론 취소됨: {Bytes:N0}바이트 복사 후 중단", bytesCopied);

            TryFlush(plan);

            return new CloneResult
            {
                Outcome = CloneOutcome.Cancelled,
                BytesCopied = bytesCopied,
                Duration = stopwatch.Elapsed,
                StartedUtc = startedUtc,
                FinishedUtc = DateTime.UtcNow,
                BadSectors = badSectors,
                ErrorMessage = L.T(
                    "사용자가 작업을 취소했습니다. 대상 디스크는 불완전한 상태이므로 부팅하거나 사용하지 마십시오.",
                    "The operation was cancelled. The target disk is incomplete — do not boot or use it."),
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "클론 실패: {Bytes:N0}바이트 복사 후 오류", bytesCopied);

            TryFlush(plan);

            return new CloneResult
            {
                Outcome = CloneOutcome.Failed,
                BytesCopied = bytesCopied,
                Duration = stopwatch.Elapsed,
                StartedUtc = startedUtc,
                FinishedUtc = DateTime.UtcNow,
                BadSectors = badSectors,
                ErrorMessage = ex.Message,
            };
        }
    }

    private long CopyAll(
        ClonePlan plan,
        CloneOptions options,
        IProgress<CloneProgress>? progress,
        PauseController? pause,
        List<BadSectorRecord> badSectors,
        CopyHashLog hashLog,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        using var buffer = new AlignedBuffer(options.BufferSize);
        using var sectorBuffer = new AlignedBuffer(MaxSectorBufferSize(plan));
        using var compareBuffer = options.WriteOnlyChangedBlocks ? new AlignedBuffer(options.BufferSize) : null;

        var reporter = new ProgressReporter(progress, options.ProgressInterval, stopwatch, plan.TotalBytes);
        long totalCopied = 0;
        long sinceLastFlush = 0;
        long skippedIdentical = 0;
        long skippedResume = 0;

        if (options.ResumeFromBytes > 0)
        {
            _logger.LogInformation(
                "이어하기: {Resume:N0}바이트 지점까지는 기록을 건너뜁니다(검증 해시는 전체 기록).",
                options.ResumeFromBytes);
        }

        try
        {
        foreach (var region in plan.Regions)
        {
            long position = 0;

            while (position < region.Length)
            {
                ct.ThrowIfCancellationRequested();
                pause?.WaitIfPaused(ct);

                int chunk = (int)Math.Min(options.BufferSize, region.Length - position);
                var span = buffer.SpanOf(chunk);

                long sourceOffset = region.SourceOffset + position;
                int read = ReadWithRecovery(
                    region, sourceOffset, span, sectorBuffer, options, badSectors, ct);

                if (read <= 0)
                {
                    throw new IOException(
                        $"{region.Description}: 오프셋 {sourceOffset:N0}에서 읽은 바이트가 없습니다.");
                }

                long targetOffset = region.TargetOffset + position;

                // 이어하기: 저널이 보증하는 지점 이전은 이미 대상에 기록돼 있으므로 쓰기를
                // 건너뜁니다. 원본 읽기·해시 기록은 그대로 하므로 검증 범위는 줄지 않습니다.
                // (청크가 경계에 걸치면 그 청크는 통째로 다시 씁니다 — 덮어쓰기는 무해합니다.)
                bool alreadyWritten = totalCopied + read <= options.ResumeFromBytes;

                // 증분 모드: 대상(차등 자식이면 부모의 병합 뷰)을 먼저 읽어, 같은 내용이면 쓰기를
                // 건너뜁니다 — 차등 자식에는 쓴 블록만 파일에 남으므로 변경분만 저장됩니다.
                bool identical = false;
                if (!alreadyWritten && compareBuffer is not null)
                {
                    var existing = compareBuffer.SpanOf(read);
                    identical = plan.Target.Read(targetOffset, existing) == read &&
                                existing.SequenceEqual(span[..read]);
                }

                if (alreadyWritten) skippedResume += read;
                else if (identical) skippedIdentical += read;
                else plan.Target.Write(targetOffset, span[..read]);

                // 검증용: 방금 대상에 쓴 데이터의 해시를 기록해 둡니다. 검증 때 원본(드리프트
                // 가능한 스냅샷)을 다시 읽는 대신 이 해시와 대상을 비교합니다.
                if (options.VerifyAfterClone)
                {
                    hashLog.Record(targetOffset, span[..read]);
                }

                position += read;
                totalCopied += read;
                sinceLastFlush += read;

                if (options.FlushInterval > 0 && sinceLastFlush >= options.FlushInterval)
                {
                    plan.Target.Flush();
                    sinceLastFlush = 0;
                    // 플러시 후에만 체크포인트 — 이 지점까지는 디스크에 실제로 기록됐습니다.
                    plan.FlushCheckpoint?.Invoke(totalCopied);
                }

                reporter.Report(L.T("복제", "Copying"), region.Description, totalCopied,
                    region.TargetOffset + position, badSectors.Count);
            }
        }
        }
        catch
        {
            // 취소·오류로 멈출 때도 여기까지의 기록을 디스크에 확정하고 체크포인트를 남깁니다 —
            // 다음 실행이 이 지점부터 이어할 수 있습니다(플러시 실패는 저널만 못 남길 뿐 무해).
            try
            {
                plan.Target.Flush();
                plan.FlushCheckpoint?.Invoke(totalCopied);
            }
            catch { /* 이어하기 기회를 잃을 뿐, 원래 예외를 그대로 전달한다 */ }
            throw;
        }

        reporter.ReportFinal(L.T("복제", "Copying"), plan.Regions[^1].Description, totalCopied,
            plan.Target.Length, badSectors.Count);

        if (options.WriteOnlyChangedBlocks)
        {
            _logger.LogInformation(
                "증분 쓰기: 처리 {Total:N0}바이트 중 변경 {Changed:N0}바이트만 기록 (동일 {Same:N0}바이트 건너뜀).",
                totalCopied, totalCopied - skippedIdentical, skippedIdentical);
        }
        if (skippedResume > 0)
        {
            _logger.LogInformation(
                "이어하기 완료: 앞부분 {Resume:N0}바이트는 쓰기 생략(이미 기록됨), 새로 기록 {New:N0}바이트.",
                skippedResume, totalCopied - skippedResume - skippedIdentical);
        }

        return totalCopied;
    }

    /// <summary>
    /// 블록을 읽되, 실패하면 정책에 따라 재시도하거나 섹터 단위로 구조합니다.
    /// </summary>
    private int ReadWithRecovery(
        CopyRegion region,
        long offset,
        Span<byte> buffer,
        AlignedBuffer sectorBuffer,
        CloneOptions options,
        List<BadSectorRecord> badSectors,
        CancellationToken ct)
    {
        try
        {
            return ReadWithRetry(region.Source, offset, buffer, options, ct);
        }
        catch (IOException ex) when (options.BadSectorPolicy == BadSectorPolicy.ZeroFillAndContinue)
        {
            _logger.LogWarning(
                "{Region}: 오프셋 {Offset:N0} 블록 읽기 실패 — 섹터 단위로 재시도합니다. ({Error})",
                region.Description, offset, ex.Message);

            return SalvageSectorBySector(region, offset, buffer, sectorBuffer, options, badSectors, ct);
        }
    }

    internal static int ReadWithRetry(
        IBlockDevice source,
        long offset,
        Span<byte> buffer,
        CloneOptions options,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return source.Read(offset, buffer);
            }
            catch (IOException) when (attempt < options.ReadRetryCount)
            {
                // 노후 HDD는 재시도로 읽히는 경우가 실제로 있습니다. 헤드 재보정 시간을 줍니다.
                if (options.RetryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(options.RetryDelay);
                }
            }
        }
    }

    /// <summary>
    /// 블록 전체 읽기가 실패했을 때, 섹터 단위로 쪼개 읽어 살릴 수 있는 데이터를 최대한 살립니다.
    /// 진짜로 읽히지 않는 섹터만 0으로 채우고 기록합니다.
    /// </summary>
    private int SalvageSectorBySector(
        CopyRegion region,
        long blockOffset,
        Span<byte> buffer,
        AlignedBuffer sectorBuffer,
        CloneOptions options,
        List<BadSectorRecord> badSectors,
        CancellationToken ct)
    {
        int sectorSize = region.Source.SectorSize;
        int sectorCount = buffer.Length / sectorSize;

        for (int i = 0; i < sectorCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            long sectorOffset = blockOffset + (long)i * sectorSize;
            var destination = buffer.Slice(i * sectorSize, sectorSize);

            // 슬라이스 주소가 4KB 정렬이 아닐 수 있어 FILE_FLAG_NO_BUFFERING 요구를 어길 수 있습니다.
            // 정렬된 별도 버퍼로 읽은 뒤 복사합니다. 이 경로는 드물게만 타므로 비용은 문제되지 않습니다.
            var staging = sectorBuffer.SpanOf(sectorSize);

            try
            {
                int read = ReadWithRetry(region.Source, sectorOffset, staging, options, ct);

                if (read == sectorSize)
                {
                    staging.CopyTo(destination);
                    continue;
                }

                // 짧게 읽힘 — 읽힌 만큼만 쓰고 나머지는 0으로.
                staging[..read].CopyTo(destination);
                destination[read..].Clear();
            }
            catch (IOException ex)
            {
                destination.Clear();
                badSectors.Add(new BadSectorRecord(sectorOffset, sectorSize, region.Description, ex.Message));

                _logger.LogWarning(
                    "불량 섹터: {Region} 오프셋 {Offset:N0} ({Size}바이트) — 0으로 채우고 계속합니다.",
                    region.Description, sectorOffset, sectorSize);

                if (badSectors.Count > options.MaxBadSectors)
                {
                    throw new IOException(L.T(
                        $"불량 섹터가 {options.MaxBadSectors}개를 넘었습니다. 원본 디스크가 심각하게 " +
                        "손상된 것으로 보입니다. 작업을 중단합니다.",
                        $"Bad sectors exceeded {options.MaxBadSectors}. The source disk appears to be " +
                        "seriously damaged. Stopping the operation."));
                }
            }
        }

        return buffer.Length;
    }

    private void TryFlush(ClonePlan plan)
    {
        try
        {
            plan.Target.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "중단 처리 중 대상 장치 flush에 실패했습니다.");
        }
    }

    /// <summary>
    /// 섹터 단위 구조(salvage) 경로에서 쓸 정렬 버퍼의 크기.
    /// 계획에 등장하는 모든 장치의 섹터 크기를 담을 수 있어야 하고,
    /// FILE_FLAG_NO_BUFFERING 정렬 요구를 확실히 만족하도록 4KB 배수로 올립니다.
    /// </summary>
    internal static int MaxSectorBufferSize(ClonePlan plan)
    {
        int maxSector = plan.Regions
            .Select(r => r.Source.SectorSize)
            .Append(plan.Target.SectorSize)
            .Max();

        return Math.Max(4096, (maxSector + 4095) / 4096 * 4096);
    }
}
