using DiskMigrator.Core.Util;

namespace DiskMigrator.Core.Models;

/// <summary>디스크 위의 파티션 하나에 대한 정보.</summary>
public sealed class PartitionInfo
{
    /// <summary>파티션 테이블상의 번호 (1부터).</summary>
    public required int Number { get; init; }

    /// <summary>디스크 시작점 기준 바이트 오프셋.</summary>
    public required long StartingOffset { get; init; }

    public required long LengthBytes { get; init; }

    /// <summary>StartingOffset + LengthBytes (파티션 끝의 배타적 오프셋).</summary>
    public long EndOffset => StartingOffset + LengthBytes;

    /// <summary>마운트된 드라이브 문자 (예: "C"). 마운트되지 않았으면 null.</summary>
    public string? DriveLetter { get; init; }

    /// <summary>\\?\Volume{GUID}\ 형식의 볼륨 GUID 경로. VSS 스냅샷과 볼륨 잠금에 사용합니다.</summary>
    public string? VolumeGuidPath { get; init; }

    /// <summary>파일 시스템 이름 (NTFS, FAT32, exFAT 등). 인식 불가면 null.</summary>
    public string? FileSystem { get; init; }

    public string? VolumeLabel { get; init; }

    /// <summary>GPT 파티션 타입 GUID. MBR 디스크에서는 null.</summary>
    public Guid? GptPartitionType { get; init; }

    /// <summary>MBR 파티션 타입 바이트. GPT 디스크에서는 null.</summary>
    public byte? MbrPartitionType { get; init; }

    /// <summary>EFI 시스템 파티션 여부 (UEFI 부팅 요소).</summary>
    public bool IsEfiSystemPartition { get; init; }

    /// <summary>MBR 활성(부팅) 파티션 여부.</summary>
    public bool IsActive { get; init; }

    public long? FreeSpaceBytes { get; init; }

    public long? UsedSpaceBytes =>
        FreeSpaceBytes is { } free ? LengthBytes - free : null;

    public override string ToString()
    {
        // 크기 표기는 반드시 SizeFormatter를 거칩니다. 직접 GB로 나누면 화면 다른 곳과
        // 어긋납니다 — 같은 디스크가 막대에는 "3.64 TB", 안전 문구에는 "4000.8 GB"로
        // 나오고, 16 MB짜리 파티션은 "0.0 GB"로 뭉개집니다.
        var letter = DriveLetter is null ? "" : $"{DriveLetter}: ";
        var fs = FileSystem ?? (IsEfiSystemPartition ? "EFI" : "RAW");
        return $"#{Number} {letter}{fs} {SizeFormatter.Format(LengthBytes)}";
    }
}
