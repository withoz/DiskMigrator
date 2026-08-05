using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>진단 리포트 — 한 디스크의 부팅 관련 상태를 통째로 담은 파일 형식.</summary>
/// <param name="FormatVersion">형식 버전. 나중에 항목이 늘어도 옛 파일을 읽을 수 있게 합니다.</param>
/// <param name="CollectedUtc">수집 시각.</param>
/// <param name="AppVersion">수집한 앱 버전.</param>
/// <param name="CollectedInWinPe">WinPE(부팅 USB)에서 수집했는지.</param>
/// <param name="Summary">사람이 파일을 열자마자 읽을 수 있는 요약.</param>
/// <param name="Disk">대상 디스크 개요.</param>
/// <param name="Partitions">파티션 배치.</param>
/// <param name="BootCheck">부팅 준비 검사 항목.</param>
/// <param name="BootDrivers">부팅 시작 드라이버 요약(문제 항목만 상세).</param>
/// <param name="FastStartup">빠른 시작·재개 이미지 상태.</param>
/// <param name="BootTrace">부팅 흔적 — 어디까지 갔는지.</param>
/// <param name="Esp">ESP 감사(서명 발급자 포함).</param>
/// <param name="Firmware">수집한 PC의 펌웨어 정보. <b>대상 PC의 것일 때만 의미가 있습니다.</b></param>
public sealed record DiagnosticReport(
    string FormatVersion,
    DateTime CollectedUtc,
    string AppVersion,
    bool CollectedInWinPe,
    string Summary,
    DiagnosticDisk Disk,
    IReadOnlyList<DiagnosticPartition> Partitions,
    IReadOnlyList<DiagnosticCheckItem> BootCheck,
    DiagnosticBootDrivers? BootDrivers,
    DiagnosticFastStartup? FastStartup,
    DiagnosticBootTrace? BootTrace,
    DiagnosticEsp? Esp,
    DiagnosticFirmware? Firmware);

/// <summary>대상 디스크 개요.</summary>
public sealed record DiagnosticDisk(
    int DeviceNumber, string Model, string? SerialNumber, long SizeBytes,
    string BusType, string PartitionStyle, string? DiskGuid, string? MbrSignature);

/// <summary>파티션 하나.</summary>
public sealed record DiagnosticPartition(
    int Number, long OffsetBytes, long SizeBytes, string? FileSystem,
    string? Label, string? DriveLetter, bool IsEfiSystem, bool IsActive);

/// <summary>부팅 준비 검사 항목.</summary>
public sealed record DiagnosticCheckItem(string Name, bool? Passed, string Severity, string Detail, string? Code);

/// <summary>부팅 드라이버 요약.</summary>
public sealed record DiagnosticBootDrivers(
    int TotalCount, int MissingCount, int OutsideSystem32Count,
    IReadOnlyList<string> MissingNames, IReadOnlyList<string> OutsideNames);

/// <summary>빠른 시작 상태.</summary>
public sealed record DiagnosticFastStartup(
    uint? HiberbootEnabled, uint? HibernateEnabled, bool HiberfilExists,
    long? HiberfilSizeBytes, bool ResumeWouldBeAttempted);

/// <summary>부팅 흔적.</summary>
public sealed record DiagnosticBootTrace(
    DateTime? LastAttemptUtc, string Progress,
    IReadOnlyList<DiagnosticTraceFile> Files,
    IReadOnlyList<string> NtbtlogTail,
    IReadOnlyList<string> NtbtlogNotLoaded);

/// <summary>흔적 파일 하나.</summary>
public sealed record DiagnosticTraceFile(string Name, bool Exists, DateTime? LastWriteUtc, string Stage);

/// <summary>ESP 감사.</summary>
public sealed record DiagnosticEsp(
    bool Uefi, bool BootManagerPresent, bool FallbackPresent, bool BcdPresent,
    string? SignatureIssuer, string? SignatureAuthority,
    int TotalFileCount, IReadOnlyList<string> ForeignBootFolders);

/// <summary>수집 PC의 펌웨어.</summary>
public sealed record DiagnosticFirmware(
    string? BoardManufacturer, string? BoardProduct, string? BiosVersion,
    DateTime? BiosReleaseDate, bool IsUefi, bool? SecureBootEnabled);

