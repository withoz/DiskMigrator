using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Devices;

/// <summary>부팅 복구 결과.</summary>
/// <param name="Success">복구 성공 여부.</param>
/// <param name="Message">사람이 읽을 요약.</param>
/// <param name="Steps">실행한 단계와 결과(진단용).</param>
public sealed record BootRepairResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// 클론 디스크의 BCD 장치 참조(device/osdevice)를 <b>현재 파티션</b>으로 다시 설정해
/// 0xc000000e를 고칩니다.
/// </summary>
/// <remarks>
/// 디스크 서명 충돌로 GPT 디스크 GUID가 재서명되면 BCD의 장치 참조가 어긋나 부팅이
/// 0xc000000e로 실패합니다. 이 복구는 대상 디스크의 ESP에 임시 드라이브 문자를 부여하고
/// <c>bcdedit /store</c>로 부팅 관리자·기본 로더·재개 개체의 device/osdevice를 이 디스크의
/// 실제 파티션으로 다시 씁니다. bcdedit은 BCD 장치 요소의 버전별 형식을 정확히 다루므로,
/// 하이브 바이너리를 직접 조작하는 위험을 피합니다. UEFI(GPT/ESP) 클론 전용입니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BootRepair(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");

    public BootRepairResult Repair(DiskInfo disk)
    {
        var steps = new List<string>();

        var esp = disk.Partitions.FirstOrDefault(p => p.IsEfiSystemPartition || p.GptPartitionType == EfiSystem);
        if (esp?.VolumeGuidPath is null)
            return new(false, "EFI 시스템 파티션(ESP)을 찾지 못했습니다. UEFI/GPT 클론에만 사용할 수 있습니다.", steps);

        var windows = FindWindowsPartition(disk);
        if (windows is null)
            return new(false, "\\Windows가 있는 파티션을 찾지 못했습니다 (볼륨이 마운트되어야 합니다).", steps);

        char? espTemp = null, winTemp = null;
        try
        {
            // ESP는 보통 드라이브 문자가 없으므로 임시 부여. bcdedit의 partition= 값은 문자만 받습니다.
            char espLetter = esp.DriveLetter is { } el ? el[0] : (espTemp = AssignTempLetter(esp.VolumeGuidPath))
                ?? throw new InvalidOperationException("ESP에 임시 드라이브 문자를 부여하지 못했습니다.");
            steps.Add($"ESP → {espLetter}: ({(espTemp is null ? "기존" : "임시")})");

            char winLetter;
            if (windows.DriveLetter is { } wl) winLetter = wl[0];
            else if (windows.VolumeGuidPath is { } wv)
                winLetter = (winTemp = AssignTempLetter(wv)) ?? throw new InvalidOperationException("Windows 파티션에 임시 문자를 부여하지 못했습니다.");
            else return new(false, "Windows 파티션의 볼륨 경로를 알 수 없습니다.", steps);
            steps.Add($"Windows → {winLetter}: ({(winTemp is null ? "기존" : "임시")})");

            string bcd = $@"{espLetter}:\EFI\Microsoft\Boot\BCD";
            if (!File.Exists(bcd))
                return new(false, $"BCD를 찾지 못했습니다: {bcd}", steps);

            // 재개(하이버네이트) 개체 GUID를 bcdedit 출력에서 얻습니다(있으면).
            string? resume = ReadResumeObject(bcd);

            var sets = new List<(string id, string element, string value)>
            {
                ("{bootmgr}", "device", $"partition={espLetter}:"),
                ("{default}", "device", $"partition={winLetter}:"),
                ("{default}", "osdevice", $"partition={winLetter}:"),
            };
            if (resume is not null)
            {
                sets.Add((resume, "device", $"partition={winLetter}:"));
                sets.Add((resume, "filedevice", $"partition={winLetter}:"));
            }

            foreach (var (id, element, value) in sets)
            {
                var (code, output) = RunBcdedit("/store", bcd, "/set", id, element, value);
                steps.Add($"set {id} {element} {value} → {(code == 0 ? "OK" : $"실패({code}): {output.Trim()}")}");
                if (code != 0)
                    return new(false, $"bcdedit 설정 실패: {id} {element}. {output.Trim()}", steps);
            }

            return new(true,
                $"BCD 장치 참조를 이 디스크(ESP {espLetter}:, Windows {winLetter}:)로 복구했습니다. 0xc000000e가 해결됩니다.",
                steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 복구 중 오류.");
            return new(false, $"복구 중 오류: {ex.Message}", steps);
        }
        finally
        {
            if (espTemp is { } e) TryRemoveLetter(e);
            if (winTemp is { } w) TryRemoveLetter(w);
        }
    }

    private static PartitionInfo? FindWindowsPartition(DiskInfo disk)
    {
        foreach (var p in disk.Partitions)
        {
            string? root = p.VolumeGuidPath ?? (p.DriveLetter is { } l ? $"{l}:\\" : null);
            if (root is null) continue;
            try
            {
                if (Directory.Exists(Path.Combine(root, "Windows", "System32"))) return p;
            }
            catch { /* 접근 불가 볼륨은 건너뜀 */ }
        }
        return null;
    }

    private char? AssignTempLetter(string volumeGuidPath)
    {
        string vol = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (char c in "STUVWYZRQPNM")
        {
            if (used.Contains(c)) continue;
            if (NativeMethods.SetVolumeMountPoint($"{c}:\\", vol))
            {
                _logger.LogInformation("임시 마운트 {Letter}: → {Vol}", c, vol);
                return c;
            }
        }
        return null;
    }

    private void TryRemoveLetter(char letter)
    {
        if (!NativeMethods.DeleteVolumeMountPoint($"{letter}:\\"))
            _logger.LogWarning("임시 마운트 {Letter}: 해제 실패(무해).", letter);
    }

    /// <summary>기본 로더의 resumeobject GUID를 bcdedit 출력에서 파싱합니다. 없으면 null.</summary>
    private string? ReadResumeObject(string bcdPath)
    {
        var (code, output) = RunBcdedit("/store", bcdPath, "/enum", "{default}");
        if (code != 0) return null;
        var m = Regex.Match(output, @"resumeobject\s+(\{[0-9a-fA-F\-]+\})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private (int Code, string Output) RunBcdedit(params string[] args)
    {
        var psi = new ProcessStartInfo("bcdedit.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("bcdedit를 시작하지 못했습니다.");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + stderr);
    }
}
