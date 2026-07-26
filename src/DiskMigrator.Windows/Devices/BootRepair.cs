using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiskMigrator.Core.Localization;
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
/// 원본과 대상을 함께 연결해 두면 디스크 식별자가 충돌해 Windows가 대상을 재서명합니다.
/// 그러면 BCD의 장치 참조가 어긋나 부팅이 0xc000000e로 실패합니다. 이 복구는 부팅 파일이 있는
/// 파티션에 임시 드라이브 문자를 부여하고 <c>bcdedit /store</c>로 부팅 관리자·기본 로더·재개
/// 개체의 device/osdevice를 이 디스크의 실제 파티션으로 다시 씁니다. bcdedit은 BCD 장치 요소의
/// 버전별 형식을 정확히 다루므로, 하이브 바이너리를 직접 조작하는 위험을 피합니다.
///
/// <para><b>UEFI와 BIOS 둘 다 지원합니다.</b> UEFI는 ESP 안의
/// <c>EFI\Microsoft\Boot\BCD</c>를, BIOS는 활성 파티션 루트의 <c>Boot\BCD</c>를 고칩니다.
/// 고치는 내용은 같고 부팅 파일이 어디 있느냐만 다릅니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BootRepair(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");

    public BootRepairResult Repair(DiskInfo disk)
    {
        var steps = new List<string>();

        // 부팅 파일(bootmgr·BCD)이 있는 파티션을 찾습니다.
        //
        // UEFI는 ESP, BIOS는 활성 파티션입니다. 예전에는 ESP만 찾아서 BIOS/MBR 클론에서는
        // "ESP를 찾지 못했습니다"로 끝났습니다 — 정작 MBR 클론이 디스크 서명 충돌로 BCD 참조가
        // 어긋나기 가장 쉬운데 고칠 방법이 없었습니다.
        var esp = disk.Partitions.FirstOrDefault(p => p.IsEfiSystemPartition || p.GptPartitionType == EfiSystem);
        bool uefi = esp is not null;

        var system = esp ?? disk.Partitions.FirstOrDefault(p => p.IsActive);
        if (system is null || (system.VolumeGuidPath is null && system.DriveLetter is null))
        {
            return new(false,
                uefi
                    ? L.T("EFI 시스템 파티션(ESP)의 볼륨 경로를 알 수 없습니다.",
                          "The EFI System Partition (ESP) has no known volume path.")
                    : L.T("부팅 파일이 있는 활성 파티션을 찾지 못했습니다 (볼륨이 마운트되어야 합니다).",
                          "No active partition with boot files was found (the volume must be mounted)."),
                steps);
        }

        var windows = FindWindowsPartition(disk);
        if (windows is null)
            return new(false, L.T(
                "\\Windows가 있는 파티션을 찾지 못했습니다 (볼륨이 마운트되어야 합니다).",
                "No partition containing \\Windows was found (the volume must be mounted)."), steps);

        char? espTemp = null, winTemp = null;
        try
        {
            // ESP는 보통 드라이브 문자가 없으므로 임시 부여. bcdedit의 partition= 값은 문자만 받습니다.
            char espLetter = system.DriveLetter is { } el ? el[0] : (espTemp = AssignTempLetter(system.VolumeGuidPath!))
                ?? throw new InvalidOperationException(L.T(
                    "부팅 파티션에 임시 드라이브 문자를 부여하지 못했습니다.",
                    "Failed to assign a temporary drive letter to the boot partition."));
            steps.Add($"{(uefi ? "ESP" : "활성 파티션")} → {espLetter}: ({(espTemp is null ? "기존" : "임시")})");

            char winLetter;
            if (windows.DriveLetter is { } wl) winLetter = wl[0];
            else if (windows.VolumeGuidPath is { } wv)
                winLetter = (winTemp = AssignTempLetter(wv)) ?? throw new InvalidOperationException(L.T(
                    "Windows 파티션에 임시 문자를 부여하지 못했습니다.",
                    "Failed to assign a temporary drive letter to the Windows partition."));
            else return new(false, L.T("Windows 파티션의 볼륨 경로를 알 수 없습니다.",
                                       "The Windows partition has no known volume path."), steps);
            steps.Add($"Windows → {winLetter}: ({(winTemp is null ? "기존" : "임시")})");

            // UEFI는 ESP 안의 EFI\Microsoft\Boot\, BIOS는 활성 파티션 루트의 Boot\.
            string bcd = uefi
                ? $@"{espLetter}:\EFI\Microsoft\Boot\BCD"
                : $@"{espLetter}:\Boot\BCD";
            if (!File.Exists(bcd))
                return new(false, L.T($"BCD를 찾지 못했습니다: {bcd}", $"BCD was not found: {bcd}"), steps);

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
                    return new(false, L.T($"bcdedit 설정 실패: {id} {element}. {output.Trim()}",
                                          $"bcdedit set failed: {id} {element}. {output.Trim()}"), steps);
            }

            string hibernation = DisableResume(bcd, winLetter, steps);

            return new(true, L.T(
                $"BCD 장치 참조를 이 디스크({(uefi ? "ESP" : "활성 파티션")} {espLetter}:, " +
                $"Windows {winLetter}:)로 복구했습니다. 0xc000000e가 해결됩니다.{hibernation}",
                $"Repaired the BCD device references to this disk ({(uefi ? "ESP" : "active partition")} {espLetter}:, " +
                $"Windows {winLetter}:). This resolves 0xc000000e.{hibernation}"),
                steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 복구 중 오류.");
            return new(false, L.T($"복구 중 오류: {ex.Message}", $"Error during repair: {ex.Message}"), steps);
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

    /// <summary>
    /// 사본이 최대 절전 이미지에서 재개하지 않게 하고, 남은 이미지를 지웁니다.
    /// </summary>
    /// <remarks>
    /// Windows는 기본값인 빠른 시작으로 종료할 때 커널 상태를 <c>hiberfil.sys</c>에 저장하고
    /// 다음 부팅에서 그것을 복원합니다. 저장된 상태는 원래 하드웨어를 전제하므로, 사본을 다른
    /// 메인보드에서 켜면 <b>오류 문구 없이 검은 화면에서 멈춥니다</b> — 부트로더·BCD·드라이버가
    /// 모두 정상인데도 원인을 알 수 없는 상태가 됩니다(실기에서 규명).
    ///
    /// <para><c>hiberboot</c>도 함께 끕니다. 켜 둔 채로 두면 사본에서 한 번 종료할 때 이미지가
    /// 다시 생겨 같은 문제가 되돌아옵니다.</para>
    ///
    /// <para>실패해도 복구 전체를 실패로 만들지 않습니다 — 장치 참조 복구는 이미 끝났고,
    /// 이쪽은 그 위의 추가 조치입니다.</para>
    /// </remarks>
    private string DisableResume(string bcdPath, char winLetter, List<string> steps)
    {
        try
        {
            foreach (string element in new[] { "resume", "hiberboot" })
            {
                var (code, output) = RunBcdedit("/store", bcdPath, "/set", "{bootmgr}", element, "No");
                steps.Add($"set {{bootmgr}} {element} No → {(code == 0 ? "OK" : $"실패({code}): {output.Trim()}")}");
            }

            string hiberfil = Path.Combine($"{winLetter}:\\", "hiberfil.sys");
            if (!File.Exists(hiberfil))
            {
                steps.Add("hiberfil.sys 없음");
                return L.T(" 재개(빠른 시작)도 껐습니다.", " Resume (Fast Startup) was also disabled.");
            }

            // 시스템·숨김 속성과 소유권 때문에 그냥은 지워지지 않습니다.
            RunProcess("takeown.exe", "/F", hiberfil, "/A");
            RunProcess("icacls.exe", hiberfil, "/grant", "*S-1-5-32-544:(F)");
            File.SetAttributes(hiberfil, FileAttributes.Normal);
            File.Delete(hiberfil);

            steps.Add("hiberfil.sys 삭제");
            return L.T(" 재개(빠른 시작)를 끄고 최대 절전 이미지를 지웠습니다.",
                       " Resume (Fast Startup) was disabled and the hibernation image deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "최대 절전 이미지 정리에 실패했습니다.");
            steps.Add($"최대 절전 정리 실패: {ex.Message}");
            return " (최대 절전 이미지는 정리하지 못했습니다 — 사본이 검은 화면에서 멈추면 " +
                   "hiberfil.sys 를 지우십시오.)";
        }
    }

    private static void RunProcess(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        proc?.WaitForExit();
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
            ?? throw new InvalidOperationException(L.T("bcdedit를 시작하지 못했습니다.", "Failed to start bcdedit."));
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + stderr);
    }
}
