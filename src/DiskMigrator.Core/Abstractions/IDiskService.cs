using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Abstractions;

/// <summary>연결된 물리 디스크를 열거하고 여는 플랫폼 서비스.</summary>
public interface IDiskService
{
    /// <summary>연결된 모든 물리 디스크를 조회합니다.</summary>
    Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default);

    /// <summary>디스크를 읽기 전용으로 엽니다.</summary>
    IBlockDevice OpenRead(DiskInfo disk);

    /// <summary>
    /// 디스크를 읽기/쓰기로 엽니다. 이 디스크 위의 모든 볼륨을 잠그고 디스마운트해야 하며,
    /// 반환된 장치를 Dispose할 때 잠금이 풀립니다.
    /// </summary>
    /// <remarks>대상 디스크의 기존 데이터를 파괴합니다. 반드시 SafetyGuard 통과 후에만 호출하십시오.</remarks>
    IBlockDevice OpenWriteExclusive(DiskInfo disk);

    /// <summary>
    /// 쓰기 완료 후 Windows에 파티션 테이블을 다시 읽도록 알립니다.
    /// 이걸 하지 않으면 클론한 디스크의 새 파티션이 탐색기에 나타나지 않습니다.
    /// </summary>
    void RefreshDiskProperties(DiskInfo disk);

    /// <summary>현재 프로세스가 관리자 권한으로 실행 중인지.</summary>
    bool IsElevated { get; }
}
