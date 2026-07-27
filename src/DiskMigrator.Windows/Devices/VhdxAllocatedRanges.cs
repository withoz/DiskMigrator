using System.Runtime.Versioning;
using System.Text;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// VHDX 파일의 BAT(Block Allocation Table)를 읽어, <b>실제로 할당된</b>(백업이 기록한) 디스크
/// 영역만 골라냅니다. 차등(differencing) 이미지면 <b>부모 사슬 전체</b>의 할당 영역을 합칩니다.
/// </summary>
/// <remarks>
/// 스마트 백업은 사용 블록만 이미지에 기록하므로, VHDX에서 할당된 블록 = 원본의 사용 영역 +
/// 파티션 테이블입니다. 복원 때 이 영역만 쓰면 빈 공간을 건너뛰어 백업만큼 빨라집니다.
///
/// <para>단순 "0 감지"는 <b>값이 0인 사용 블록</b>(예: 0으로 채워진 파일 영역)을 잘못 건너뛰어
/// 데이터를 깨뜨리므로 쓰지 않습니다. BAT는 백업이 실제로 기록한 블록을 정확히 알려주므로
/// 안전합니다 — 미할당 블록만(한 번도 안 쓰인 자유 공간) 건너뜁니다.</para>
///
/// <para><b>차등 이미지(증분 백업) 주의</b>: 자식 파일의 BAT에는 <b>변경분만</b> 할당돼
/// 있습니다. 자식 하나의 BAT로 복원 범위를 정하면 부모에만 있는 데이터가 통째로 빠집니다 —
/// 실기 검증에서 실제로 발견된 결함입니다. 그래서 부모 위치 정보(parent locator)를 따라
/// 사슬을 끝까지 걸어 <b>모든 구성원의 할당 영역을 합집합</b>합니다. 읽기는 병합 뷰(부착된
/// 자식)에서 하므로 데이터는 항상 최신 내용입니다.</para>
///
/// <para>형식을 못 읽거나 사슬이 불완전하면 null을 돌려주고, 호출자는 전체 복원으로 안전하게
/// 되돌립니다.</para>
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
    private static readonly Guid ParentLocatorGuid = new("a8d35f2d-b30b-454d-abf7-d3d84834ab0c");
    private static readonly Guid VhdxParentLocatorType = new("b04aefb7-d19e-4a81-b789-25b8e9445913");

    private const uint PayloadBlockFullyPresent = 6;
    private const uint PayloadBlockPartiallyPresent = 7;
    private const uint FileParamsHasParentFlag = 0x2;

    private const uint RegionTableOffset = 0x30000;      // 192 KB
    private const uint RegiSignature = 0x69676572;        // "regi"
    private const ulong MetadataSignature = 0x617461646174656D; // "metadata"

    /// <summary>
    /// 할당된 디스크 영역 목록((오프셋, 길이), 오프셋 순, 병합됨)을 반환합니다. 차등 이미지면
    /// 부모 사슬 전체의 합집합입니다. VHDX가 아니거나 형식·사슬을 못 읽으면 null.
    /// </summary>
    public static List<(long Offset, long Length)>? TryRead(string vhdxPath)
    {
        try
        {
            var union = new List<(long Offset, long Length)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = Path.GetFullPath(vhdxPath);

            while (current is not null)
            {
                if (!visited.Add(current) || visited.Count > 64) return null; // 순환·비정상 깊이
                var single = ReadSingle(current, out string? parentPath, out bool hasParent);
                if (single is null) return null;
                union.AddRange(single);

                if (hasParent && parentPath is null) return null; // 부모가 있는데 못 찾음 → 전체 복원
                current = parentPath;
            }

            var merged = Merge(union);
            return merged.Count > 0 ? merged : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>한 파일의 할당 영역과 부모 경로를 읽습니다(사슬은 걷지 않음).</summary>
    private static List<(long Offset, long Length)>? ReadSingle(
        string vhdxPath, out string? parentPath, out bool hasParent)
    {
        parentPath = null;
        hasParent = false;

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

        // --- 2) Metadata: BlockSize·HasParent, VirtualDiskSize, LogicalSectorSize, ParentLocator. ---
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
        foreach (var (id, off, len) in entries)
        {
            fs.Position = metaOffset + off;
            if (id == FileParametersGuid)
            {
                blockSize = br.ReadUInt32();
                hasParent = (br.ReadUInt32() & FileParamsHasParentFlag) != 0;
            }
            else if (id == VirtualDiskSizeGuid) virtualSize = br.ReadUInt64();
            else if (id == LogicalSectorSizeGuid) sectorSize = br.ReadUInt32();
            else if (id == ParentLocatorGuid)
                parentPath = TryReadParentLocator(fs, br, metaOffset + off, vhdxPath);
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

            if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == off)
                ranges[^1] = (ranges[^1].Offset, ranges[^1].Length + len);
            else
                ranges.Add((off, len));
        }

        return ranges;
    }

    /// <summary>
    /// Parent Locator 메타데이터에서 부모 파일 경로를 해석합니다.
    /// relative_path(자식 기준 상대 경로)를 우선하고, 절대 경로로 보완합니다.
    /// </summary>
    private static string? TryReadParentLocator(FileStream fs, BinaryReader br, long locatorStart, string childPath)
    {
        try
        {
            fs.Position = locatorStart;
            var type = new Guid(br.ReadBytes(16));
            if (type != VhdxParentLocatorType) return null;
            br.ReadUInt16();                       // reserved
            ushort kvCount = br.ReadUInt16();
            if (kvCount == 0 || kvCount > 64) return null;

            var kvs = new List<(uint KeyOff, uint ValOff, ushort KeyLen, ushort ValLen)>(kvCount);
            for (int i = 0; i < kvCount; i++)
                kvs.Add((br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt16(), br.ReadUInt16()));

            string? relative = null, absolute = null, volume = null;
            foreach (var (keyOff, valOff, keyLen, valLen) in kvs)
            {
                fs.Position = locatorStart + keyOff;
                string key = Encoding.Unicode.GetString(br.ReadBytes(keyLen));
                fs.Position = locatorStart + valOff;
                string value = Encoding.Unicode.GetString(br.ReadBytes(valLen));

                switch (key)
                {
                    case "relative_path": relative = value; break;
                    case "absolute_win32_path": absolute = value; break;
                    case "volume_path": volume = value; break;
                }
            }

            string? dir = Path.GetDirectoryName(childPath);
            foreach (string? candidate in new[]
            {
                relative is not null && dir is not null ? Path.GetFullPath(Path.Combine(dir, relative)) : null,
                absolute?.StartsWith(@"\\?\", StringComparison.Ordinal) == true ? absolute[4..] : absolute,
                volume,
            })
            {
                if (candidate is not null && File.Exists(candidate)) return candidate;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>겹치거나 맞닿은 구간을 병합해 오프셋 순으로 돌려줍니다.</summary>
    private static List<(long Offset, long Length)> Merge(List<(long Offset, long Length)> ranges)
    {
        if (ranges.Count <= 1) return ranges;
        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var merged = new List<(long Offset, long Length)> { ranges[0] };
        foreach (var r in ranges.Skip(1))
        {
            var last = merged[^1];
            if (r.Offset <= last.Offset + last.Length)
            {
                long end = Math.Max(last.Offset + last.Length, r.Offset + r.Length);
                merged[^1] = (last.Offset, end - last.Offset);
            }
            else
            {
                merged.Add(r);
            }
        }
        return merged;
    }
}
