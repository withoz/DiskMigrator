using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Mcp.Dto;

namespace DiskMigrator.Mcp;

/// <summary>
/// 엔진 모델 → DTO 변환. <b>민감 정보 마스킹도 여기서 함께 처리합니다</b>(계획서 §5.2·§7).
/// </summary>
/// <remarks>
/// 변환을 한곳에 모으는 이유: 진단 결과는 대화 로그에 남으므로, 시리얼·볼륨 레이블이
/// 어디선가 빠져나가면 사용자가 의도치 않게 공유하게 됩니다. 통로가 하나여야 빠뜨리지 않습니다.
/// </remarks>
public sealed class Mapping(bool includeSensitive = false)
{
    /// <summary>시리얼·볼륨 레이블을 그대로 내보낼지. 사용자가 앱에서 켤 때만 참입니다.</summary>
    public bool IncludeSensitive { get; } = includeSensitive;

    public DiskDto ToDto(DiskInfo d) => new(
        DeviceNumber: d.DeviceNumber,
        Model: d.Model,
        SerialNumber: Mask(d.SerialNumber),
        SizeBytes: d.SizeBytes,
        SizeText: SizeFormatter.Format(d.SizeBytes),
        BusType: d.BusType.ToString(),
        PartitionStyle: d.PartitionStyle.ToString(),
        IsSystemDisk: d.IsSystemDisk,
        IsBootDisk: d.IsBootDisk,
        HasPageFile: d.HasPageFile,
        IsRemovable: d.IsRemovable,
        IsReadOnly: d.IsReadOnly,
        IsOffline: d.IsOffline,
        DiskGuid: d.DiskGuid?.ToString("B"),
        MbrSignature: d.MbrSignature is { } s ? $"0x{s:X8}" : null,
        LogicalSectorSize: d.LogicalSectorSize,
        PartitionCount: d.Partitions.Count);

    public DiskDetailDto ToDetailDto(DiskInfo d) =>
        new(ToDto(d), d.Partitions.Select(ToDto).ToList());

    public PartitionDto ToDto(PartitionInfo p) => new(
        Number: p.Number,
        OffsetBytes: p.StartingOffset,
        SizeBytes: p.LengthBytes,
        SizeText: SizeFormatter.Format(p.LengthBytes),
        DriveLetter: p.DriveLetter,
        FileSystem: p.FileSystem,
        Label: Mask(p.VolumeLabel),
        Kind: DescribeKind(p),
        IsActive: p.IsActive,
        // 여유 공간만 알 수 있으므로 사용량은 역산합니다. 마운트 안 된 볼륨은 알 수 없어 null입니다.
        UsedBytes: p.FreeSpaceBytes is { } free ? p.LengthBytes - free : null);

