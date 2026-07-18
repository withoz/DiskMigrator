using DiskMigrator.Core.Models;

namespace DiskMigrator.Core.Abstractions;

/// <summary>
/// 실행 중인 볼륨의 특정 시점 스냅샷을 만드는 공급자 (Windows에서는 VSS).
/// </summary>
/// <remarks>
/// 실행 중인 시스템 디스크를 그냥 읽으면 읽는 동안 파일이 계속 바뀌어
/// 결과물이 깨진 상태가 됩니다. 스냅샷은 그 시점에 정지된 이미지를 제공합니다.
/// </remarks>
public interface ISnapshotProvider
{
    /// <summary>이 환경에서 스냅샷을 만들 수 있는지 (VSS 서비스 사용 가능 여부).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 지정한 볼륨들에 대해 하나의 일관된 스냅샷 세트를 만듭니다.
    /// 한 세트로 만들어야 볼륨 간 시점이 일치합니다.
    /// </summary>
    /// <param name="volumeGuidPaths">\\?\Volume{GUID}\ 형식의 볼륨 경로 목록.</param>
    Task<ISnapshotSet> CreateSnapshotSetAsync(
        IReadOnlyList<string> volumeGuidPaths,
        CancellationToken ct = default);
}

/// <summary>생성된 스냅샷 세트. Dispose하면 스냅샷이 삭제됩니다.</summary>
public interface ISnapshotSet : IDisposable
{
    /// <summary>스냅샷이 찍힌 시각(UTC).</summary>
    DateTime CreatedUtc { get; }

    /// <summary>원본 볼륨 경로 → 스냅샷 장치 경로 매핑.</summary>
    IReadOnlyDictionary<string, string> SnapshotDevicePaths { get; }

    /// <summary>원본 볼륨의 스냅샷을 읽기 전용 블록 장치로 엽니다.</summary>
    IBlockDevice OpenSnapshotRead(string originalVolumeGuidPath);
}
