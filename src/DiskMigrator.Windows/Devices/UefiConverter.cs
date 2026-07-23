using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Devices;

/// <summary>UEFI 변환 결과.</summary>
/// <param name="Success">변환 성공 여부.</param>
/// <param name="Message">사람이 읽을 요약.</param>
/// <param name="Steps">실행한 단계와 결과(진단용).</param>
public sealed record UefiConversionResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// BIOS/MBR로 복제된 디스크를 <b>GPT/UEFI로 부팅 가능하게</b> 만듭니다.
/// </summary>
/// <remarks>
/// <para>
/// MBR·활성 파티션 배치의 사본은 레거시(CSM) 부팅을 지원하는 하드웨어에서만 켜집니다.
/// <b>NVMe M.2에는 레거시 부팅용 옵션 ROM이 사실상 없어 어떤 모드로도 부팅되지 않습니다.</b>
/// 요즘 PC로 옮기려면 GPT로 바꾸고 EFI 시스템 파티션(ESP)을 만들어야 합니다.
/// </para>
/// <para>
/// Windows의 <c>mbr2gpt</c>가 그 일을 하지만, 클론 디스크에서는 두 곳에서 걸립니다.
/// 실기에서 하나씩 규명한 것을 그대로 처리합니다:
/// </para>
/// <list type="number">
/// <item><b>끊어진 WinRE 항목</b> — 리사이즈로 복구 파티션이 옮겨지면 BCD의 복구 항목이 옛
/// 위치를 가리킵니다. <c>mbr2gpt</c>는 모든 부팅 항목의 볼륨을 찾으려다 실패하고
/// <c>Cannot find OS partition(s)</c>로 거부합니다. 변환 전에 그 항목들을 지웁니다
/// (부팅 후 <c>reagentc /enable</c>로 다시 만들 수 있습니다).</item>
/// <item><b>ESP의 BCD가 빈 껍데기</b> — <c>mbr2gpt</c>도 <c>bcdboot</c>도 새 저장소를 만들다
/// <c>c0000035</c>(이름 충돌)로 실패하고, 8192바이트짜리 레지스트리가 아닌 파일만 남깁니다.
/// 그 상태로는 부팅되지 않습니다. Windows 파티션의 정상 BIOS 저장소를 ESP로 복사한 뒤
/// UEFI용으로 고쳐 넣습니다.</item>
/// </list>
/// <para>
/// <b>되돌릴 수 없습니다.</b> 파티션 테이블을 GPT로 바꾸므로 호출하는 쪽이 사용자 확인을
/// 받아야 합니다. 실행 중인 시스템 디스크에는 절대 적용하지 않습니다.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UefiConverter(WindowsDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 이 디스크가 UEFI 변환 대상인지. 판정은 <see cref="DiskInfo.IsBiosOnlyBootLayout"/> 하나만
    /// 씁니다 — 시작 전 경고와 같은 근거여야 "경고는 하는데 변환 버튼은 없는" 상태가 안 생깁니다.
    /// </summary>
    public static bool NeedsConversion(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.IsBiosOnlyBootLayout;
    }

    public async Task<UefiConversionResult> ConvertAsync(DiskInfo disk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var steps = new List<string>();

        if (disk.IsSystemDisk || disk.IsBootDisk)
            return new(false, "실행 중인 시스템 디스크는 변환할 수 없습니다.", steps);

        if (disk.PartitionStyle != PartitionStyle.Mbr)
            return new(false, $"이미 {disk.PartitionStyle} 디스크입니다. 변환할 것이 없습니다.", steps);

        var windows = FindWindowsPartition(disk);
        if (windows is null)
            return new(false, "\\Windows가 있는 파티션을 찾지 못했습니다 (볼륨이 마운트되어야 합니다).", steps);

        // 임시로 부여한 드라이브 문자들 — 끝에 모두 뗍니다.
        var temps = new List<char>();
        try
        {
            char winLetter;
            if (windows.DriveLetter is { } wl) winLetter = wl[0];
            else
            {
                char? t = AssignTempLetter(windows.VolumeGuidPath!);
                if (t is not { } tl) throw new InvalidOperationException("Windows 파티션에 임시 문자를 부여하지 못했습니다.");
                winLetter = tl; temps.Add(tl);
            }
            steps.Add($"Windows → {winLetter}:");

            // BIOS BCD(\Boot\BCD)는 보통 별도 "시스템 예약"(활성) 파티션에 있고, 단일 파티션
            // 설치일 때만 Windows 파티션에 있습니다. Windows 파티션만 보던 예전 코드는 시스템
            // 예약 레이아웃에서 "BCD를 찾지 못했습니다"로 실패했습니다 — 실기에서 규명.
            if (LocateBcdPartition(disk, windows, winLetter, temps, steps) is not { } bcdLetter)
                return new(false, "부팅 구성(BCD)을 담은 파티션을 찾지 못했습니다.", steps);
            string biosBcd = $@"{bcdLetter}:\Boot\BCD";

            DropRecoveryEntries(biosBcd, steps);

            // mbr2gpt는 변환 전에 스스로 검사합니다. 먼저 물어보고 안 되면 손대지 않습니다 —
            // 파티션 테이블을 바꾸다 중간에 멈추는 것보다 시작 전에 멈추는 편이 낫습니다.
            var (vCode, vOut) = RunProcess("mbr2gpt.exe", "/validate", $"/disk:{disk.DeviceNumber}", "/allowFullOS");
            steps.Add($"mbr2gpt /validate → exit={vCode}");
            if (vCode != 0)
            {
                string reason = MeaningfulError(vOut);
                _logger.LogWarning("mbr2gpt /validate 실패 (exit {Code}): {Out}", vCode, vOut.Trim());
                return new(false,
                    $"UEFI 변환 전 검사(mbr2gpt)에 실패했습니다: {reason}",
                    steps);
            }

            var (cCode, cOut) = RunProcess("mbr2gpt.exe", "/convert", $"/disk:{disk.DeviceNumber}", "/allowFullOS");
            steps.Add($"mbr2gpt /convert → exit={cCode}");
            _logger.LogInformation("mbr2gpt 변환 출력: {Output}", cOut.Trim());

            // 종료 코드 100은 "변환은 됐지만 부팅 항목은 직접 넣어라"입니다 — 아래에서 넣습니다.
            if (cCode is not (0 or 100))
                return new(false, $"변환에 실패했습니다. {FirstLine(cOut)}", steps);

            await Task.Delay(2000, ct);

            var converted = (await diskService.EnumerateDisksAsync(ct))
                .FirstOrDefault(d => d.DeviceNumber == disk.DeviceNumber);

            if (converted is null || converted.PartitionStyle != PartitionStyle.Gpt)
                return new(false, "변환 후 디스크가 GPT로 보이지 않습니다.", steps);
            steps.Add("파티션 형식 → GPT");

            string espMessage = BuildUefiBcd(converted, winLetter, biosBcd, steps);

            return new(true,
                $"이 디스크를 GPT/UEFI로 변환했습니다. {espMessage} " +
                "이제 UEFI 모드로 부팅할 수 있습니다 — BIOS에서 CSM을 끄고 UEFI 항목으로 부팅하십시오. " +
                "부팅 후 관리자 명령 프롬프트에서 reagentc /enable 을 한 번 실행하면 복구 환경이 다시 연결됩니다.",
                steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UEFI 변환 중 오류.");
            return new(false, $"변환 중 오류: {ex.Message}", steps);
        }
        finally
        {
            foreach (char t in temps) TryRemoveLetter(t);
        }
    }

    /// <summary>
    /// BIOS BCD(<c>\Boot\BCD</c>)가 있는 파티션을 찾아 임시 문자를 부여하고 그 드라이브 문자를
    /// 돌려줍니다. 활성("시스템 예약") 파티션을 먼저 보고, 없으면 Windows 파티션, 그다음 나머지.
    /// </summary>
    /// <remarks>
    /// System Reserved 레이아웃에서는 BCD가 Windows 파티션이 아니라 활성 파티션에 있습니다.
    /// 임시로 붙인 문자만 <paramref name="temps"/>에 넣어 호출부가 끝에 되돌리게 합니다.
    /// </remarks>
    private char? LocateBcdPartition(DiskInfo disk, PartitionInfo windows, char winLetter, List<char> temps, List<string> steps)
    {
        // 단일 파티션 설치: 이미 마운트된 Windows 파티션에 BCD가 있으면 그대로.
        if (File.Exists($@"{winLetter}:\Boot\BCD"))
        {
            steps.Add($"BCD → {winLetter}: (Windows 파티션)");
            return winLetter;
        }

        // 활성(시스템 예약) 우선, EFI·Windows 파티션 제외한 나머지.
        var candidates = disk.Partitions
            .Where(p => !p.IsEfiSystemPartition && p.Number != windows.Number)
            .OrderByDescending(p => p.IsActive);

        foreach (var p in candidates)
        {
            char letter;
            bool temp = false;
            if (p.DriveLetter is { } dl) letter = dl[0];
            else if (p.VolumeGuidPath is { } gp && AssignTempLetter(gp) is { } al) { letter = al; temp = true; }
            else continue;

            if (File.Exists($@"{letter}:\Boot\BCD"))
            {
                if (temp) temps.Add(letter);
                steps.Add($"BCD → {letter}: (시스템 예약 파티션)");
                return letter;
            }
            if (temp) TryRemoveLetter(letter); // 여기엔 없었으니 임시 문자 반환
        }
        return null;
    }

    /// <summary>
    /// 끊어진 복구(WinRE) 부팅 항목을 지웁니다 — 이것이 있으면 <c>mbr2gpt</c>가 거부합니다.
    /// </summary>
    /// <remarks>
    /// 리사이즈로 복구 파티션이 옮겨지면 BCD의 복구 항목이 옛 위치를 가리키고, mbr2gpt는
    /// 모든 부팅 항목의 볼륨을 찾으려다 <c>Cannot find OS partition(s)</c>로 멈춥니다.
    /// WinRE는 어차피 끊어진 상태이며 부팅 후 <c>reagentc /enable</c>로 다시 만들 수 있습니다.
    /// </remarks>
    private void DropRecoveryEntries(string bcdPath, List<string> steps)
    {
        int dropped = 0;

        // 1) {default}·{current}의 recoverysequence가 가리키는 복구 항목을 GUID로 직접 지웁니다.
        //    복구 파티션이 마운트되지 않아 device가 해석되지 않는 항목이라도 GUID로는 삭제됩니다 —
        //    이것이 텍스트 검색 방식(enum 실패로 놓치던)이 못 지우던 바로 그 항목입니다.
        //    recoverysequence를 지우기 '전에' GUID를 읽어야 대상을 알 수 있습니다.
        foreach (string owner in new[] { "{default}", "{current}" })
        {
            var (_, ownerOut) = RunProcess("bcdedit.exe", "/store", bcdPath, "/enum", owner);
            foreach (Match m in Regex.Matches(ownerOut, @"recoverysequence\s+(\{[0-9a-fA-F-]{36}\})"))
            {
                var (code, _) = RunProcess("bcdedit.exe", "/store", bcdPath, "/delete", m.Groups[1].Value, "/f");
                if (code == 0) dropped++;
            }
            RunProcess("bcdedit.exe", "/store", bcdPath, "/deletevalue", owner, "recoverysequence");
            RunProcess("bcdedit.exe", "/store", bcdPath, "/set", owner, "recoveryenabled", "No");
        }

        // 2) 백스톱: 남아 있는 복구/WinRE 항목을 설명·ramdisk 단서로 한 번 더 훑어 지웁니다.
        //    메인 Windows 항목({default})과 {bootmgr}은 이 단서에 걸리지 않아 안전합니다.
        var (_, all) = RunProcess("bcdedit.exe", "/store", bcdPath, "/enum", "ALL");
        var guids = Regex.Matches(all, @"\{[0-9a-fA-F-]{36}\}").Select(m => m.Value).Distinct();
        foreach (string guid in guids)
        {
            var (_, one) = RunProcess("bcdedit.exe", "/store", bcdPath, "/enum", guid);
            bool looksRecovery =
                one.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ||
                one.Contains("복구", StringComparison.Ordinal) ||
                one.Contains("winre", StringComparison.OrdinalIgnoreCase) ||
                one.Contains("ramdisk", StringComparison.OrdinalIgnoreCase);
            if (!looksRecovery) continue;

            var (code, _) = RunProcess("bcdedit.exe", "/store", bcdPath, "/delete", guid, "/f");
            if (code == 0) dropped++;
        }

        steps.Add($"끊어진 복구 항목 정리: {dropped}개");
    }

    /// <summary>
    /// ESP에 쓸 수 있는 UEFI BCD를 만듭니다.
    /// </summary>
    /// <remarks>
    /// <c>mbr2gpt</c>와 <c>bcdboot</c>은 새 저장소를 만들다 <c>c0000035</c>로 실패하고 8192바이트짜리
    /// 레지스트리가 아닌 파일을 남깁니다(실기에서 확인). 그래서 먼저 <c>bcdboot</c>을 시도해 보고,
    /// 결과가 열리지 않는 저장소면 Windows 파티션의 정상 BIOS 저장소를 복사해 UEFI용으로 고칩니다.
    /// 고칠 것은 넷뿐입니다 — 부팅 관리자의 위치·경로, OS 로더의 장치·경로.
    /// </remarks>
    private string BuildUefiBcd(DiskInfo converted, char winLetter, string biosBcdSource, List<string> steps)
    {
        var esp = converted.Partitions.FirstOrDefault(p => p.IsEfiSystemPartition);
        if (esp?.VolumeGuidPath is null && esp?.DriveLetter is null)
        {
            steps.Add("ESP를 찾지 못했습니다");
            return "다만 EFI 시스템 파티션을 찾지 못해 부팅 파일을 넣지 못했습니다.";
        }

        char? espTemp = null;
        try
        {
            char espLetter = esp.DriveLetter is { } el ? el[0] : (espTemp = AssignTempLetter(esp.VolumeGuidPath!)) ?? '\0';
            if (espLetter == '\0')
            {
                steps.Add("ESP에 임시 문자를 부여하지 못했습니다");
                return "다만 EFI 시스템 파티션을 마운트하지 못해 부팅 파일을 넣지 못했습니다.";
            }
            steps.Add($"ESP → {espLetter}:");

            string espBcd = $@"{espLetter}:\EFI\Microsoft\Boot\BCD";

            RunProcess("bcdboot.exe", $@"{winLetter}:\Windows", "/s", $"{espLetter}:", "/f", "UEFI");

            if (StoreOpens(espBcd))
            {
                steps.Add("bcdboot이 만든 저장소 사용");
                return "EFI 시스템 파티션에 UEFI 부팅 파일을 넣었습니다.";
            }

            // 여기부터가 실기에서 필요했던 우회입니다.
            steps.Add("bcdboot 저장소가 열리지 않음 — 정상 저장소를 복사해 고칩니다");

            Directory.CreateDirectory(Path.GetDirectoryName(espBcd)!);
            File.Copy(biosBcdSource, espBcd, overwrite: true);

            (string id, string element, string value)[] sets =
            [
                ("{bootmgr}", "device",   $"partition={espLetter}:"),
                ("{bootmgr}", "path",     @"\EFI\Microsoft\Boot\bootmgfw.efi"),
                ("{default}", "device",   $"partition={winLetter}:"),
                ("{default}", "osdevice", $"partition={winLetter}:"),
                ("{default}", "path",     @"\WINDOWS\system32\winload.efi"),
            ];

            foreach (var (id, element, value) in sets)
            {
                var (code, _) = RunProcess("bcdedit.exe", "/store", espBcd, "/set", id, element, value);
                if (code != 0) steps.Add($"set {id} {element} 실패({code})");
            }

            return StoreOpens(espBcd)
                ? "EFI 시스템 파티션에 UEFI 부팅 파일을 넣었습니다(정상 저장소를 옮겨 고침)."
                : "다만 UEFI 부팅 구성을 만들지 못했습니다 — 완료 화면의 '부팅 검사'로 확인하십시오.";
        }
        finally
        {
            if (espTemp is { } e) TryRemoveLetter(e);
        }
    }

    /// <summary>bcdedit이 이 저장소를 열 수 있는지 — 껍데기 파일과 진짜 저장소를 가릅니다.</summary>
    private bool StoreOpens(string bcdPath)
    {
        if (!File.Exists(bcdPath)) return false;
        var (code, _) = RunProcess("bcdedit.exe", "/store", bcdPath, "/enum", "{bootmgr}");
        return code == 0;
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
            if (NativeMethods.SetVolumeMountPoint($"{c}:\\", vol)) return c;
        }
        return null;
    }

    private void TryRemoveLetter(char letter)
    {
        if (!NativeMethods.DeleteVolumeMountPoint($"{letter}:\\"))
            _logger.LogWarning("임시 마운트 {Letter}: 해제 실패(무해).", letter);
    }

    private static string FirstLine(string output) =>
        output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

    /// <summary>
    /// mbr2gpt 출력에서 사람에게 의미 있는 오류 줄을 고릅니다.
    /// </summary>
    /// <remarks>
    /// mbr2gpt는 진행 상황("Attempting to validate...", "Retrieving layout...")을 여러 줄 찍고
    /// 실제 실패 이유("Cannot find OS partition(s)...")를 뒤에 붙입니다. 첫 줄만 보여 주던 탓에
    /// 사용자는 "Attempting to validate disk 6"이라는 무의미한 문구만 봤습니다. 오류로 보이는
    /// 줄(없으면 마지막 비어있지 않은 줄)을 돌려줍니다.
    /// </remarks>
    private static string MeaningfulError(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "(출력 없음)";

        string? err = lines.LastOrDefault(l =>
            l.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("fail", StringComparison.OrdinalIgnoreCase));
        return err ?? lines[^1];
    }

    private static (int Code, string Output) RunProcess(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "");

        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
