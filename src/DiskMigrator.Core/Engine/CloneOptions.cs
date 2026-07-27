using DiskMigrator.Core.Models;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// 큰 대상으로 복제했을 때 남는 공간을 어떻게 할지 — 하나만 고를 수 있습니다.
/// </summary>
/// <remarks>
/// 세 방법은 같은 질문의 답이라 <b>배타적</b>입니다. 별개 스위치로 두면 동시에 켤 수 있고,
/// 그러면 하나가 조용히 무시되어 사용자가 고른 것과 다른 결과가 나옵니다.
/// </remarks>
public enum FreeSpaceMode
{
    /// <summary>미할당으로 남깁니다. 나중에 디스크 관리에서 쓸 수 있습니다.</summary>
    Leave,

    /// <summary>
    /// 빈 공간 <b>바로 앞</b>(마지막) 파티션을 늘립니다. 파티션을 옮기지 않아 간단하고 MBR도 됩니다.
    /// 다만 마지막 파티션이 복구 파티션이면 엉뚱한 것이 커지므로 주의해야 합니다.
    /// </summary>
    ExpandLast,

    /// <summary>
    /// <b>고른 파티션</b>을 넓히고 그 뒤 파티션들을 오른쪽으로 밉니다. C: 처럼 중간에 있는
    /// 파티션도 넓힐 수 있지만, GPT를 다시 써야 하므로 GPT 원본만 지원합니다.
    /// </summary>
    GrowPartition,
}

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

    /// <summary>
    /// 빈 영역을 건너뛰고 <b>사용 중인 블록만</b> 복사할지(스마트 클론). NTFS 할당 비트맵을
    /// 읽어 쓰이는 클러스터만 복제하므로, 실데이터가 적으면 크게 빨라집니다.
    /// 스냅샷 모드에서만 동작하며, 실패 시 해당 파티션은 통째 복사로 안전하게 되돌립니다.
    /// </summary>
    public bool SkipUnusedBlocks { get; init; }

    /// <summary>
    /// 이어하기: 계획 순서 기준 이 바이트 지점까지는 이미 대상에 기록돼 있으므로
    /// <b>쓰기를 건너뜁니다</b>(0=처음부터).
    /// </summary>
    /// <remarks>
    /// 중단된 복원을 다시 시작할 때, 저널이 보증하는(플러시 완료) 지점을 넘깁니다.
    /// 건너뛰는 앞부분도 <b>원본은 그대로 읽어</b> 검증 해시를 채우므로, 복제 후 검증은
    /// 전체 범위를 그대로 커버합니다 — 절약되는 것은 대상 쓰기뿐이고, 신뢰는 깎이지
    /// 않습니다. 원본이 정적일 때(이미지 파일)만 의미가 있습니다 — 살아 있는 원본은
    /// 중단 전후 내용이 달라 이어붙이면 시점이 섞입니다.
    /// </remarks>
    public long ResumeFromBytes { get; init; }

    /// <summary>
    /// 대상에서 같은 내용의 블록은 쓰지 않고 건너뛸지(증분 백업용).
    /// </summary>
    /// <remarks>
    /// 켜면 블록마다 대상을 먼저 읽어 원본과 비교하고, <b>다른 블록만</b> 씁니다. 차등 VHDX
    /// 자식을 대상으로 하면 자식의 병합 뷰(=부모 내용)와 비교되므로, 부모 백업 이후 바뀐
    /// 블록만 자식 파일에 기록됩니다 — 이것이 증분 백업의 원리입니다. 읽기량은 원본+대상
    /// 두 배가 되지만 쓰기(=자식 파일 크기)가 변경분만큼으로 줄어듭니다.
    /// 대상 장치가 읽기를 지원해야 합니다.
    /// </remarks>
    public bool WriteOnlyChangedBlocks { get; init; }

    /// <summary>
    /// 대상에 남는 공간을 어떻게 할지. <see cref="GrowRequest"/>와 짝을 이룹니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 "마지막 파티션 확장"과 "파티션 리사이즈"가 <b>별개 불리언</b>이었습니다. 사실은
    /// 같은 질문("남는 공간을 누구에게?")의 답인데 둘 다 켤 수 있었고, 그러면 리사이즈가 이기고
    /// 다른 하나는 <b>조용히 무시</b>됐습니다. 하나의 모드로 합쳐 그 조합 자체를 없앴습니다.
    /// </remarks>
    public FreeSpaceMode FreeSpace { get; init; } = FreeSpaceMode.Leave;

    // ZeroFreeSpace(스마트 클론에서 건너뛴 영역을 0으로 채우기)는 여기 있었지만 엔진 어디에서도
    // 읽지 않는 죽은 스위치였습니다. 동작하는 것처럼 문서화된 채 남아 있으면 나중에 누군가
    // 화면에 체크박스를 달아 아무 일도 안 하는 옵션을 만들게 됩니다. 실제로 구현할 때 다시 넣습니다.

    /// <summary>
    /// 넓힐 파티션과 크기. <see cref="FreeSpace"/>가 <see cref="FreeSpaceMode.GrowPartition"/>일 때만
    /// 쓰이며, 그 경우 반드시 지정해야 합니다.
    /// </summary>
    /// <remarks>
    /// 설정하면 대상에서 이 파티션을 넓히고 그 뒤 파티션들을 오른쪽으로 밀어 배치합니다.
    /// 대상이 원본보다 커야 하며, 확대된 파티션은 클론 후 NTFS를 그 크기까지 확장합니다.
    /// 축소(대상 &lt; 원본)는 지원하지 않습니다(파일시스템 인식 축소는 다음 버전).
    /// </remarks>
    public PartitionGrowRequest? GrowRequest { get; init; }

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
