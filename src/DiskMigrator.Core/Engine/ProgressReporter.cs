using System.Diagnostics;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// 진행 상황을 일정 간격으로만 밖에 알리고, 속도를 평활화해서 계산합니다.
/// </summary>
/// <remarks>
/// 버퍼 하나 쓸 때마다 UI를 갱신하면 초당 수백~수천 번 디스패치가 일어나
/// 오히려 복제 속도를 떨어뜨립니다. 또 순간 속도를 그대로 쓰면 ETA가 심하게 요동칩니다.
/// </remarks>
internal sealed class ProgressReporter(
    IProgress<CloneProgress>? progress,
    TimeSpan interval,
    Stopwatch stopwatch,
    long totalBytes)
{
    /// <summary>지수 이동 평균 계수. 낮을수록 부드럽지만 속도 변화에 늦게 반응합니다.</summary>
    private const double SmoothingFactor = 0.25;

    private TimeSpan _lastReportAt = TimeSpan.MinValue;
    private long _lastReportBytes;
    private TimeSpan _lastSpeedSampleAt = TimeSpan.Zero;
    private double _smoothedSpeed;

    public void Report(string phase, string region, long bytesProcessed, long currentOffset, int badSectorCount)
    {
        if (progress is null) return;

        var now = stopwatch.Elapsed;
        if (_lastReportAt != TimeSpan.MinValue && now - _lastReportAt < interval) return;

        Emit(phase, region, bytesProcessed, currentOffset, badSectorCount, now);
    }

    /// <summary>간격과 무관하게 마지막 상태를 강제로 알립니다 (100% 표시가 누락되지 않도록).</summary>
    public void ReportFinal(string phase, string region, long bytesProcessed, long currentOffset, int badSectorCount)
    {
        if (progress is null) return;
        Emit(phase, region, bytesProcessed, currentOffset, badSectorCount, stopwatch.Elapsed);
    }

    private void Emit(string phase, string region, long bytesProcessed, long currentOffset, int badSectorCount, TimeSpan now)
    {
        double windowSeconds = (now - _lastSpeedSampleAt).TotalSeconds;
        if (windowSeconds > 0.05)
        {
            double instantSpeed = (bytesProcessed - _lastReportBytes) / windowSeconds;

            _smoothedSpeed = _smoothedSpeed <= 0
                ? instantSpeed
                : (SmoothingFactor * instantSpeed) + ((1 - SmoothingFactor) * _smoothedSpeed);

            _lastSpeedSampleAt = now;
            _lastReportBytes = bytesProcessed;
        }

        _lastReportAt = now;

        progress!.Report(new CloneProgress
        {
            Phase = phase,
            CurrentRegion = region,
            BytesProcessed = bytesProcessed,
            TotalBytes = totalBytes,
            CurrentOffset = currentOffset,
            SpeedBytesPerSecond = Math.Max(_smoothedSpeed, 0),
            Elapsed = now,
            BadSectorCount = badSectorCount,
        });
    }
}
