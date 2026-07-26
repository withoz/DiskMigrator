using System.Buffers.Binary;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Partitioning;

/// <summary>MBR 파티션 항목(16바이트) 안의 자리.</summary>
internal static class MbrEntryOffsets
{
    internal const int BootIndicator = 0;   // 1  0x80이면 활성(부팅) 파티션
    internal const int ChsFirst = 1;        // 3  헤드/섹터/실린더 — 요즘 OS는 LBA를 씁니다
    internal const int PartitionType = 4;   // 1  0x07 NTFS, 0x27 복구, 0x05·0x0F 확장
    internal const int ChsLast = 5;         // 3
    internal const int StartLba = 8;        // 4  32비트 — 이것이 2 TB 한계의 원인
    internal const int SectorCount = 12;    // 4
}

public sealed record MbrRewriteResult(bool Rewritten, string Description);

/// <summary>
/// MBR 디스크의 파티션 테이블을 새 배치로 다시 씁니다(<b>주 파티션 전용</b>).
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가:</b> 리사이즈 클론은 고른 파티션을 넓히고 그 뒤 파티션들을 오른쪽으로
/// 밉니다. 데이터는 새 위치에 복사되지만 파티션 테이블은 여전히 옛 위치를 가리키므로,
/// 테이블을 고쳐 주지 않으면 디스크를 읽을 수 없습니다. GPT는 <see cref="GptRewriter"/>가
/// 하고, 이 클래스가 MBR을 맡습니다.
/// </para>
/// <para>
/// <b>건드리는 범위는 446~509바이트뿐입니다.</b> 앞의 0~445바이트에는 부트 코드와
/// <b>NT 디스크 서명</b>(0x1B8)이 있습니다. Windows의 BCD는 "디스크 서명 + 파티션 오프셋"으로
/// 부팅 볼륨을 찾으므로, 서명이 바뀌면 클론한 디스크가 부팅하지 못합니다. 부팅 표시(0x80)와
/// 파티션 타입도 원본 값을 그대로 둡니다.
/// </para>
/// <para>
/// <b>확장 파티션(0x05·0x0F)은 거절합니다.</b> 논리 드라이브는 EBR 체인으로 이어지고 그 안의
/// 오프셋이 상대값이라, 옮기려면 모든 EBR을 함께 다시 써야 합니다. 반만 고치면 체인이 끊겨
/// 디스크를 통째로 못 읽게 되므로, 시작 전에 명확히 거절하는 편이 안전합니다.
/// </para>
/// </remarks>
public sealed class MbrRewriter(ILogger<MbrRewriter>? logger = null)
{
    private const int TableOffset = 446;
    private const int EntrySize = 16;
    private const int EntryCount = 4;

    /// <summary>MBR의 LBA 필드는 32비트라 이 섹터 수를 넘는 위치는 가리킬 수 없습니다.</summary>
    private const long MaxLba = uint.MaxValue;

    private readonly ILogger _logger = logger ?? NullLogger<MbrRewriter>.Instance;

    /// <summary>
    /// 대상의 MBR 파티션 항목들을 <paramref name="remaps"/>대로 옮깁니다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// MBR이 아니거나, 확장 파티션이 있거나, 사용 중 항목에 대응 remap이 없거나,
    /// 새 배치가 32비트 LBA 한계나 대상 크기를 넘을 때.
    /// </exception>
    public MbrRewriteResult Rewrite(IBlockDevice target, IReadOnlyList<PartitionRemap> remaps)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(remaps);

        if (!target.CanWrite)
            throw new InvalidOperationException($"{target.Id} 은(는) 쓰기용으로 열려 있지 않습니다.");

        int sectorSize = target.SectorSize;
        long lastLba = (target.Length / sectorSize) - 1;

        using var buffer = new AlignedBuffer(RoundUp(sectorSize));
        var mbr = buffer.SpanOf(sectorSize);

        if (target.Read(0, mbr) < sectorSize)
            throw new InvalidOperationException("MBR을 읽지 못했습니다.");

        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            throw new InvalidOperationException("대상에 MBR 서명(0x55AA)이 없습니다.");

