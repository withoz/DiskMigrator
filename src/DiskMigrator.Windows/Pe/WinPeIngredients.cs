using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Pe;

/// <summary>부팅 USB(WinPE) 제작에 필요한 재료를 시스템에서 찾은 결과.</summary>
/// <param name="WinreWimPath">WinPE의 본체가 될 Winre.wim 경로(복구 환경 이미지). 없으면 null.</param>
/// <param name="WinreWimBytes">Winre.wim 크기(바이트).</param>
/// <param name="BootSdiPath">램디스크 부팅에 필요한 boot.sdi 경로. 없으면 null.</param>
/// <param name="BootMgfwEfiPath">UEFI 부트로더(bootmgfw.efi) 경로. 없으면 null.</param>
/// <param name="DismPath">이미지 마운트·수정에 쓸 DISM 경로. 없으면 null.</param>
/// <param name="BcdeditPath">부팅 구성(BCD) 생성에 쓸 bcdedit 경로. 없으면 null.</param>
/// <param name="Notes">사람이 읽을 진행/문제 메모.</param>
public sealed record WinPeIngredientsReport(
    string? WinreWimPath, long WinreWimBytes,
    string? BootSdiPath, string? BootMgfwEfiPath,
    string? DismPath, string? BcdeditPath,
    IReadOnlyList<string> Notes)
{
    /// <summary>부팅 USB를 만들 재료가 모두 갖춰졌는지.</summary>
    public bool AllFound =>
        WinreWimPath is not null && BootSdiPath is not null &&
        BootMgfwEfiPath is not null && DismPath is not null && BcdeditPath is not null;
}

