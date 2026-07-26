using System.Buffers.Binary;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Partitioning;

/// <summary>GPT 헤더의 필드 오프셋. UEFI 사양 5.3.1의 GPT Header 구조입니다.</summary>
internal static class GptHeaderOffsets
{
    internal const int Signature = 0;              // 8바이트 "EFI PART"
    internal const int Revision = 8;               // 4
    internal const int HeaderSize = 12;            // 4
    internal const int HeaderCrc32 = 16;           // 4
    internal const int Reserved = 20;              // 4
    internal const int MyLba = 24;                 // 8
    internal const int AlternateLba = 32;          // 8
    internal const int FirstUsableLba = 40;        // 8
    internal const int LastUsableLba = 48;         // 8
    internal const int DiskGuid = 56;              // 16
    internal const int PartitionEntryLba = 72;     // 8
    internal const int NumberOfPartitionEntries = 80; // 4
    internal const int SizeOfPartitionEntry = 84;  // 4
    internal const int PartitionEntryArrayCrc32 = 88; // 4
}

public sealed record GptRepairResult(bool WasRepaired, string Description);

/// <summary>
/// 클론한 대상 디스크의 GPT를 대상 크기에 맞게 보정합니다.
/// </summary>
/// <remarks>
/// GPT는 디스크 <b>끝</b>에 백업 헤더 사본을 둡니다. 섹터 단위로 그대로 복제하면
/// 백업 헤더는 "원본 디스크의 끝"이었던 위치에 놓이는데, 대상이 더 크면
/// 그 자리는 디스크 중간입니다. 그러면:
///
/// - Windows 디스크 관리가 "GPT 백업 헤더가 잘못된 위치에 있다"고 보고하고,
/// - 남는 공간을 쓸 수 없으며,
/// - 일부 UEFI 펌웨어는 부팅을 거부합니다.
///
/// 그래서 백업 헤더를 진짜 끝으로 옮기고, LastUsableLBA와 CRC를 다시 계산합니다.
/// </remarks>
public sealed class GptRepair(ILogger<GptRepair>? logger = null)
{
    private static readonly byte[] GptSignature = Encoding.ASCII.GetBytes("EFI PART");

    private readonly ILogger _logger = logger ?? NullLogger<GptRepair>.Instance;

    /// <summary>
    /// 대상 장치의 GPT를 검사하고, 백업 헤더 위치가 어긋나 있으면 고칩니다.
    /// MBR 디스크나 GPT가 아닌 디스크에서는 아무것도 하지 않습니다.
    /// </summary>
    public GptRepairResult RepairIfNeeded(IBlockDevice target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!target.CanWrite)
        {
            throw new InvalidOperationException(DiskMigrator.Core.Localization.L.T($"{target.Id} 은(는) 쓰기용으로 열려 있지 않습니다.", $"{target.Id} is not open for writing."));
        }

        int sectorSize = target.SectorSize;
        long lastLba = (target.Length / sectorSize) - 1;

        using var headerBuffer = new AlignedBuffer(RoundUp(sectorSize));
        var header = headerBuffer.SpanOf(sectorSize);

        // 주 GPT 헤더는 항상 LBA 1에 있습니다.
        if (target.Read(sectorSize, header) < sectorSize)
        {
            return new GptRepairResult(false, L.T("주 GPT 헤더를 읽지 못했습니다.",
                                                  "Failed to read the primary GPT header."));
        }

        if (!header[..8].SequenceEqual(GptSignature))
        {
            // MBR 디스크이거나 파티션 테이블이 없는 디스크입니다 — 정상적인 경우입니다.
            return new GptRepairResult(false, L.T("GPT 디스크가 아니므로 보정할 것이 없습니다.",
                                                  "Not a GPT disk — nothing to fix."));
        }

        long currentAlternateLba = ReadInt64(header, GptHeaderOffsets.AlternateLba);

        if (currentAlternateLba == lastLba)
        {
            _logger.LogInformation("GPT 백업 헤더가 이미 올바른 위치({Lba})에 있습니다.", lastLba);
            return new GptRepairResult(false, L.T("GPT 백업 헤더 위치가 이미 정확합니다.",
                                                  "The GPT backup header is already in the correct place."));
        }

        _logger.LogInformation(
            "GPT 백업 헤더를 옮깁니다: LBA {Current} → {Correct} (대상이 원본보다 큽니다).",
            currentAlternateLba, lastLba);

        uint headerSize = ReadUInt32(header, GptHeaderOffsets.HeaderSize);
        uint entryCount = ReadUInt32(header, GptHeaderOffsets.NumberOfPartitionEntries);
        uint entrySize = ReadUInt32(header, GptHeaderOffsets.SizeOfPartitionEntry);
        long primaryEntryLba = ReadInt64(header, GptHeaderOffsets.PartitionEntryLba);

        if (headerSize is < 92 or > 512 || entryCount == 0 || entrySize < 128)
        {
            throw new InvalidOperationException(
                DiskMigrator.Core.Localization.L.T($"GPT 헤더 값이 비정상입니다 (HeaderSize={headerSize}, 항목 {entryCount}개 × {entrySize}바이트). 대상 디스크를 신뢰할 수 없습니다.", $"GPT header values are invalid (HeaderSize={headerSize}, {entryCount} entries × {entrySize} bytes). The target disk cannot be trusted."));
        }

