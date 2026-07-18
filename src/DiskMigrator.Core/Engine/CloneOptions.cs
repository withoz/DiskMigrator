using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Engine;

public sealed class CloneOptions
{
    /// <summary>
    /// 한 번에 읽고 쓸 크기(바이트). 클수록 순차 처리량이 좋아지지만 취소 반응이 느려집니다.
    /// 섹터 크기의 배수여야 합니다.
    /// </summary>
    public int BufferSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>읽기 실패 시 재시도 횟수. 오래된 HDD는 재시도로 읽히는 경우가 실제로 있습니다.</summary>
    public int ReadRetryCount { get; init; } = 3;

    /// <summary>재시도 사이 대기 시간. 헤드 재시도/재보정 시간을 줍니다.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public BadSectorPolicy BadSectorPolicy { get; init; } = BadSectorPolicy.Abort;

    /// <summary>
    /// ZeroFillAndContinue 정책이라도 이 개수를 넘으면 중단합니다.
    /// 디스크가 통째로 죽어가는 상황에서 무의미하게 몇 시간을 쓰지 않기 위한 안전장치입니다.
    /// </summary>
    public int MaxBadSectors { get; init; } = 2000;

    /// <summary>복제 후 원본과 대상을 해시로 비교할지.</summary>
    public bool VerifyAfterClone { get; init; } = true;

    /// <summary>진행률 콜백 최소 간격. UI 스레드를 초당 수천 번 깨우지 않기 위함.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>쓰기 후 대상 장치를 flush할 간격(바이트). 0이면 마지막에 한 번만.</summary>
    public long FlushInterval { get; init; } = 256L * 1024 * 1024;

    internal void Validate(int sectorSize)
    {
        if (BufferSize <= 0 || BufferSize % sectorSize != 0)
        {
            throw new InvalidOperationException(
                $"버퍼 크기({BufferSize})는 섹터 크기({sectorSize})의 양의 배수여야 합니다.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ReadRetryCount);
    }
}