        // 보호 MBR(0xEE)은 GPT 디스크의 껍데기입니다. 여기까지 왔다면 배선이 잘못된 것이므로
        // 파티션 테이블을 덮어쓰기 전에 멈춥니다 — GPT를 MBR로 잘못 쓰면 디스크를 못 읽습니다.
        if (mbr[TableOffset + MbrEntryOffsets.PartitionType] == 0xEE)
            throw new InvalidOperationException("대상이 GPT 디스크입니다(보호 MBR). MBR 재작성을 적용할 수 없습니다.");

        var applied = new bool[remaps.Count];
        long maxNewEndLba = 0;
        int movedCount = 0;

        for (int i = 0; i < EntryCount; i++)
        {
            var entry = mbr.Slice(TableOffset + (i * EntrySize), EntrySize);

            byte type = entry[MbrEntryOffsets.PartitionType];
            uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(
                entry.Slice(MbrEntryOffsets.SectorCount, 4));

            // 타입 0 또는 길이 0이면 빈 슬롯입니다.
            if (type == 0 || sectorCount == 0) continue;

            if (type is 0x05 or 0x0F)
            {
                throw new InvalidOperationException(
                    "원본에 확장 파티션(논리 드라이브)이 있어 리사이즈할 수 없습니다. " +
                    "논리 드라이브는 EBR 체인으로 이어져 있어 옮기려면 체인 전체를 다시 써야 합니다. " +
                    "'마지막 파티션에 합치기'나 '그대로 둡니다'를 쓰십시오.");
            }

            long startLba = BinaryPrimitives.ReadUInt32LittleEndian(
                entry.Slice(MbrEntryOffsets.StartLba, 4));

            int match = FindRemap(remaps, applied, startLba);
            if (match < 0)
                throw new InvalidOperationException(
                    $"MBR에 사용 중인 파티션(시작 LBA {startLba})이 있는데 대응하는 재배치 정보가 없습니다. " +
                    "모든 원본 파티션을 배치에 포함해야 안전하게 리사이즈할 수 있습니다.");

            var remap = remaps[match];
            applied[match] = true;

            long newCount = remap.NewEndLba - remap.NewStartLba + 1;

            // 0번 섹터는 MBR 자신입니다. 파티션이 거기서 시작하면 파티션 테이블을 덮어씁니다.
            if (remap.NewStartLba < 1)
                throw new InvalidOperationException(
                    $"파티션 시작 LBA가 {remap.NewStartLba}입니다 — 0번 섹터는 MBR 자신이라 쓸 수 없습니다.");

            // 시작·길이 모두 32비트 필드입니다. 길이도 함께 봐야 합니다 — 끝 LBA만 검사하면
            // 길이가 2^32가 되는 경우 캐스트에서 0으로 잘려 '길이 0' 항목이 조용히 써집니다.
            if (remap.NewEndLba > MaxLba || newCount <= 0 || newCount > MaxLba)
                throw new InvalidOperationException(
                    $"새 배치가 MBR의 32비트 한계를 넘습니다(끝 LBA {remap.NewEndLba}, 길이 {newCount}섹터). " +
                    "MBR 디스크는 약 2 TB까지만 가리킬 수 있습니다.");

            BinaryPrimitives.WriteUInt32LittleEndian(
                entry.Slice(MbrEntryOffsets.StartLba, 4), (uint)remap.NewStartLba);
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry.Slice(MbrEntryOffsets.SectorCount, 4), (uint)newCount);

            // CHS는 요즘 OS가 쓰지 않지만, 남겨 두면 LBA와 어긋난 값이 됩니다. 일부 부트 코드와
            // 파티션 도구가 여전히 읽으므로 새 위치에 맞춰 다시 계산합니다.
            WriteChs(entry.Slice(MbrEntryOffsets.ChsFirst, 3), remap.NewStartLba);
            WriteChs(entry.Slice(MbrEntryOffsets.ChsLast, 3), remap.NewEndLba);

