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

    /// <summary>MBR 디스크의 NT 디스크 서명(0x1B8). GPT/RAW 디스크에서는 null.</summary>
    /// <remarks>
    /// MBR에서 <see cref="DiskGuid"/>와 같은 역할을 합니다. BCD의 장치 참조가 이 4바이트를
    /// 내장하므로, 원본과 대상이 함께 연결돼 서명이 충돌하면 Windows가 대상을 재서명하고
    /// BCD 참조가 어긋나 부팅이 실패합니다. 부팅 구성 검사가 이를 대조합니다.
    /// </remarks>
    public uint? MbrSignature { get; init; }

    /// <summary>MBR 확장 파티션(논리 드라이브)이 있는 디스크인지.</summary>
    /// <remarks>
    /// 논리 드라이브는 EBR 체인으로 이어지고 그 안의 오프셋이 상대값이라, 파티션을 옮기려면
    /// 모든 EBR을 함께 다시 써야 합니다. 지원하지 않으므로 리사이즈를 <b>시작하기 전에</b>
    /// 막아야 합니다 — 복제가 끝난 뒤 거절하면 파티션은 옮겨졌는데 테이블은 못 고친
    /// 못 쓰는 디스크가 남습니다.
    /// </remarks>
    public bool HasExtendedPartition { get; init; }

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

    /// <summary>
    /// BIOS(레거시)로만 부팅되는 배치인지 — MBR이면서 활성 파티션이 있고 EFI 시스템 파티션이 없음.
    /// </summary>
    /// <remarks>
    /// 이 배치의 사본은 레거시(CSM) 부팅을 지원하는 하드웨어에서만 켜집니다. UEFI 펌웨어는
    /// ESP의 부트로더를 찾는데 그것이 없으므로 <b>아무 말 없이 다음 장치로 넘어갑니다</b>.
    /// NVMe는 특히 확정적입니다 — 레거시 부팅용 옵션 ROM이 사실상 존재하지 않아 어떤 모드로도
    /// 부팅되지 않습니다(실기에서 규명).
    ///
    /// <para>시작 전 경고와 복제 후 UEFI 변환 제안이 <b>같은 판정</b>을 써야 합니다. 따로
    /// 계산하면 한쪽만 조건이 바뀌었을 때 "경고는 하는데 변환 버튼은 없는" 상태가 됩니다.</para>
    /// </remarks>
    public bool IsBiosOnlyBootLayout =>
        PartitionStyle == PartitionStyle.Mbr &&
        Partitions.Any(p => p.IsActive) &&
        !Partitions.Any(p => p.IsEfiSystemPartition);

    /// <summary>파티션 테이블이 있고 파티션이 하나라도 있으면 "기존 데이터가 있다"고 봅니다.</summary>
    public bool HasExistingData => PartitionStyle is PartitionStyle.Mbr or PartitionStyle.Gpt && Partitions.Count > 0;

    public override string ToString() => $"[{DeviceNumber}] {Model} ({SizeBytes / 1_000_000_000.0:F1} GB)";
}
