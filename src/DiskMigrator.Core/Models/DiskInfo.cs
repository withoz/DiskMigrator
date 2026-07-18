namespace DiskMigrator.Core.Models;

/// <summary>
/// 물리 디스크 한 대에 대한 식별 정보. 저수준 계층이 채워서 Core/UI로 전달합니다.
/// </summary>
public sealed class DiskInfo
{
    /// <summary>Windows 물리 디스크 번호 (\\.\PhysicalDriveN 의 N).</summary>
    public required int DeviceNumber { get; init; }

    /// <summary>\\.\PhysicalDriveN 형식의 장치 경로.</summary>
    public string DevicePath => $@"\\.\PhysicalDrive{DeviceNumber}";

    /// <summary>WMI Win32_DiskDrive.Model. 사용자에게 보여주고 확인 절차에 쓰는 이름입니다.</summary>
    public required string Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? FirmwareRevision { get; init; }

    /// <summary>디스크 전체 크기(바이트).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>논리 섹터 크기(바이트). 보통 512 또는 4096.</summary>
    public required int LogicalSectorSize { get; init; }

    /// <summary>물리 섹터 크기(바이트). 512e 디스크는 논리 512 / 물리 4096입니다.</summary>
    public int PhysicalSectorSize { get; init; }

    public DiskBusType BusType { get; init; }

    public PartitionStyle PartitionStyle { get; init; }

    /// <summary>GPT 디스크 GUID(DiskId). MBR/RAW 디스크에서는 null.</summary>
    /// <remarks>
    /// BCD의 장치 참조가 이 값을 내장합니다. 디스크 서명 충돌로 Windows가 이 GUID를 재서명하면
    /// BCD 참조가 어긋나 부팅 시 0xc000000e가 납니다. 부팅 구성 검사가 이를 대조합니다.
    /// </remarks>
    public Guid? DiskGuid { get; init; }

    public bool IsRemovable { get; init; }

    public bool IsReadOnly { get; init; }

    /// <summary>디스크가 오프라인 상태인지 (디스크 관리의 "오프라인").</summary>
    public bool IsOffline { get; init; }

    /// <summary>현재 실행 중인 Windows가 설치된 디스크. 대상으로 절대 선택할 수 없습니다.</summary>
    public bool IsSystemDisk { get; init; }

    /// <summary>부팅에 사용된 디스크(EFI 시스템 파티션 또는 활성 MBR 파티션 보유).</summary>
    public bool IsBootDisk { get; init; }

    /// <summary>페이지 파일이 올라가 있는 디스크. 대상으로 선택하면 차단합니다.</summary>
    public bool HasPageFile { get; init; }

    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = [];

    /// <summary>파티션 테이블이 있고 파티션이 하나라도 있으면 "기존 데이터가 있다"고 봅니다.</summary>
    public bool HasExistingData => PartitionStyle is PartitionStyle.Mbr or PartitionStyle.Gpt && Partitions.Count > 0;

    public override string ToString() => $"[{DeviceNumber}] {Model} ({SizeBytes / 1_000_000_000.0:F1} GB)";
}
