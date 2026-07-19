using System.Buffers.Binary;
using System.Text;
using DiskMigrator.Core.Partitioning;

namespace DiskMigrator.Core.Tests.Fakes;

/// <summary>
/// 테스트용으로 유효한 GPT 디스크 이미지를 메모리에 만듭니다.
/// </summary>
/// <remarks>
/// GPT 보정 로직은 실제 디스크에서만 검증하기엔 너무 위험하고 느립니다.
/// 사양대로 만든 합성 이미지로 검사합·오프셋·백업 헤더 위치를 정확히 확인합니다.
/// </remarks>
public static class GptImageBuilder
{
    public const int SectorSize = 512;
    public const int EntryCount = 128;
    public const int EntrySize = 128;

    /// <summary>파티션 항목 배열이 차지하는 LBA 수 (128 × 128B ÷ 512B = 32).</summary>
    public const int EntryArrayLbaCount = EntryCount * EntrySize / SectorSize;

    /// <summary>첫 사용 가능 LBA: 보호 MBR(0) + 헤더(1) + 항목 배열(2~33) 다음.</summary>
    public const long FirstUsableLba = 2 + EntryArrayLbaCount;

    /// <summary>Basic Data 파티션 타입 GUID.</summary>
    public static readonly Guid BasicDataType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    /// <summary>이미지에 넣을 파티션 하나의 명세.</summary>
    /// <param name="FirstLba">시작 LBA.</param>
    /// <param name="LastLba">끝 LBA(포함).</param>
    /// <param name="Unique">고유 파티션 GUID(null이면 무작위 생성).</param>
    /// <param name="Type">타입 GUID(null이면 Basic Data).</param>
    /// <param name="Name">파티션 이름.</param>
    public sealed record PartitionSpec(
        long FirstLba, long LastLba, Guid? Unique = null, Guid? Type = null, string Name = "Part");

    /// <summary>
    /// 파티션 하나(디스크를 가득 채움)를 가진 유효한 GPT 디스크 이미지를 만듭니다.
    /// </summary>
    /// <param name="sizeBytes">디스크 전체 크기. 섹터 크기의 배수여야 합니다.</param>
    public static byte[] Build(long sizeBytes)
    {
        long lastUsable = (sizeBytes / SectorSize - 1) - EntryArrayLbaCount - 1;
        return Build(sizeBytes, new PartitionSpec(FirstUsableLba, lastUsable, Name: "Test Partition"));
    }

    /// <summary>
    /// 지정한 파티션들을 가진 유효한 GPT 디스크 이미지를 만듭니다.
    /// </summary>
    public static byte[] Build(long sizeBytes, params PartitionSpec[] parts)
    {
        if (sizeBytes % SectorSize != 0)
            throw new ArgumentException("크기는 섹터 크기의 배수여야 합니다.", nameof(sizeBytes));
        if (parts.Length == 0)
            throw new ArgumentException("파티션이 하나 이상 필요합니다.", nameof(parts));
        if (parts.Length > EntryCount)
            throw new ArgumentException($"파티션은 최대 {EntryCount}개입니다.", nameof(parts));

        var image = new byte[sizeBytes];
        long lastLba = (sizeBytes / SectorSize) - 1;
        long backupEntryLba = lastLba - EntryArrayLbaCount;
        long lastUsableLba = backupEntryLba - 1;

        WriteProtectiveMbr(image, lastLba);

        // --- 파티션 항목 배열 ---
        var entries = new byte[EntryCount * EntrySize];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            WritePartitionEntry(
                entries.AsSpan(i * EntrySize, EntrySize),
                typeGuid: p.Type ?? BasicDataType,
                partitionGuid: p.Unique ?? Guid.NewGuid(),
                firstLba: p.FirstLba,
                lastLba: p.LastLba,
                name: p.Name);
        }

        uint entriesCrc = Crc32.Compute(entries);

        entries.CopyTo(image.AsSpan((int)(2 * SectorSize)));
        entries.CopyTo(image.AsSpan((int)(backupEntryLba * SectorSize)));

        var diskGuid = Guid.NewGuid();

        // --- 주 헤더 (LBA 1) ---
        var primary = BuildHeader(
            myLba: 1, alternateLba: lastLba, firstUsableLba: FirstUsableLba,
            lastUsableLba: lastUsableLba, diskGuid: diskGuid,
            partitionEntryLba: 2, entriesCrc: entriesCrc);

        primary.CopyTo(image.AsSpan(SectorSize));

        // --- 백업 헤더 (마지막 LBA) ---
        var backup = BuildHeader(
            myLba: lastLba, alternateLba: 1, firstUsableLba: FirstUsableLba,
            lastUsableLba: lastUsableLba, diskGuid: diskGuid,
            partitionEntryLba: backupEntryLba, entriesCrc: entriesCrc);

        backup.CopyTo(image.AsSpan((int)(lastLba * SectorSize)));

