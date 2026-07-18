using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Util;

namespace DiskMigrator.Core.Devices;

/// <summary>
/// 장치가 보고하는 크기가 아니라, 실제로 읽을 수 있는 길이를 찾습니다.
/// </summary>
/// <remarks>
/// VSS 섀도 복사본은 IOCTL_DISK_GET_LENGTH_INFO로 볼륨 전체 크기를 보고하면서도
/// 마지막 몇 섹터의 읽기는 0을 돌려줍니다. 실기에서 1,056,899,072바이트 볼륨의
/// 마지막 4,096바이트가 이렇게 동작하는 것을 관측했고, 보고된 크기를 그대로 믿었더니
/// 복제가 끝에서 실패했습니다.
///
/// 장치의 끝은 단조적이므로(어느 지점부터 끝까지 못 읽음) 이진 탐색으로 경계를 찾습니다.
/// 1GB 볼륨 기준 21회 정도의 섹터 읽기면 끝나므로 비용은 무시할 수 있습니다.
/// </remarks>
public static class ReadableLengthProbe
{
    /// <summary>
    /// <paramref name="device"/>에서 오프셋 0부터 연속으로 읽을 수 있는 최대 길이를 찾습니다.
    /// </summary>
    /// <param name="device">조사할 장치.</param>
    /// <param name="upperBound">이 길이를 넘어서는 조사하지 않습니다 (보통 파티션 길이).</param>
    /// <returns>섹터 크기의 배수인, 실제로 읽을 수 있는 길이.</returns>
    public static long Probe(IBlockDevice device, long upperBound)
    {
        ArgumentNullException.ThrowIfNull(device);

        int sectorSize = device.SectorSize;
        long limit = AlignDown(Math.Min(device.Length, upperBound), sectorSize);

        if (limit <= 0) return 0;

        long totalSectors = limit / sectorSize;

        using var buffer = new AlignedBuffer(Math.Max(4096, sectorSize));

        // 흔한 경우(끝까지 멀쩡히 읽힘)를 한 번의 읽기로 처리합니다.
        if (CanReadSector(device, totalSectors - 1, sectorSize, buffer)) return limit;

        // 읽을 수 있는 섹터 개수 K를 찾습니다: 인덱스 < K는 읽히고, >= K는 안 읽힙니다.
        // 불변식: low <= K <= high.
        long low = 0;
        long high = totalSectors - 1;

        while (low < high)
        {
            long mid = low + ((high - low + 1) / 2);

            if (CanReadSector(device, mid - 1, sectorSize, buffer)) low = mid;
            else high = mid - 1;
        }

        return low * sectorSize;
    }

    private static bool CanReadSector(IBlockDevice device, long sectorIndex, int sectorSize, AlignedBuffer buffer)
    {
        if (sectorIndex < 0) return false;

        try
        {
            return device.Read(sectorIndex * sectorSize, buffer.SpanOf(sectorSize)) == sectorSize;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
}
