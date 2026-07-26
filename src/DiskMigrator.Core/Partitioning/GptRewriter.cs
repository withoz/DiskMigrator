using System.Buffers.Binary;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Partitioning;

/// <summary>GPT 파티션 엔트리의 필드 오프셋(UEFI 사양 5.3.3, 엔트리당 128바이트 기준).</summary>
internal static class GptEntryOffsets
{
    internal const int TypeGuid = 0;      // 16바이트. 전부 0이면 미사용 슬롯.
    internal const int UniqueGuid = 16;   // 16
    internal const int StartingLba = 32;  // 8
    internal const int EndingLba = 40;    // 8 (마지막 LBA, 포함)
    internal const int Attributes = 48;   // 8
    internal const int Name = 56;         // 72 (UTF-16)
}

/// <summary>한 파티션을 옛 위치에서 새 위치로 옮기라는 지시(LBA 단위).</summary>
/// <param name="OldStartLba">현재 GPT 엔트리의 StartingLBA(대응을 찾는 열쇠).</param>
/// <param name="NewStartLba">새 StartingLBA.</param>
/// <param name="NewEndLba">새 EndingLBA(마지막 LBA, 포함).</param>
public sealed record PartitionRemap(long OldStartLba, long NewStartLba, long NewEndLba);

public sealed record GptRewriteResult(bool Rewritten, string Description);

/// <summary>
/// 대상 디스크의 GPT를 새 파티션 배치에 맞게 다시 씁니다(확대 리사이즈용).
/// </summary>
/// <remarks>
/// 리사이즈 클론은 원본 GPT를 대상에 그대로 복제한 다음(엔트리의 <b>타입 GUID·고유 GUID·
/// 속성·이름</b>이 온전히 담김) 이 클래스로 각 엔트리의 <b>StartingLBA/EndingLBA만</b>
/// 새 위치로 고칩니다. 고유 GUID를 보존하는 것이 핵심입니다 — BCD와 부팅 구성이 파티션을
/// GUID로 참조하므로, GUID가 바뀌면 클론이 부팅되지 않습니다.
///
/// 엔트리 배열을 고친 뒤에는 주 헤더·백업 엔트리 배열·백업 헤더의 CRC를 모두 다시 계산하고
/// 백업 헤더를 디스크 끝으로 옮깁니다(<see cref="GptRepair"/>가 하는 일을 포함). 대상이 반드시
/// 쓰기용으로 열려 있어야 합니다.
///
/// <see cref="GptRepair"/>와 겹치는 로직(백업 재작성·보호 MBR·CRC)이 있지만, 이미 출시돼
/// 검증된 그 코드를 건드리지 않도록 의도적으로 분리했습니다.
/// </remarks>
public sealed class GptRewriter(ILogger<GptRewriter>? logger = null)
{
    private static readonly byte[] GptSignature = Encoding.ASCII.GetBytes("EFI PART");

    private readonly ILogger _logger = logger ?? NullLogger<GptRewriter>.Instance;

    /// <summary>
    /// 대상의 GPT 엔트리들을 <paramref name="remaps"/>대로 옮기고 헤더·백업·CRC를 갱신합니다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// GPT가 아니거나, 사용 중 엔트리에 대응 remap이 없거나, 새 배치가 대상에 맞지 않을 때.
    /// </exception>
    public GptRewriteResult Rewrite(IBlockDevice target, IReadOnlyList<PartitionRemap> remaps)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(remaps);

        if (!target.CanWrite)
            throw new InvalidOperationException($"{target.Id} 은(는) 쓰기용으로 열려 있지 않습니다.");

        int sectorSize = target.SectorSize;
        long lastLba = (target.Length / sectorSize) - 1;

        using var headerBuffer = new AlignedBuffer(RoundUp(sectorSize));
        var header = headerBuffer.SpanOf(sectorSize);

        if (target.Read(sectorSize, header) < sectorSize)
            throw new InvalidOperationException("주 GPT 헤더를 읽지 못했습니다.");

        if (!header[..8].SequenceEqual(GptSignature))
            throw new InvalidOperationException("대상이 GPT 디스크가 아닙니다. 리사이즈에는 GPT가 필요합니다.");

        uint headerSize = ReadUInt32(header, GptHeaderOffsets.HeaderSize);
        uint entryCount = ReadUInt32(header, GptHeaderOffsets.NumberOfPartitionEntries);
        uint entrySize = ReadUInt32(header, GptHeaderOffsets.SizeOfPartitionEntry);
        long primaryEntryLba = ReadInt64(header, GptHeaderOffsets.PartitionEntryLba);
        long firstUsableLba = ReadInt64(header, GptHeaderOffsets.FirstUsableLba);

        if (headerSize is < 92 or > 512 || entryCount == 0 || entrySize < 128)
            throw new InvalidOperationException(
                $"GPT 헤더 값이 비정상입니다 (HeaderSize={headerSize}, 항목 {entryCount}개 × {entrySize}바이트).");

        long entryArrayBytes = (long)entryCount * entrySize;
        long entryArrayLbaCount = (entryArrayBytes + sectorSize - 1) / sectorSize;

        int entryBufferSize = (int)(entryArrayLbaCount * sectorSize);
        using var entryBuffer = new AlignedBuffer(RoundUp(entryBufferSize));
        var entries = entryBuffer.SpanOf(entryBufferSize);

        if (target.Read(primaryEntryLba * sectorSize, entries) < entryBufferSize)
            throw new InvalidOperationException("GPT 파티션 항목 배열을 읽지 못했습니다.");

        // --- 엔트리 재배치 --------------------------------------------------
        var applied = new bool[remaps.Count];
        long maxNewEndLba = 0;
        int movedCount = 0;