            maxNewEndLba = Math.Max(maxNewEndLba, remap.NewEndLba);
            movedCount++;
        }

        for (int i = 0; i < remaps.Count; i++)
        {
            if (!applied[i])
                throw new InvalidOperationException(
                    $"재배치 정보(시작 LBA {remaps[i].OldStartLba})에 해당하는 MBR 파티션을 찾지 못했습니다.");
        }

        if (maxNewEndLba > lastLba)
            throw new InvalidOperationException(
                $"새 파티션 배치가 대상 디스크를 넘습니다(마지막 파티션 끝 LBA {maxNewEndLba} > " +
                $"디스크 마지막 LBA {lastLba}).");

        // 파티션 테이블을 쓰기 직전 마지막 방어선입니다. 겹친 배치를 쓰면 두 파일시스템이
        // 같은 섹터를 자기 것으로 알고 서로를 덮어써, 디스크를 통째로 못 읽게 됩니다.
        // 배치는 ResizePlanner가 계산하고 테스트도 있지만, 되돌릴 수 없는 쓰기 앞에서는
        // 한 겹 더 봅니다.
        var ordered = remaps.OrderBy(r => r.NewStartLba).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].NewStartLba <= ordered[i - 1].NewEndLba)
                throw new InvalidOperationException(
                    $"새 파티션 배치가 서로 겹칩니다(앞 파티션 끝 LBA {ordered[i - 1].NewEndLba}, " +
                    $"뒤 파티션 시작 LBA {ordered[i].NewStartLba}). 파티션 테이블을 쓰지 않았습니다.");
        }

        target.Write(0, mbr);
        target.Flush();

        _logger.LogInformation(
            "MBR 재작성 완료: 파티션 {Moved}개 재배치, 마지막 끝 LBA {End} (디스크 마지막 {Last}). " +
            "부트 코드·디스크 서명·부팅 표시는 그대로 두었습니다.",
            movedCount, maxNewEndLba, lastLba);

        return new MbrRewriteResult(true, L.T(
            $"MBR 파티션 테이블을 새 배치로 다시 썼습니다(파티션 {movedCount}개, 디스크 서명 보존).",
            $"Rewrote the MBR partition table for the new layout ({movedCount} partition(s), disk signature preserved)."));
    }

    /// <summary>아직 쓰이지 않은 remap 중 OldStartLba가 일치하는 것을 찾습니다.</summary>
    private static int FindRemap(IReadOnlyList<PartitionRemap> remaps, bool[] applied, long startLba)
    {
        for (int i = 0; i < remaps.Count; i++)
        {
            if (!applied[i] && remaps[i].OldStartLba == startLba) return i;
        }
        return -1;
    }

    /// <summary>
    /// LBA를 CHS 3바이트로 적습니다. 표준 기하(헤드 255 × 트랙당 섹터 63)를 씁니다.
    /// </summary>
    /// <remarks>
    /// CHS로 표현할 수 있는 한계(약 8 GB)를 넘으면 관례대로 0xFE/0xFF/0xFF를 적습니다 —
    /// "CHS로는 못 가리키니 LBA를 보라"는 뜻이며, 모든 현대 도구가 이렇게 합니다.
    /// </remarks>
    private static void WriteChs(Span<byte> chs, long lba)
    {
        const int headsPerCylinder = 255;
        const int sectorsPerTrack = 63;
        const long maxChsLba = 1024L * headsPerCylinder * sectorsPerTrack;

        if (lba >= maxChsLba)
        {
            chs[0] = 0xFE;
            chs[1] = 0xFF;
            chs[2] = 0xFF;
            return;
        }

        long cylinder = lba / (headsPerCylinder * sectorsPerTrack);
        long temp = lba % (headsPerCylinder * sectorsPerTrack);
        long head = temp / sectorsPerTrack;
        long sector = (temp % sectorsPerTrack) + 1;   // 섹터는 1부터 셉니다

        chs[0] = (byte)head;
        chs[1] = (byte)(sector | ((cylinder >> 2) & 0xC0));   // 실린더 상위 2비트가 섹터 바이트에 얹힙니다
        chs[2] = (byte)(cylinder & 0xFF);
    }

    private static int RoundUp(int size) => Math.Max(4096, (size + 4095) / 4096 * 4096);
}
