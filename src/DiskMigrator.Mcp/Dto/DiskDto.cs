using System.Text.Json.Serialization;

namespace DiskMigrator.Mcp.Dto;

/// <summary>
/// Claude에게 보내는 디스크 요약. 엔진의 <c>DiskInfo</c>를 그대로 쓰지 않는 이유는
/// <see href="../../../docs/MCP-PLAN.md">계획서 §5.2</see>에 있습니다 — 직렬화 불가 타입이 섞여 있고,
/// 민감 정보를 걸러야 하며, 엔진 모델이 바뀌어도 이 계약은 유지되어야 합니다.
/// </summary>
/// <param name="DeviceNumber">
/// 물리 디스크 번호. <b>디스크를 가리킬 때는 항상 이 값을 씁니다</b> — 드라이브 문자는 바뀝니다.
/// </param>
/// <param name="Model">사용자에게 보여주고 확인 절차에 쓰는 이름.</param>
/// <param name="SerialNumber">기본은 마스킹됩니다(§7). 상세 공유를 켠 경우에만 전체가 들어옵니다.</param>
/// <param name="SizeBytes">디스크 전체 크기(바이트).</param>
/// <param name="SizeText">사람이 읽는 크기. Claude가 그대로 인용할 수 있게 함께 보냅니다.</param>
/// <param name="BusType">NVMe·SATA·USB 등. 호환성 판정(§2.6)의 입력입니다.</param>
/// <param name="PartitionStyle">GPT·MBR·RAW.</param>
/// <param name="IsSystemDisk">지금 이 Windows가 설치된 디스크인지.</param>
/// <param name="IsBootDisk">지금 부팅에 쓰인 디스크인지.</param>
/// <param name="HasPageFile">페이지 파일이 있는 디스크인지.</param>
/// <param name="IsRemovable">이동식 매체인지.</param>
/// <param name="IsReadOnly">쓰기 금지 상태인지.</param>
/// <param name="IsOffline">디스크 관리에서 "오프라인" 상태인지.</param>
/// <param name="DiskGuid">GPT 디스크 GUID. MBR/RAW면 null.</param>
/// <param name="MbrSignature">MBR 디스크 서명(16진). GPT/RAW면 null.</param>
/// <param name="LogicalSectorSize">논리 섹터 크기.</param>
/// <param name="PartitionCount">파티션 수.</param>
public sealed record DiskDto(
    int DeviceNumber,
    string Model,
    string? SerialNumber,
    long SizeBytes,
    string SizeText,
    string BusType,
    string PartitionStyle,
    bool IsSystemDisk,
    bool IsBootDisk,
    bool HasPageFile,
    bool IsRemovable,
    bool IsReadOnly,
    bool IsOffline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiskGuid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MbrSignature,
    int LogicalSectorSize,
    int PartitionCount);

/// <summary>파티션 하나의 요약.</summary>
/// <param name="Number">파티션 번호(1부터).</param>
/// <param name="OffsetBytes">디스크 시작으로부터의 오프셋.</param>
/// <param name="SizeBytes">파티션 크기.</param>
/// <param name="SizeText">사람이 읽는 크기.</param>
/// <param name="DriveLetter">할당된 드라이브 문자(없으면 null).</param>
/// <param name="FileSystem">NTFS·FAT32 등. 마운트되지 않았거나 손상되면 null.</param>
/// <param name="Label">볼륨 레이블. 기본 마스킹 대상입니다(§7).</param>
/// <param name="Kind">EFI 시스템·Windows·복구·기타 중 무엇인지 — 사람이 이해할 수 있는 분류.</param>
/// <param name="IsActive">MBR 활성 파티션인지.</param>
/// <param name="UsedBytes">사용 중인 용량(알 수 있을 때만).</param>
public sealed record PartitionDto(
    int Number,
    long OffsetBytes,
    long SizeBytes,
    string SizeText,
    string? DriveLetter,
    string? FileSystem,
    string? Label,
    string Kind,
    bool IsActive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UsedBytes);

/// <summary>한 디스크의 상세 — 파티션 배치까지.</summary>
public sealed record DiskDetailDto(DiskDto Disk, IReadOnlyList<PartitionDto> Partitions);
