using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// 원시 디스크 장치와 그 디스크의 오프라인 처리·볼륨 잠금을 하나로 묶어,
/// 생명주기를 함께 관리합니다.
/// </summary>
/// <remarks>
/// 세 자원의 해제 순서가 데이터 정합성을 좌우합니다. 오프라인 처리가 클론보다 먼저
/// 풀리면 Windows가 대상 볼륨을 마운트해 NTFS 로그를 재생하며, 우리가 방금 쓴 데이터를
/// 덮어씁니다. 그래서 오프라인 해제는 <b>가장 마지막</b>에, 모든 쓰기·검증·GPT 보정이
/// 끝난 뒤에 이뤄져야 합니다. 세 자원을 한 객체로 묶어 이 순서를 구조적으로 강제합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class LockedDiskDevice(
    RawDiskDevice device,
    VolumeLock? volumeLock,
    DiskOfflineScope? offline,
    DiskInfo disk,
    WindowsDiskService diskService,
    ILogger logger) : IBlockDevice
{
    private bool _disposed;

    public string Id => device.Id;
    public long Length => device.Length;
    public int SectorSize => device.SectorSize;
    public bool CanWrite => device.CanWrite;

    public int Read(long offset, Span<byte> buffer) => device.Read(offset, buffer);

    public void Write(long offset, ReadOnlySpan<byte> buffer) => device.Write(offset, buffer);

    public void Flush() => device.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            device.Flush();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "장치를 닫기 전 flush에 실패했습니다.");
        }

        // 1) 원시 쓰기 핸들을 닫아 남은 쓰기를 모두 내려보냅니다.
        device.Dispose();

        // 2) 볼륨 잠금을 풉니다 (오프라인으로 처리했다면 잠금 자체가 없습니다).
        volumeLock?.Dispose();

        // 3) 파티션 테이블을 다시 읽도록 알립니다. 이걸 빠뜨리면 클론한 디스크의
        //    새 파티션이 나타나지 않아 실패한 것처럼 보입니다.
        diskService.RefreshDiskProperties(disk);

        // 4) 마지막으로 오프라인을 해제합니다. 이 시점엔 모든 데이터가 이미 쓰였고
        //    검증도 끝났으므로, Windows가 볼륨을 마운트해도 우리 데이터를 해치지 않습니다.
        offline?.Dispose();
    }
}
