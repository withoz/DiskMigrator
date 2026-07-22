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

    /// <summary>
    /// 이동식(USB) 대상 디스크를 안전하게 제거할 수 있도록, 볼륨을 디스마운트하고 디스크를
    /// 오프라인으로 내립니다. 클론이 끝나면 대상을 다시 온라인으로 올려 복제된 볼륨이 자동
    /// 마운트되는데, 그 상태에서는 Windows가 "장치 사용 중"이라며 안전 제거를 막습니다.
    /// 이 메서드가 볼륨을 내려 캐시를 비우면 사용자가 USB를 그대로 뽑아도 안전합니다.
    /// </summary>
    /// <remarks>사용자 데이터는 이미 대상에 온전히 쓰인 뒤이므로, 오프라인 전환은 손상을 일으키지 않습니다.</remarks>
    Task<SafeRemoveResult> SafeRemoveAsync(DiskInfo disk, CancellationToken ct = default);

    /// <summary>현재 프로세스가 관리자 권한으로 실행 중인지.</summary>
    bool IsElevated { get; }
}

/// <summary>
/// 안전 제거 결과. 사용자에게 보여줄 문구는 App 계층이 현지화해 조립하므로, 여기서는
/// 성공 여부와 (실패 시) 원인 상세만 전달합니다.
/// </summary>
public sealed record SafeRemoveResult(bool Success, string? ErrorDetail = null);