        return image;
    }

    private static void WriteProtectiveMbr(Span<byte> image, long lastLba)
    {
        const int firstEntry = 446;

        image[firstEntry + 0] = 0x00;                  // 부팅 표시자 없음
        image[firstEntry + 1] = 0x00;                  // 시작 헤드
        image[firstEntry + 2] = 0x02;                  // 시작 섹터
        image[firstEntry + 3] = 0x00;                  // 시작 실린더
        image[firstEntry + 4] = 0xEE;                  // GPT 보호 파티션 타입
        image[firstEntry + 5] = 0xFF;                  // 끝 CHS (관례상 최댓값)
        image[firstEntry + 6] = 0xFF;
        image[firstEntry + 7] = 0xFF;

        BinaryPrimitives.WriteUInt32LittleEndian(image.Slice(firstEntry + 8, 4), 1); // 시작 LBA
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.Slice(firstEntry + 12, 4),
            lastLba >= uint.MaxValue ? uint.MaxValue : (uint)lastLba);

        image[510] = 0x55;
        image[511] = 0xAA;
    }

    private static byte[] BuildHeader(
        long myLba, long alternateLba, long firstUsableLba, long lastUsableLba,
        Guid diskGuid, long partitionEntryLba, uint entriesCrc)
    {
        var sector = new byte[SectorSize];
        var header = sector.AsSpan();

        Encoding.ASCII.GetBytes("EFI PART").CopyTo(header[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 0x00010000); // 리비전 1.0
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), 92);        // HeaderSize
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 0);         // CRC 자리 (나중에)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), 0);         // 예약
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(24, 8), myLba);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(32, 8), alternateLba);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(40, 8), firstUsableLba);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(48, 8), lastUsableLba);
        diskGuid.ToByteArray().CopyTo(header.Slice(56, 16));
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(72, 8), partitionEntryLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80, 4), EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84, 4), EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(88, 4), entriesCrc);

        uint headerCrc = Crc32.Compute(header[..92]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), headerCrc);

        return sector;
    }

    private static void WritePartitionEntry(
        Span<byte> entry, Guid typeGuid, Guid partitionGuid, long firstLba, long lastLba, string name)
    {
        typeGuid.ToByteArray().CopyTo(entry[..16]);
        partitionGuid.ToByteArray().CopyTo(entry.Slice(16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(32, 8), firstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(40, 8), lastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(48, 8), 0); // 속성
        Encoding.Unicode.GetBytes(name).CopyTo(entry.Slice(56, 72));
    }

    // --- 검증 헬퍼 ---------------------------------------------------------

    public static bool IsHeaderCrcValid(ReadOnlySpan<byte> sector)
    {
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(12, 4));
        if (headerSize is < 92 or > 512) return false;

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(16, 4));

        var copy = sector[..(int)headerSize].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16, 4), 0);

        return Crc32.Compute(copy) == stored;
    }

    public static bool HasGptSignature(ReadOnlySpan<byte> sector) =>
        sector[..8].SequenceEqual("EFI PART"u8);

    public static long ReadMyLba(ReadOnlySpan<byte> sector) =>
        BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(24, 8));

    public static long ReadAlternateLba(ReadOnlySpan<byte> sector) =>
        BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(32, 8));

    public static long ReadLastUsableLba(ReadOnlySpan<byte> sector) =>
        BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(48, 8));

    public static long ReadPartitionEntryLba(ReadOnlySpan<byte> sector) =>
        BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(72, 8));

    // --- 파티션 엔트리 읽기 (재작성 검증용) --------------------------------

    /// <summary>엔트리 배열이 시작하는 LBA에서 index번째 엔트리의 StartingLBA를 읽습니다.</summary>
    public static long ReadEntryStartLba(byte[] image, long entryArrayLba, int index) =>
        BinaryPrimitives.ReadInt64LittleEndian(
            image.AsSpan((int)(entryArrayLba * SectorSize) + index * EntrySize + 32, 8));

    public static long ReadEntryEndLba(byte[] image, long entryArrayLba, int index) =>
        BinaryPrimitives.ReadInt64LittleEndian(
            image.AsSpan((int)(entryArrayLba * SectorSize) + index * EntrySize + 40, 8));

    public static Guid ReadEntryUniqueGuid(byte[] image, long entryArrayLba, int index) =>
        new(image.AsSpan((int)(entryArrayLba * SectorSize) + index * EntrySize + 16, 16).ToArray());

    public static string ReadEntryName(byte[] image, long entryArrayLba, int index)
    {
        var raw = image.AsSpan((int)(entryArrayLba * SectorSize) + index * EntrySize + 56, 72).ToArray();
        return Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    /// <summary>헤더가 가리키는 엔트리 배열의 CRC가 헤더에 저장된 값과 일치하는지 검사합니다.</summary>
    public static bool IsEntriesCrcValid(byte[] image, ReadOnlySpan<byte> headerSector)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(headerSector.Slice(88, 4));
        long entryLba = ReadPartitionEntryLba(headerSector);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(headerSector.Slice(80, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(headerSector.Slice(84, 4));
        var arr = image.AsSpan((int)(entryLba * SectorSize), (int)(count * size));
        return Crc32.Compute(arr) == stored;
    }
}
