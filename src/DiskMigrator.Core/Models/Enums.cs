namespace DiskMigrator.Core.Models;

/// <summary>디스크의 파티션 테이블 형식.</summary>
public enum PartitionStyle
{
    Unknown = 0,
    Mbr = 1,
    Gpt = 2,
    /// <summary>파티션 테이블이 없음 (초기화되지 않은 디스크).</summary>
    Raw = 3,
}

/// <summary>디스크가 연결된 버스 종류. 진단과 UI 표시에 사용합니다.</summary>
public enum DiskBusType
{
    Unknown = 0,
    Scsi,
    Atapi,
    Ata,
    Ieee1394,
    Ssa,
    Fibre,
    Usb,
    RAID,
    Iscsi,
    Sas,
    Sata,
    Sd,
    Mmc,
    Virtual,
    FileBackedVirtual,
    Nvme,
}

/// <summary>불량 섹터를 만났을 때의 처리 정책.</summary>
public enum BadSectorPolicy
{
    /// <summary>재시도 후에도 실패하면 작업 전체를 중단합니다. (기본값 — 가장 안전)</summary>
    Abort = 0,

    /// <summary>읽지 못한 섹터만 0으로 채우고 계속 진행합니다. 결과 리포트에 목록을 남깁니다.</summary>
    ZeroFillAndContinue = 1,
}

/// <summary>안전 점검 결과 한 건의 심각도.</summary>
public enum SafetySeverity
{
    /// <summary>참고 정보. 진행을 막지 않습니다.</summary>
    Info = 0,

    /// <summary>위험하지만 진행 가능. 사용자에게 반드시 보여야 합니다.</summary>
    Warning = 1,

    /// <summary>사용자가 대상 디스크 모델명을 직접 입력해 확인해야만 진행할 수 있습니다.</summary>
    RequiresConfirmation = 2,

    /// <summary>어떤 확인으로도 진행할 수 없습니다. 구조적으로 차단됩니다.</summary>
    Blocker = 3,
}

/// <summary>클론 작업의 최종 상태.</summary>
public enum CloneOutcome
{
    Completed = 0,
    CompletedWithBadSectors = 1,
    Cancelled = 2,
    Failed = 3,
}
