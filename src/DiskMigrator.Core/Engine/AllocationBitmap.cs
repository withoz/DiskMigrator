namespace DiskMigrator.Core.Engine;

/// <summary>
/// 볼륨 할당 비트맵(1비트 = 1클러스터)에서 <b>사용 중인 영역</b>만 복사 런으로 뽑아냅니다.
/// 스마트 클론(빈 영역 건너뛰기)의 핵심 순수 로직입니다.
/// </summary>
/// <remarks>
/// NTFS는 <c>FSCTL_GET_VOLUME_BITMAP</c>으로 클러스터 할당 비트맵을 제공합니다(비트가 켜져
/// 있으면 그 클러스터가 쓰이는 중). 이 클래스는 그 비트맵을 받아 연속된 할당 영역을
/// "런(run)"으로 묶습니다. 플랫폼 의존성이 없어 실제 디스크 없이 단위 테스트할 수 있습니다.
///
/// 비트맵의 비트 순서는 NTFS 규약을 따릅니다: <c>bitmap[i]</c>의 <c>bit b</c>(LSB=0)가
/// 클러스터 <c>i*8 + b</c>에 해당합니다.
/// </remarks>
public static class AllocationBitmap
{
    /// <summary>볼륨 시작 기준 바이트 오프셋과 길이로 나타낸 사용 영역 한 조각.</summary>
    public readonly record struct Run(long OffsetBytes, long LengthBytes)
    {
        public long EndBytes => OffsetBytes + LengthBytes;
    }

    /// <summary>
    /// 할당 비트맵에서 복사할 런 목록을 만듭니다(볼륨 시작 기준 바이트, 오름차순).
    /// </summary>
    /// <param name="bitmap">클러스터 할당 비트맵. LSB-first.</param>
    /// <param name="clusterCount">유효한 클러스터(비트) 수. 비트맵 길이보다 클 수 없습니다.</param>
    /// <param name="bytesPerCluster">클러스터 크기(바이트). 섹터 크기의 배수입니다.</param>
    /// <param name="coalesceGapBytes">
    /// 이보다 작은 빈틈은 이어 붙여 런을 하나로 합칩니다. 미세한 빈틈마다 런을 쪼개면 IO가
    /// 잘게 조각나 오히려 느려지므로, 약간의 빈 영역을 함께 복사하는 편이 이득입니다.
    /// </param>
    /// <param name="capBytes">
    /// 이 바이트를 넘는 부분은 잘라냅니다. 스냅샷의 실제 읽기 가능 길이를 상한으로 둘 때 씁니다.
    /// </param>
    public static List<Run> ExtractRuns(
        ReadOnlySpan<byte> bitmap,
        long clusterCount,
        long bytesPerCluster,
        long coalesceGapBytes,
        long capBytes)
    {
        var runs = new List<Run>();
        if (clusterCount <= 0 || bytesPerCluster <= 0 || capBytes <= 0) return runs;

        long maxByBitmap = (long)bitmap.Length * 8;
        long clusters = Math.Min(clusterCount, maxByBitmap);
        long coalesceGapClusters = coalesceGapBytes <= 0
            ? 0
            : (coalesceGapBytes + bytesPerCluster - 1) / bytesPerCluster;

        long runStartCluster = -1;   // 현재 런의 시작 클러스터
        long runEndCluster = -1;     // 현재 런에서 마지막 할당 클러스터 + 1 (배타적)

        for (int byteIdx = 0; byteIdx < bitmap.Length; byteIdx++)
        {
            long baseCluster = (long)byteIdx * 8;
            if (baseCluster >= clusters) break;

            byte b = bitmap[byteIdx];
            if (b == 0) continue; // 전부 빈 클러스터 — 빠르게 건너뜀

            for (int bit = 0; bit < 8; bit++)
            {
                long c = baseCluster + bit;
                if (c >= clusters) break;
                if ((b & (1 << bit)) == 0) continue; // 빈 클러스터

                if (runStartCluster < 0)
                {
                    runStartCluster = c;
                }
                else if (c - runEndCluster > coalesceGapClusters)
                {
                    // 빈틈이 병합 한도보다 크면 이전 런을 닫고 새 런을 시작합니다.
                    AddRun(runs, runStartCluster, runEndCluster, bytesPerCluster, capBytes);
                    runStartCluster = c;
                }

                runEndCluster = c + 1;
            }
        }

        if (runStartCluster >= 0)
            AddRun(runs, runStartCluster, runEndCluster, bytesPerCluster, capBytes);

        return runs;
    }

    private static void AddRun(List<Run> runs, long startCluster, long endCluster, long bpc, long capBytes)
    {
        long offset = startCluster * bpc;
        long end = Math.Min(endCluster * bpc, capBytes);
        if (end <= offset) return; // 상한 밖이면 버립니다.
        runs.Add(new Run(offset, end - offset));
    }
}