    /// <summary>부팅 준비 검사 결과를 DTO로. 요약 한 줄은 여기서 만듭니다.</summary>
    public BootCheckDto ToDto(Core.Registry.BootReadinessReport r)
    {
        int fatalFailed = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Fatal && i.Passed == false);
        int unknown = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Fatal && i.Passed is null);
        int warned = r.Items.Count(i => i.Severity == Core.Registry.BootCheckSeverity.Warning && i.Passed == false);

        // "확인 못 함"과 "실패"를 구분해 말합니다 — 볼륨이 안 잡혀 못 본 것을 결함으로 단정하면
        // 사용자가 멀쩡한 디스크를 의심하게 됩니다.
        string summary = r.WouldBoot
            ? warned > 0
                ? $"Boot configuration looks complete, but {warned} warning(s) may matter on different hardware."
                : "Boot configuration looks complete."
            : fatalFailed > 0
                ? $"{fatalFailed} critical check(s) failed — this disk is not expected to boot."
                : $"{unknown} critical check(s) could not be verified (volume not mounted?) — boot cannot be confirmed.";

        return new BootCheckDto(r.WouldBoot, r.HasWarnings, summary, r.Items.Select(ToDto).ToList());
    }

    public BootCheckItemDto ToDto(Core.Registry.BootCheckItem i) =>
        new(i.Name, i.Passed, i.Severity.ToString(), i.Detail, i.Code);

    /// <summary>부팅 드라이버 조사 결과를 DTO로.</summary>
    /// <remarks>
    /// 정상 드라이버 89개를 다 보내면 응답만 길어지고 판단에는 도움이 안 됩니다.
    /// <b>문제가 되는 것(파일 없음·표준 위치 밖)만</b> 목록으로 싣고, 나머지는 개수로 요약합니다.
    /// </remarks>
    public BootDriverInventoryDto ToDto(Core.Registry.BootDriverInventoryResult r)
    {
        string summary = r.MissingFiles.Count > 0
            ? $"{r.MissingFiles.Count} of {r.Drivers.Count} boot-start driver(s) are registered but their files are missing — " +
              "this alone can stop the kernel from starting."
            : r.OutsideSystem32.Count > 0
                ? $"All {r.Drivers.Count} boot-start driver files are present. " +
                  $"{r.OutsideSystem32.Count} load from outside System32 (third-party — may be fine, worth noting)."
                : $"All {r.Drivers.Count} boot-start driver files are present and load from standard locations.";

        return new BootDriverInventoryDto(
            ControlSet: r.ControlSet,
            TotalCount: r.Drivers.Count,
            MissingCount: r.MissingFiles.Count,
            OutsideSystem32Count: r.OutsideSystem32.Count,
            Summary: summary,
            Missing: r.MissingFiles.Select(ToDto).ToList(),
            OutsideSystem32: r.OutsideSystem32.Select(ToDto).ToList());
    }

    public BootDriverDto ToDto(Core.Registry.BootDriverEntry d) =>
        new(d.ServiceName, d.Group, d.ImagePath, d.ResolvedPath, d.FileExists, d.FileSizeBytes);

    /// <summary>빠른 시작 상태를 DTO로. 요약에 "무슨 일이 벌어지는지"를 적습니다.</summary>
    public FastStartupDto ToDto(Core.Registry.FastStartupStateResult r)
    {
        string summary = r.ResumeWouldBeAttempted
            ? "A hibernation image (hiberfil.sys) is present, so the boot manager will try to RESUME " +
              "instead of booting. On different hardware that resume fails and hangs at the logo — " +
              "and boot diagnostics stay silent because they only apply to the normal boot path."
            : r.HiberbootEnabled == 1
                ? "No hibernation image right now, but Fast Startup is ON — shutting down will create one again."
                : "Fast Startup is off and there is no hibernation image. The disk will take the normal boot path.";

        return new FastStartupDto(
            r.HiberbootEnabled, r.HibernateEnabled, r.HiberfilExists, r.HiberfilSizeBytes,
            r.ResumeWouldBeAttempted, summary);
    }

    /// <summary>
    /// 파티션이 무엇인지 사람이 이해할 수 있게 분류합니다. Claude가 GUID를 해석하지 않아도 되게.
    /// </summary>
    private static string DescribeKind(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EfiSystem";

        if (p.GptPartitionType is { } g)
        {
            string s = g.ToString();
            if (s.Equals("c12a7328-f81f-11d2-ba4b-00a0c93ec93b", StringComparison.OrdinalIgnoreCase)) return "EfiSystem";
            if (s.Equals("e3c9e316-0b5c-4db8-817d-f92df00215ae", StringComparison.OrdinalIgnoreCase)) return "MicrosoftReserved";
            if (s.Equals("de94bba4-06d1-4d40-a16a-bfd50179d6ac", StringComparison.OrdinalIgnoreCase)) return "WindowsRecovery";
            if (s.Equals("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", StringComparison.OrdinalIgnoreCase)) return "BasicData";
            return "Other";
        }

        return p.MbrPartitionType switch
        {
            0x07 => "BasicData",     // NTFS/exFAT
            0x0B or 0x0C => "Fat32",
            0x27 => "WindowsRecovery",
            0xEE => "GptProtective",
            null => "Unknown",
            _ => "Other",
        };
    }

    /// <summary>
    /// 민감 문자열을 가립니다 — 앞 2글자만 남기고 나머지는 별표.
    /// 완전히 지우지 않는 이유는, 사용자가 "그 디스크 맞나?"를 대조할 수 있어야 하기 때문입니다.
    /// </summary>
    private string? Mask(string? value)
    {
        if (IncludeSensitive || string.IsNullOrEmpty(value)) return value;
        return value.Length <= 2 ? new string('*', value.Length) : value[..2] + new string('*', value.Length - 2);
    }
}
