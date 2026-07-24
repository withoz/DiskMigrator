using System.Runtime.Versioning;
using DiskMigrator.Core.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// NTFS 볼륨의 실사용량과 "파일을 안 옮기고 줄일 수 있는 최소 크기"를 측정합니다.
/// 축소 리사이즈에서 안전한 목표 크기를 제안하는 데 씁니다.
/// </summary>
/// <remarks>
/// 할당 비트맵(<c>FSCTL_GET_VOLUME_BITMAP</c>)만 읽으므로 볼륨을 <b>수정하지 않습니다</b>. 축소
/// <b>제안</b> 용도라 라이브 볼륨에서 읽어도 됩니다(스마트 클론과 달리 이 값으로 데이터를 복사하지
/// 않음). 실제 축소 가능 여부와 최종 하한은 축소를 실행할 때 Windows 축소기가 확정합니다 — 이
/// 산정기는 사용자에게 "여기까지 줄일 수 있다"를 미리 보여 주기 위한 것입니다.
///
/// <para>비트맵 파싱·측정은 플랫폼 독립인 <see cref="AllocationBitmap.MeasureUsage"/>에 있고,
/// 여기서는 Win32 FSCTL로 비트맵을 얻어오는 <see cref="VolumeBitmapReader"/>를 감쌉니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NtfsUsageProbe(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>지정한 볼륨/스냅샷의 NTFS 사용량을 측정합니다.</summary>
    /// <param name="volumePath">
    /// 볼륨 장치 경로(예: <c>\\.\C:</c> 또는 볼륨 GUID 경로·스냅샷 장치 경로, 후행 백슬래시 무관).
    /// </param>
    public AllocationBitmap.NtfsUsage Measure(string volumePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumePath);
        return new VolumeBitmapReader(_logger).MeasureUsage(volumePath);
    }
}
