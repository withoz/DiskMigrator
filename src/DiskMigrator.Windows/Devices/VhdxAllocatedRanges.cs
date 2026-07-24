using System.Runtime.Versioning;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// VHDX 파일의 BAT(Block Allocation Table)를 읽어, <b>실제로 할당된</b>(백업이 기록한) 디스크
/// 영역만 골라냅니다.
/// </summary>
/// <remarks>
/// 스마트 백업은 사용 블록만 이미지에 기록하므로, VHDX에서 할당된 블록 = 원본의 사용 영역 +
/// 파티션 테이블입니다. 복원 때 이 영역만 쓰면 빈 공간을 건너뛰어 백업만큼 빨라집니다.
///
/// <para>단순 "0 감지"는 <b>값이 0인 사용 블록</b>(예: 0으로 채워진 파일 영역)을 잘못 건너뛰어
/// 데이터를 깨뜨리므로 쓰지 않습니다. BAT는 백업이 실제로 기록한 블록을 정확히 알려주므로
/// 안전합니다 — 미할당 블록만(한 번도 안 쓰인 자유 공간) 건너뜁니다.</para>
///
/// <para>형식을 못 읽으면 null을 돌려주고, 호출자는 전체 복원으로 안전하게 되돌립니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class VhdxAllocatedRanges
{
    // VHDX 사양의 고정 GUID들.
    private static readonly Guid BatRegionGuid = new("2dc27766-f623-4200-9d64-115e9bfd4a08");
    private static readonly Guid MetadataRegionGuid = new("8b7ca206-4790-4b9a-b8fe-575f050f886e");
    private static readonly Guid FileParametersGuid = new("caa16737-fa36-4d43-b3b6-33f0aa44e76b");
    private static readonly Guid VirtualDiskSizeGuid = new("2fa54224-cd1b-4876-b211-5dbed83bf4b8");
    private static readonly Guid LogicalSectorSizeGuid = new("8141bf1d-a96f-4709-ba47-f233a8faab5f");

    private const uint PayloadBlockFullyPresent = 6;
    private const uint PayloadBlockPartiallyPresent = 7;

    private const uint RegionTableOffset = 0x30000;      // 192 KB
    private const uint RegiSignature = 0x69676572;        // "regi"
    private const ulong MetadataSignature = 0x617461646174656D; // "metadata"

    /// <summary>
    /// 할당된 디스크 영역 목록((오프셋, 길이), 오프셋 순, 인접 병합)을 반환합니다.
    /// VHDX가 아니거나 형식을 못 읽으면 null.
    /// </summary>
    public static List<(long Offset, long Length)>? TryRead(string vhdxPath)
    {
        try
        {
            using var fs = new FileStream(vhdxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            // --- 1) Region Table: BAT·Metadata 영역의 파일 위치를 찾는다. ---
            fs.Position = RegionTableOffset;
            if (br.ReadUInt32() != RegiSignature) return null; // "regi"
            br.ReadUInt32();                                    // checksum
            uint regionCount = br.ReadUInt32();
            br.ReadUInt32();                                    // reserved
            if (regionCount == 0 || regionCount > 2047) return null;

            long batOffset = 0, batLength = 0, metaOffset = 0;
            for (uint i = 0; i < regionCount; i++)
            {
                var guid = new Guid(br.ReadBytes(16));
                long fileOffset = br.ReadInt64();
                uint length = br.ReadUInt32();
                br.ReadUInt32();                                // flags
                if (guid == BatRegionGuid) { batOffset = fileOffset; batLength = length; }
                else if (guid == MetadataRegionGuid) { metaOffset = fileOffset; }
            }
            if (batOffset <= 0 || batLength <= 0 || metaOffset <= 0) return null;

            // --- 2) Metadata: BlockSize, VirtualDiskSize, LogicalSectorSize. ---
            fs.Position = metaOffset;
            if (br.ReadUInt64() != MetadataSignature) return null; // "metadata"
            br.ReadUInt16();                                       // reserved
            ushort entryCount = br.ReadUInt16();
            if (entryCount == 0 || entryCount > 2047) return null;

            fs.Position = metaOffset + 32;                         // 헤더 32바이트 뒤 엔트리 시작
            var entries = new List<(Guid Id, uint Off, uint Len)>(entryCount);
            for (int i = 0; i < entryCount; i++)
            {
                var id = new Guid(br.ReadBytes(16));
                uint off = br.ReadUInt32();
                uint len = br.ReadUInt32();
                br.ReadUInt32();                                  // flags
                br.ReadUInt32();                                  // reserved
                entries.Add((id, off, len));
            }

            uint blockSize = 0, sectorSize = 0;
            ulong virtualSize = 0;
            foreach (var (id, off, _) in entries)
            {
                fs.Position = metaOffset + off;
                if (id == FileParametersGuid) blockSize = br.ReadUInt32();
                else if (id == VirtualDiskSizeGuid) virtualSize = br.ReadUInt64();
                else if (id == LogicalSectorSizeGuid) sectorSize = br.ReadUInt32();
            }
            if (blockSize == 0 || sectorSize == 0 || virtualSize == 0) return null;

            // --- 3) BAT: payload block 상태를 읽어 할당된 블록만 수집. ---
            // BAT는 payload 엔트리 사이에 (ChunkRatio개마다) sector bitmap 엔트리가 끼어 있다.
            long chunkRatio = (1L << 23) * sectorSize / blockSize;
            if (chunkRatio <= 0) return null;
            long blockCount = (long)((virtualSize + blockSize - 1) / blockSize);

            var ranges = new List<(long Offset, long Length)>();
            for (long i = 0; i < blockCount; i++)
            {
                long batIndex = i + i / chunkRatio;               // 끼어든 bitmap 엔트리만큼 밀림
                long entryPos = batOffset + batIndex * 8;
                if (entryPos + 8 > batOffset + batLength) break;

                fs.Position = entryPos;
                ulong entry = br.ReadUInt64();
                uint state = (uint)(entry & 0x7);
                if (state is not (PayloadBlockFullyPresent or PayloadBlockPartiallyPresent)) continue;

                long off = i * blockSize;
                long len = Math.Min(blockSize, (long)virtualSize - off);
                if (len <= 0) continue;

                // 인접 블록은 한 구간으로 병합(IO 조각화 감소).
                if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == off)
                    ranges[^1] = (ranges[^1].Offset, ranges[^1].Length + len);
                else
                    ranges.Add((off, len));
            }

            return ranges.Count > 0 ? ranges : null;
        }
        catch
        {
            return null;
        }
    }
}