        long entryArrayBytes = (long)entryCount * entrySize;
        long entryArrayLbaCount = (entryArrayBytes + sectorSize - 1) / sectorSize;

        // 파티션 항목 배열을 읽습니다 — 백업본에도 그대로 써야 하고 CRC 계산에도 필요합니다.
        int entryBufferSize = (int)(entryArrayLbaCount * sectorSize);
        using var entryBuffer = new AlignedBuffer(RoundUp(entryBufferSize));
        var entries = entryBuffer.SpanOf(entryBufferSize);

        if (target.Read(primaryEntryLba * sectorSize, entries) < entryBufferSize)
        {
            throw new InvalidOperationException(DiskMigrator.Core.Localization.L.T("GPT 파티션 항목 배열을 읽지 못했습니다.", "Failed to read the GPT partition entry array."));
        }

        uint entriesCrc = Crc32.Compute(entries[..(int)entryArrayBytes]);

        // 백업 항목 배열은 백업 헤더 바로 앞에 놓입니다.
        long backupEntryLba = lastLba - entryArrayLbaCount;
        long newLastUsableLba = backupEntryLba - 1;

        long firstUsableLba = ReadInt64(header, GptHeaderOffsets.FirstUsableLba);
        if (newLastUsableLba <= firstUsableLba)
        {
            throw new InvalidOperationException(DiskMigrator.Core.Localization.L.T(
                "대상 디스크가 너무 작아 GPT 백업 헤더를 놓을 자리가 없습니다.",
                "The target disk is too small to hold the GPT backup header."));
        }

        // --- 주 헤더 갱신 ---------------------------------------------------
        WriteInt64(header, GptHeaderOffsets.AlternateLba, lastLba);
        WriteInt64(header, GptHeaderOffsets.LastUsableLba, newLastUsableLba);
        WriteUInt32(header, GptHeaderOffsets.PartitionEntryArrayCrc32, entriesCrc);
        RecomputeHeaderCrc(header, headerSize);

        target.Write(sectorSize, header);

        // --- 백업 항목 배열 -------------------------------------------------
        target.Write(backupEntryLba * sectorSize, entries);

        // --- 백업 헤더 ------------------------------------------------------
        // 백업 헤더는 주 헤더와 같되 MyLBA/AlternateLBA가 뒤바뀌고,
        // PartitionEntryLBA가 백업 항목 배열을 가리킵니다.
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
            "GPT 보정 완료: 백업 헤더 LBA {Lba}, 마지막 사용 가능 LBA {LastUsable}.",
            lastLba, newLastUsableLba);

        return new GptRepairResult(true, L.T(
            $"GPT 백업 헤더를 디스크 끝(LBA {lastLba})으로 옮기고 검사합을 다시 계산했습니다. " +
            "디스크 관리에서 마지막 파티션을 남은 공간까지 확장할 수 있습니다.",
            $"Moved the GPT backup header to the end of the disk (LBA {lastLba}) and recomputed checksums. " +
            "You can extend the last partition into the remaining space in Disk Management."));
    }

    /// <summary>
    /// 보호 MBR의 파티션 항목이 디스크 전체를 덮도록 크기를 갱신합니다.
    /// </summary>
    /// <remarks>
    /// 보호 MBR은 GPT를 모르는 옛 도구가 디스크를 "비어 있다"고 착각해 덮어쓰는 것을
    /// 막는 장치입니다. 크기가 실제 디스크보다 작으면 일부 도구가 뒷부분을
    /// 미사용 공간으로 오인합니다.
    /// </remarks>
    private void RepairProtectiveMbr(IBlockDevice target, long lastLba)
    {
        int sectorSize = target.SectorSize;

        using var mbrBuffer = new AlignedBuffer(RoundUp(sectorSize));
        var mbr = mbrBuffer.SpanOf(sectorSize);

        if (target.Read(0, mbr) < sectorSize) return;

        // 부트 시그니처가 없으면 보호 MBR이 아닙니다.
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return;

        // 첫 파티션 항목은 오프셋 446에서 시작하고, 타입 바이트는 +4 위치입니다.
        const int firstEntry = 446;
        if (mbr[firstEntry + 4] != 0xEE) return; // 0xEE = GPT 보호 파티션

        // 디스크가 2TB를 넘으면 32비트 섹터 수에 담을 수 없으므로 최댓값으로 고정합니다.
        // 이것이 사양이 정한 동작입니다.
        uint sizeInSectors = lastLba >= uint.MaxValue ? uint.MaxValue : (uint)lastLba;

        uint current = BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(firstEntry + 12, 4));
        if (current == sizeInSectors) return;

        BinaryPrimitives.WriteUInt32LittleEndian(mbr.Slice(firstEntry + 12, 4), sizeInSectors);
        target.Write(0, mbr);

        _logger.LogInformation(
            "보호 MBR의 크기를 갱신했습니다: {Old} → {New} 섹터.", current, sizeInSectors);
    }

    private static void RecomputeHeaderCrc(Span<byte> header, uint headerSize)
    {
        // CRC 계산 전에 CRC 필드 자체를 0으로 만들어야 합니다 (UEFI 사양).
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