/// <summary>
/// 부팅이 막힌 PC의 상태를 <b>파일 하나로 모아</b> 다른 PC에서 분석할 수 있게 합니다.
/// </summary>
/// <remarks>
/// 부팅 불가 PC를 봐야 하는데 그 PC에는 Claude가 없습니다 — WinPE는 램디스크 최소 환경이고
/// 네트워크도 보장되지 않습니다. 그래서 <b>진단 결과를 파일로 옮깁니다</b>.
/// PE에서 이 리포트를 USB에 저장해 정상 PC로 가져오면, 오늘 손으로 열 번 왕복했던 조사를
/// 한 번에 끝낼 수 있습니다.
///
/// <para><b>수집은 순수 읽기입니다.</b> 대상 디스크에 아무것도 쓰지 않습니다 —
/// 부팅이 막힌 디스크는 이미 상태가 위태로울 수 있고, 진단이 그것을 더 건드려서는 안 됩니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DiagnosticCollector(IDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>이 도구가 쓰는 리포트 형식 버전.</summary>
    public const string CurrentFormatVersion = "1.0";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 디스크 하나의 진단을 모읍니다. 개별 진단이 실패해도 나머지는 채웁니다 —
    /// 부분적인 리포트라도 없는 것보다 낫습니다.
    /// </summary>
    /// <param name="includeSensitive">시리얼·볼륨 레이블을 그대로 담을지. 기본은 가립니다.</param>
    public async Task<DiagnosticReport> CollectAsync(
        int deviceNumber, bool includeSensitive = false, CancellationToken ct = default)
    {
        var disks = await diskService.EnumerateDisksAsync(ct);
        var disk = disks.FirstOrDefault(d => d.DeviceNumber == deviceNumber)
            ?? throw new InvalidOperationException($"디스크 {deviceNumber}를 찾지 못했습니다.");

        var input = BootReadinessCheck.ResolveInput(disk);

        DiagnosticBootDrivers? drivers = null;
        DiagnosticFastStartup? fast = null;
        DiagnosticBootTrace? trace = null;
        DiagnosticEsp? esp = null;

        if (input.WindowsRoot is { } winRoot)
        {
            drivers = Try(() =>
            {
                var r = BootDriverInventory.Inspect(winRoot);
                return new DiagnosticBootDrivers(
                    r.Drivers.Count, r.MissingFiles.Count, r.OutsideSystem32.Count,
                    r.MissingFiles.Select(d => d.ServiceName).ToList(),
                    r.OutsideSystem32.Select(d => d.ServiceName).ToList());
            }, "부팅 드라이버");

            fast = Try(() =>
            {
                var r = FastStartupState.Inspect(winRoot);
                return new DiagnosticFastStartup(
                    r.HiberbootEnabled, r.HibernateEnabled, r.HiberfilExists,
                    r.HiberfilSizeBytes, r.ResumeWouldBeAttempted);
            }, "빠른 시작");

            trace = Try(() =>
            {
                var r = BootTraceAnalysis.Inspect(winRoot);
                return new DiagnosticBootTrace(
                    r.LastAttemptUtc, r.Progress.ToString(),
                    r.Files.Select(f => new DiagnosticTraceFile(f.Name, f.Exists, f.LastWriteUtc, f.Stage.ToString())).ToList(),
                    r.NtbtlogTailLines, r.NtbtlogNotLoaded);
            }, "부팅 흔적");
        }

        if (input.SystemRoot is { } espRoot)
        {
            esp = Try(() =>
            {
                var r = EspAudit.Inspect(espRoot);
                return new DiagnosticEsp(
                    r.Uefi, r.BootManagerPresent, r.FallbackPresent, r.BcdPresent,
                    r.Signature?.Issuer, r.Signature?.Authority,
                    r.TotalFileCount, r.ForeignBootFolders);
            }, "ESP 감사");
        }

        var check = Try(() => BootReadinessCheck.Inspect(input), "부팅 준비 검사");
        var firmware = Try(() =>
        {
            var f = Devices.FirmwareInfo.Read();
            return new DiagnosticFirmware(
                f.BoardManufacturer, f.BoardProduct, f.BiosVersion,
                f.BiosReleaseDate, f.IsUefi, f.SecureBootEnabled);
        }, "펌웨어 정보");

        var report = new DiagnosticReport(
            FormatVersion: CurrentFormatVersion,
            CollectedUtc: DateTime.UtcNow,
            AppVersion: typeof(DiagnosticCollector).Assembly.GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0",
            CollectedInWinPe: IsWinPe(),
            Summary: "",   // 아래에서 채웁니다
            Disk: new DiagnosticDisk(
                disk.DeviceNumber, disk.Model, Mask(disk.SerialNumber, includeSensitive), disk.SizeBytes,
                disk.BusType.ToString(), disk.PartitionStyle.ToString(),
                disk.DiskGuid?.ToString("B"), disk.MbrSignature is { } s ? $"0x{s:X8}" : null),
            Partitions: disk.Partitions.Select(p => new DiagnosticPartition(
                p.Number, p.StartingOffset, p.LengthBytes, p.FileSystem,
                Mask(p.VolumeLabel, includeSensitive), p.DriveLetter,
                p.IsEfiSystemPartition, p.IsActive)).ToList(),
            BootCheck: check?.Items.Select(i => new DiagnosticCheckItem(
                i.Name, i.Passed, i.Severity.ToString(), i.Detail, i.Code)).ToList() ?? [],
            BootDrivers: drivers,
            FastStartup: fast,
            BootTrace: trace,
            Esp: esp,
            Firmware: firmware);

        return report with { Summary = BuildSummary(report) };
    }

    /// <summary>리포트를 파일로 저장합니다. 내용 해시를 함께 넣어 옮기는 중 깨졌는지 알 수 있게 합니다.</summary>
    public async Task SaveAsync(DiagnosticReport report, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string body = JsonSerializer.Serialize(report, Json);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];

        // 해시를 본문 밖 첫 줄에 둡니다 — 본문에 넣으면 자기 자신을 포함하게 되어 검증할 수 없습니다.
        var sb = new StringBuilder();
        sb.AppendLine($"// DiskMigrator-X diagnostic report  sha256:{hash}");
        sb.Append(body);

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        _logger.LogInformation("진단 리포트 저장: {Path} ({Size:N0} bytes)", path, sb.Length);
    }

    /// <summary>저장한 리포트를 읽습니다. 해시가 어긋나면 경고를 남기되 읽기는 계속합니다.</summary>
    public async Task<DiagnosticReport> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);

        string body = text;
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl > 0)
            {
                string header = text[..nl];
                body = text[(nl + 1)..];

                int idx = header.IndexOf("sha256:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    string expected = header[(idx + 7)..].Trim();
                    string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
                    if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                        _logger.LogWarning("진단 리포트 해시 불일치 — 옮기는 중 손상됐을 수 있습니다 ({Path})", path);
                }
            }
        }

        return JsonSerializer.Deserialize<DiagnosticReport>(body, Json)
            ?? throw new InvalidDataException("진단 리포트를 해석하지 못했습니다.");
    }

    /// <summary>사람이 파일을 열자마자 상황을 알 수 있게 요약을 만듭니다.</summary>
    private static string BuildSummary(DiagnosticReport r)
    {
        var lines = new List<string>
        {
            $"Disk {r.Disk.DeviceNumber}: {r.Disk.Model} ({r.Disk.SizeBytes / 1024 / 1024 / 1024} GB, " +
            $"{r.Disk.BusType}, {r.Disk.PartitionStyle}), {r.Partitions.Count} partition(s).",
        };

        if (r.BootTrace is { } t)
            lines.Add($"Last boot attempt: {t.LastAttemptUtc?.ToString("u") ?? "unknown"} — reached {t.Progress}.");

        if (r.BootDrivers is { } d)
            lines.Add(d.MissingCount > 0
                ? $"WARNING: {d.MissingCount} boot-start driver file(s) missing: {string.Join(", ", d.MissingNames)}."
                : $"All {d.TotalCount} boot-start driver files present.");

        if (r.FastStartup is { ResumeWouldBeAttempted: true })
            lines.Add("WARNING: a hibernation image is present — the boot manager will try to resume, " +
                      "which fails on different hardware.");

        if (r.Esp is { } e)
            lines.Add(e.BootManagerPresent
                ? $"ESP OK (boot manager signed by {e.SignatureAuthority ?? "unknown authority"})."
                : "WARNING: boot manager missing from the ESP.");

        int fatalFailed = r.BootCheck.Count(i => i.Severity == "Fatal" && i.Passed == false);
        if (fatalFailed > 0) lines.Add($"WARNING: {fatalFailed} critical boot check(s) failed.");

        return string.Join(" ", lines);
    }

    private T? Try<T>(Func<T> f, string what) where T : class
    {
        try { return f(); }
        catch (Exception ex)
        {
            // 하나가 실패해도 나머지는 담습니다 — 부분 리포트가 없는 것보다 낫습니다.
            _logger.LogWarning(ex, "진단 수집 중 '{What}' 실패 — 건너뜁니다.", what);
            return null;
        }
    }

    private static string? Mask(string? value, bool includeSensitive)
    {
        if (includeSensitive || string.IsNullOrEmpty(value)) return value;
        return value.Length <= 2 ? new string('*', value.Length) : value[..2] + new string('*', value.Length - 2);
    }

    private static bool IsWinPe()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\MiniNT");
            return k is not null;
        }
        catch { return false; }
    }
}
