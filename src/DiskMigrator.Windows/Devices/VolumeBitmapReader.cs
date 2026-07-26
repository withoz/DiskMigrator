using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Core.Engine;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// NTFS 볼륨(또는 그 VSS 스냅샷)의 할당 비트맵을 읽어, 사용 중인 영역을 복사 런으로 돌려줍니다.
/// 스마트 클론(빈 영역 건너뛰기)의 데이터 출처입니다.
/// </summary>
/// <remarks>
/// 반드시 <b>스냅샷 볼륨 경로</b>로 읽어야 합니다. 라이브 볼륨의 비트맵은 복제 도중 계속
/// 바뀌기 때문입니다(지난 VSS 드리프트 교훈과 같은 이유). 비트맵 파싱은 플랫폼 독립인
/// <see cref="AllocationBitmap"/>에 위임하고, 여기서는 Win32 FSCTL로 비트맵을 얻어옵니다.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class VolumeBitmapReader(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 볼륨의 사용 중 영역을 복사 런(볼륨 시작 기준 바이트)으로 돌려줍니다.
    /// </summary>
    /// <param name="volumePath">볼륨/스냅샷 장치 경로(후행 백슬래시 없이).</param>
    /// <param name="coalesceGapBytes">이보다 작은 빈틈은 이어 붙여 IO 조각화를 줄입니다.</param>
    /// <param name="capBytes">이 바이트를 넘는 런은 잘라냅니다(스냅샷 읽기 가능 길이 상한).</param>
    public IReadOnlyList<AllocationBitmap.Run> ReadRuns(string volumePath, long coalesceGapBytes, long capBytes)
    {
        using var handle = OpenVolume(volumePath);

        var (bytesPerCluster, totalClusters) = ReadNtfsData(handle);
        byte[] bitmap = ReadBitmap(handle, totalClusters);

        var runs = AllocationBitmap.ExtractRuns(bitmap, totalClusters, bytesPerCluster, coalesceGapBytes, capBytes);

        long usedBytes = runs.Sum(r => r.LengthBytes);
        _logger.LogInformation(
            "할당 비트맵: 클러스터 {Clusters:N0}개 × {Bpc}바이트, 사용 영역 {Runs}런 / {Used:N0}바이트 (상한 {Cap:N0}).",
            totalClusters, bytesPerCluster, runs.Count, usedBytes, capBytes);

        return runs;
    }

    /// <summary>
    /// 볼륨의 NTFS 사용량을 측정합니다(사용 바이트 + 파일 이동 없는 축소 하한). 볼륨을 수정하지
    /// 않으며, 축소 <b>제안</b> 용도라 라이브 볼륨에서 읽어도 됩니다(이 값으로 데이터를 복사하지
    /// 않으므로 스마트 클론과 달리 스냅샷이 필수는 아닙니다).
    /// </summary>
    public AllocationBitmap.NtfsUsage MeasureUsage(string volumePath)
    {
        using var handle = OpenVolume(volumePath);

        var (bytesPerCluster, totalClusters) = ReadNtfsData(handle);
        byte[] bitmap = ReadBitmap(handle, totalClusters);

        var usage = AllocationBitmap.MeasureUsage(bitmap, totalClusters, bytesPerCluster);
        _logger.LogInformation(
            "NTFS 사용량: 전체 {Total:N0} / 사용 {Used:N0} / 마지막 사용 끝 {High:N0} 바이트 " +
            "(제안 최소 축소 {Sug:N0}).",
            usage.TotalBytes, usage.UsedBytes, usage.HighestUsedByte, usage.SuggestedMinShrinkBytes());

        return usage;
    }

    private static SafeFileHandle OpenVolume(string volumePath)
    {
        // FSCTL은 후행 백슬래시 없는 볼륨 경로를 받습니다.
        string path = volumePath.TrimEnd('\\');

        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            0);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                DiskMigrator.Core.Localization.L.T($"할당 비트맵을 읽기 위해 볼륨 {path} 을(를) 열지 못했습니다.", $"Failed to open volume {path} to read the allocation bitmap."));
        }

        return handle;
    }

    /// <summary>NTFS_VOLUME_DATA_BUFFER에서 BytesPerCluster(오프셋 44), TotalClusters(오프셋 16)를 읽습니다.</summary>
    private static (long BytesPerCluster, long TotalClusters) ReadNtfsData(SafeFileHandle handle)
    {
        byte[] data = DiskIoctl.QueryVariable(
            handle, NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA, initialSize: 128);

        if (data.Length < 48)
        {
            throw new InvalidOperationException(
                DiskMigrator.Core.Localization.L.T($"NTFS 볼륨 데이터가 너무 짧습니다({data.Length}바이트). NTFS 볼륨이 아닐 수 있습니다.", $"NTFS volume data is too short ({data.Length} bytes). This may not be an NTFS volume."));
        }

        long totalClusters = BitConverter.ToInt64(data, 16);
        uint bytesPerCluster = BitConverter.ToUInt32(data, 44);

        if (bytesPerCluster == 0)
        {
            throw new InvalidOperationException(DiskMigrator.Core.Localization.L.T("BytesPerCluster가 0입니다 — 예상치 못한 NTFS 볼륨 데이터.", "BytesPerCluster is 0 — unexpected NTFS volume data."));
        }

        return (bytesPerCluster, totalClusters);
    }

    /// <summary>
    /// FSCTL_GET_VOLUME_BITMAP을 청크로 반복 호출해 전체 비트맵을 모읍니다.
    /// </summary>
    /// <remarks>
    /// 이 FSCTL의 ERROR_MORE_DATA는 "버퍼 부족"이 아니라 "이번 호출 데이터는 유효하고 뒤에
    /// 더 있음"이라는 뜻입니다. 그래서 QueryVariable(버퍼를 키움)이 아니라, StartingLcn을
    /// 전진시키며 조각을 이어 붙이는 전용 루프가 필요합니다.
    /// </remarks>
    private byte[] ReadBitmap(SafeFileHandle handle, long totalClusters)
    {
        long totalByteLen = (totalClusters + 7) / 8;
        var bitmap = new byte[totalByteLen];

        const int outSize = 1 << 20; // 호출당 1MB (≈8.4M 클러스터 ≈ 4KB 클러스터 기준 33GB)
        const int headerSize = 16;   // LARGE_INTEGER StartingLcn + LARGE_INTEGER BitmapSize

        nint inBuf = Marshal.AllocHGlobal(sizeof(long));
        nint outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            long nextLcn = 0;
            while (nextLcn < totalClusters)
            {
                Marshal.WriteInt64(inBuf, nextLcn);

                bool ok = NativeMethods.DeviceIoControl(
                    handle, NativeMethods.FSCTL_GET_VOLUME_BITMAP,
                    inBuf, sizeof(long), outBuf, outSize, out uint returned, 0);

                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != NativeMethods.ERROR_MORE_DATA)
                {
                    throw new Win32Exception(error, DiskMigrator.Core.Localization.L.T("FSCTL_GET_VOLUME_BITMAP 호출이 실패했습니다.", "The FSCTL_GET_VOLUME_BITMAP call failed."));
                }

                long outStartLcn = Marshal.ReadInt64(outBuf, 0);   // 바이트 경계로 내림된 시작 LCN
                long bitmapSize = Marshal.ReadInt64(outBuf, 8);    // outStartLcn부터 볼륨 끝까지의 비트 수
                int payloadBytes = (int)returned - headerSize;
                if (payloadBytes <= 0) break;

                long bitsThisCall = Math.Min(bitmapSize, (long)payloadBytes * 8);
                int destByteOffset = (int)(outStartLcn / 8);
                int copyBytes = (int)Math.Min((bitsThisCall + 7) / 8, payloadBytes);
                copyBytes = (int)Math.Min(copyBytes, bitmap.Length - destByteOffset);
                if (copyBytes <= 0) break;

                Marshal.Copy(outBuf + headerSize, bitmap, destByteOffset, copyBytes);

                nextLcn = outStartLcn + bitsThisCall;
                if (ok) break;              // MORE_DATA가 아니면 나머지까지 받은 것
                if (bitsThisCall <= 0) break; // 안전장치: 진전이 없으면 중단
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        return bitmap;
    }
}
