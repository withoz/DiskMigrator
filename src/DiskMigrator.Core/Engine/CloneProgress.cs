using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Engine;

/// <summary>진행 중인 작업의 한 시점 스냅샷. UI로 전달됩니다.</summary>
public sealed record CloneProgress
{
    /// <summary>현재 단계 이름 (예: "복제", "검증").</summary>
    public required string Phase { get; init; }

    /// <summary>현재 처리 중인 구간 설명.</summary>
    public required string CurrentRegion { get; init; }

    public required long BytesProcessed { get; init; }

    public required long TotalBytes { get; init; }

    /// <summary>대상 디스크 기준 현재 쓰기 오프셋.</summary>
    public required long CurrentOffset { get; init; }

    /// <summary>최근 구간 기준 순간 속도(바이트/초).</summary>
    public required double SpeedBytesPerSecond { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public int BadSectorCount { get; init; }

    public double Percent => TotalBytes > 0 ? BytesProcessed * 100.0 / TotalBytes : 0;

    /// <summary>남은 예상 시간. 속도를 알 수 없으면 null.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (SpeedBytesPerSecond <= 0) return null;
            long remaining = TotalBytes - BytesProcessed;
            if (remaining <= 0) return TimeSpan.Zero;

            double seconds = remaining / SpeedBytesPerSecond;
            // 초기 몇 초간 속도가 튀면 ETA가 며칠로 나오는 걸 방지합니다.
            return seconds > TimeSpan.MaxValue.TotalSeconds - 1
                ? null
                : TimeSpan.FromSeconds(seconds);
        }
    }
}

/// <summary>읽지 못한 구간 한 건의 기록.</summary>
/// <param name="Offset">원본 장치 기준 오프셋.</param>
/// <param name="Length">읽지 못한 길이(바이트).</param>
/// <param name="Region">해당 구간 설명.</param>
/// <param name="Error">마지막 오류 메시지.</param>
public sealed record BadSectorRecord(long Offset, int Length, string Region, string Error);

/// <summary>클론 작업의 최종 결과. 로그 파일과 결과 화면에 그대로 씁니다.</summary>
public sealed class CloneResult
{
    public required CloneOutcome Outcome { get; init; }
    public required long BytesCopied { get; init; }
    public required TimeSpan Duration { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime FinishedUtc { get; init; }

    public IReadOnlyList<BadSectorRecord> BadSectors { get; init; } = [];

    /// <summary>검증을 수행했다면 통과 여부. 수행하지 않았으면 null.</summary>
    public bool? VerificationPassed { get; init; }

    /// <summary>검증에서 불일치한 구간들 (오프셋, 길이).</summary>
    public IReadOnlyList<(long Offset, long Length)> VerificationMismatches { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public double AverageSpeedBytesPerSecond =>
        Duration.TotalSeconds > 0 ? BytesCopied / Duration.TotalSeconds : 0;
}