/// <summary>
/// 부팅 USB(WinPE) 제작 재료를 <b>ADK 설치 없이</b> 현재 시스템에서 찾아냅니다.
/// </summary>
/// <remarks>
/// 핵심 설계: 수 GB짜리 Windows ADK를 내려받게 하지 않습니다. 모든 Windows에는 이미
/// 복구 환경(WinRE)이 들어 있고, 그것이 곧 WinPE입니다:
/// <list type="bullet">
/// <item><b>Winre.wim</b> — WinPE 이미지 본체. 보통 숨겨진 복구 파티션의
///   <c>\Recovery\WindowsRE\</c>에 있습니다(드라이브 문자가 없어 볼륨 GUID 경로로 접근 —
///   관리자 권한 필요). RE가 꺼진 시스템에서는 <c>C:\Windows\System32\Recovery\</c>.</item>
/// <item><b>boot.sdi</b> — WIM을 램디스크로 부팅할 때 필요한 장치 이미지. Winre.wim 옆이나
///   <c>C:\Windows\Boot\DVD\EFI\</c>에 있습니다.</item>
/// <item><b>bootmgfw.efi</b> — UEFI 부트로더. <c>C:\Windows\Boot\EFI\</c>에 있으며 USB의
///   <c>\EFI\Boot\bootx64.efi</c>로 복사해 쓰는 표준 기법입니다.</item>
/// <item><b>DISM·bcdedit</b> — 이미지 수정과 BCD 생성. Windows 내장.</item>
/// </list>
/// 이 클래스는 <b>읽기 전용</b>입니다 — 찾기만 하고 아무것도 수정하지 않습니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinPeIngredients(IDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>재료를 찾습니다(읽기 전용). 관리자 권한이 아니면 복구 파티션을 못 볼 수 있습니다.</summary>
    public async Task<WinPeIngredientsReport> LocateAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // --- 1) Winre.wim -------------------------------------------------------
        string? winre = null;

        // (a) reagentc /info가 알려주는 공식 위치(\\?\GLOBALROOT\device\harddiskN\partitionM\...).
        //     레이블은 언어별로 다르지만 경로 형식은 같아 정규식으로 뽑습니다.
        string? reagentDir = TryParseReagentPath(notes);
        if (reagentDir is not null)
        {
            string candidate = Path.Combine(reagentDir, "Winre.wim");
            if (TryFile(candidate, out _))
            {
                winre = candidate;
                notes.Add(L.T($"Winre.wim: 복구 환경 등록 위치에서 발견 ({reagentDir})",
                              $"Winre.wim: found at the registered recovery location ({reagentDir})"));
            }
        }

        // (b) 복구 파티션들을 볼륨 GUID 경로로 직접 확인(문자 없는 숨김 파티션 접근).
        if (winre is null)
        {
            try
            {
                var disks = await diskService.EnumerateDisksAsync(ct);
                foreach (var p in disks.SelectMany(d => d.Partitions)
                             .Where(p => p.IsWindowsRecovery && p.VolumeGuidPath is not null))
                {
                    string candidate = p.VolumeGuidPath!.TrimEnd('\\') + @"\Recovery\WindowsRE\Winre.wim";
                    if (TryFile(candidate, out _))
                    {
                        winre = candidate;
                        notes.Add(L.T($"Winre.wim: 복구 파티션에서 발견 (파티션 {p.Number})",
                                      $"Winre.wim: found on the recovery partition (partition {p.Number})"));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add(L.T($"복구 파티션 탐색 실패: {ex.Message}",
                              $"Failed to scan recovery partitions: {ex.Message}"));
            }
        }

        // (c) RE가 꺼진 시스템: Winre.wim이 C:\Windows 안에 남아 있습니다.
        if (winre is null)
        {
            string candidate = Path.Combine(windir, @"System32\Recovery\Winre.wim");
            if (TryFile(candidate, out _))
            {
                winre = candidate;
                notes.Add(L.T("Winre.wim: Windows 폴더에서 발견 (복구 환경 비활성 상태)",
                              "Winre.wim: found in the Windows folder (recovery environment disabled)"));
            }
        }

        long winreBytes = 0;
        if (winre is not null) TryFile(winre, out winreBytes);
        else notes.Add(L.T(
            "Winre.wim을 찾지 못했습니다 — 관리자 권한인지, 복구 환경(reagentc /info)이 있는지 확인하세요.",
            "Winre.wim was not found — check administrator rights and whether the recovery environment exists (reagentc /info)."));

        // --- 2) boot.sdi --------------------------------------------------------
        string? bootSdi = null;
        if (winre is not null)
        {
            string beside = Path.Combine(Path.GetDirectoryName(winre)!, "boot.sdi");
            if (TryFile(beside, out _)) bootSdi = beside;
        }
        bootSdi ??= FirstExisting(
            Path.Combine(windir, @"Boot\DVD\EFI\boot.sdi"),
            Path.Combine(windir, @"Boot\DVD\PCAT\boot.sdi"));
        if (bootSdi is null) notes.Add(L.T("boot.sdi를 찾지 못했습니다.", "boot.sdi was not found."));

        // --- 3) UEFI 부트로더 ----------------------------------------------------
        string? bootmgfw = FirstExisting(Path.Combine(windir, @"Boot\EFI\bootmgfw.efi"));
        if (bootmgfw is null) notes.Add(L.T("bootmgfw.efi(UEFI 부트로더)를 찾지 못했습니다.",
                                            "bootmgfw.efi (UEFI boot loader) was not found."));

        // --- 4) 도구 ------------------------------------------------------------
        string sys32 = Path.Combine(windir, "System32");
        string? dism = FirstExisting(Path.Combine(sys32, "Dism.exe"));
        string? bcdedit = FirstExisting(Path.Combine(sys32, "bcdedit.exe"));
        if (dism is null) notes.Add(L.T("DISM을 찾지 못했습니다.", "DISM was not found."));
        if (bcdedit is null) notes.Add(L.T("bcdedit를 찾지 못했습니다.", "bcdedit was not found."));

        var report = new WinPeIngredientsReport(winre, winreBytes, bootSdi, bootmgfw, dism, bcdedit, notes);
        _logger.LogInformation(
            "WinPE 재료 탐지: Winre.wim={Winre} ({Size:N0}바이트), boot.sdi={Sdi}, bootmgfw.efi={Efi}, 완비={All}",
            winre ?? "없음", winreBytes, bootSdi ?? "없음", bootmgfw ?? "없음", report.AllFound);
        return report;
    }

    /// <summary>reagentc /info 출력에서 WinRE 디렉터리(\\?\GLOBALROOT\...)를 뽑습니다. 실패 시 null.</summary>
    private string? TryParseReagentPath(List<string> notes)
    {
        try
        {
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\ReAgentc.exe"),
                "/info")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);

            // 예: \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE
            var m = Regex.Match(output, @"\\\\\?\\GLOBALROOT\\device\\harddisk\d+\\partition\d+\\\S*",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }
        catch (Exception ex)
        {
            notes.Add(L.T($"reagentc 조회 실패(무해 — 다른 경로로 계속): {ex.Message}",
                          $"reagentc query failed (harmless — continuing via other paths): {ex.Message}"));
            return null;
        }
    }

    private static string? FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);

    /// <summary>File.Exists는 \\?\GLOBALROOT 경로에서 false가 날 수 있어, 직접 열어 확인합니다.</summary>
    private static bool TryFile(string path, out long length)
    {
        length = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = fs.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