        for (uint i = 0; i < entryCount; i++)
        {
            int baseOff = (int)(i * entrySize);
            var entry = entries.Slice(baseOff, (int)entrySize);

            // 타입 GUID가 전부 0이면 미사용 슬롯.
            if (IsAllZero(entry.Slice(GptEntryOffsets.TypeGuid, 16))) continue;

            long startLba = ReadInt64(entry, GptEntryOffsets.StartingLba);

            int match = FindRemap(remaps, applied, startLba);
            if (match < 0)
                throw new InvalidOperationException(
                    $"GPT에 사용 중인 파티션(StartingLBA {startLba})이 있는데 대응하는 재배치 정보가 없습니다. " +
                    "모든 원본 파티션을 배치에 포함해야 안전하게 리사이즈할 수 있습니다.");

            var remap = remaps[match];
            applied[match] = true;

            WriteInt64(entry, GptEntryOffsets.StartingLba, remap.NewStartLba);
            WriteInt64(entry, GptEntryOffsets.EndingLba, remap.NewEndLba);
            maxNewEndLba = Math.Max(maxNewEndLba, remap.NewEndLba);
            movedCount++;
        }

        for (int i = 0; i < remaps.Count; i++)
        {
            if (!applied[i])
                throw new InvalidOperationException(
                    $"재배치 정보(StartingLBA {remaps[i].OldStartLba})에 해당하는 GPT 파티션을 찾지 못했습니다.");
        }

        // --- 백업 위치·사용 가능 경계 계산 (GptRepair와 동일 규칙) -----------
        long backupEntryLba = lastLba - entryArrayLbaCount;
        long newLastUsableLba = backupEntryLba - 1;

        if (newLastUsableLba <= firstUsableLba)
            throw new InvalidOperationException("대상이 너무 작아 GPT 백업 헤더를 놓을 자리가 없습니다.");

        if (maxNewEndLba > newLastUsableLba)
            throw new InvalidOperationException(
                $"새 파티션 배치가 사용 가능 영역을 넘습니다(마지막 파티션 끝 LBA {maxNewEndLba} > " +
                $"마지막 사용 가능 LBA {newLastUsableLba}).");

        uint entriesCrc = Crc32.Compute(entries[..(int)entryArrayBytes]);

        // --- 주 헤더 갱신 ---------------------------------------------------
        WriteInt64(header, GptHeaderOffsets.AlternateLba, lastLba);
        WriteInt64(header, GptHeaderOffsets.LastUsableLba, newLastUsableLba);
        WriteUInt32(header, GptHeaderOffsets.PartitionEntryArrayCrc32, entriesCrc);
        RecomputeHeaderCrc(header, headerSize);

        target.Write(sectorSize, header);
        target.Write(primaryEntryLba * sectorSize, entries);

        // --- 백업 엔트리 배열 + 백업 헤더 -----------------------------------
        target.Write(backupEntryLba * sectorSize, entries);

        using var backupBuffer = new AlignedBuffer(RoundUp(sectorSize));
        var backupHeader = backupBuffer.SpanOf(sectorSize);
        header.CopyTo(backupHeader);

        WriteInt64(backupHeader, GptHeaderOffsets.MyLba, lastLba);
        WriteInt64(backupHeader, GptHeaderOffsets.AlternateLba, 1);
        WriteInt64(backupHeader, GptHeaderOffsets.PartitionEntryLba, backupEntryLba);
        RecomputeHeaderCrc(backupHeader, headerSize);

        target.Write(lastLba * sectorSize, backupHeader);

        // --- 보호 MBR -------------------------------------------------------
        RepairProtectiveMbr(target, lastLba);

        target.Flush();

        _logger.LogInformation(
            "GPT 재작성 완료: 파티션 {Moved}개 재배치, 마지막 사용 가능 LBA {LastUsable}, 백업 헤더 LBA {Backup}.",
            movedCount, newLastUsableLba, lastLba);

        return new GptRewriteResult(true, L.T(
            $"GPT를 새 배치로 다시 썼습니다(파티션 {movedCount}개, 고유 GUID 보존).",
            $"Rewrote the GPT for the new layout ({movedCount} partition(s), unique GUIDs preserved)."));
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

    /// <summary>보호 MBR의 파티션 항목이 디스크 전체를 덮도록 크기를 갱신합니다.</summary>
    private void RepairProtectiveMbr(IBlockDevice target, long lastLba)
    {
        int sectorSize = target.SectorSize;

        using var mbrBuffer = new AlignedBuffer(RoundUp(sectorSize));
        var mbr = mbrBuffer.SpanOf(sectorSize);

        if (target.Read(0, mbr) < sectorSize) return;
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return;

        const int firstEntry = 446;
        if (mbr[firstEntry + 4] != 0xEE) return;

        uint sizeInSectors = lastLba >= uint.MaxValue ? uint.MaxValue : (uint)lastLba;
        uint current = BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(firstEntry + 12, 4));
        if (current == sizeInSectors) return;

        BinaryPrimitives.WriteUInt32LittleEndian(mbr.Slice(firstEntry + 12, 4), sizeInSectors);
        target.Write(0, mbr);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
            if (b != 0) return false;
        return true;
    }

    private static void RecomputeHeaderCrc(Span<byte> header, uint headerSize)
    {
        WriteUInt32(header, GptHeaderOffsets.HeaderCrc32, 0);
        uint crc = Crc32.Compute(header[..(int)headerSize]);
        WriteUInt32(header, GptHeaderOffsets.HeaderCrc32, crc);
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

    private static void WriteInt64(Span<byte> span, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), value);

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), value);

    private static int RoundUp(int size) => Math.Max(4096, (size + 4095) / 4096 * 4096);
}
