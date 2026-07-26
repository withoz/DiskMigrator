using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiskMigrator.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Pe;

/// <summary>부팅 미디어 빌드 결과.</summary>
/// <param name="Success">스테이징 폴더가 완성됐는지.</param>
/// <param name="MediaRoot">완성된 미디어 루트(USB에 그대로 복사하면 되는 폴더). 실패 시 null.</param>
/// <param name="BootWimBytes">주입 후 boot.wim 크기.</param>
/// <param name="Message">사람이 읽을 요약.</param>
public sealed record WinPeBuildResult(bool Success, string? MediaRoot, long BootWimBytes, string Message);

/// <summary>
/// 시스템 내장 재료(<see cref="WinPeIngredients"/>)로 <b>부팅 가능한 미디어 폴더</b>를 조립합니다 —
/// ADK 없이. 이 폴더를 FAT32 USB에 그대로 복사하면 UEFI로 부팅됩니다.
/// </summary>
/// <remarks>
/// 만드는 구조(UEFI 표준 기법):
/// <code>
/// 미디어루트\
///   EFI\Boot\bootx64.efi          ← bootmgfw.efi 복사(UEFI 폴백 부트로더 경로)
///   EFI\Microsoft\Boot\BCD        ← bcdedit로 생성한 램디스크 부팅 구성
///   boot\boot.sdi                 ← 램디스크 장치 이미지
///   sources\boot.wim              ← Winre.wim 사본 + 우리 앱 주입
/// </code>
/// 앱 주입: DISM으로 boot.wim을 마운트해 자체 포함 단일 exe를 넣고,
/// <c>Windows\System32\winpeshl.ini</c>를 바꿔 부팅하면 ① wpeinit(장치 인식) →
/// ② DiskMigrator 자동 실행 → ③ (앱 종료/실패 시) 명령 프롬프트 폴백 순서로 뜨게 합니다.
///
/// <para>이 클래스는 <b>작업 폴더 안에만</b> 씁니다 — 디스크·USB는 건드리지 않습니다.
/// USB에 쓰는 파괴적 단계는 별도 클래스가 맡습니다. DISM 마운트에 관리자 권한이 필요합니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinPeMediaBuilder(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>진행 알림: (사람이 읽을 문구, 이 빌더 안에서의 진행률 0~1).</summary>
    /// <remarks>
    /// DISM 커밋처럼 내부 진행을 알 수 없는 단계는 시작 시점의 값에 머뭅니다 — 문구가
    /// "잠시 걸립니다"로 그 사정을 설명합니다. 큰 파일 복사(winre.wim)는 10% 단위로 움직입니다.
    /// </remarks>
    public event Action<string, double>? Progress;

    /// <param name="ingredients">재료 탐지 결과(<see cref="WinPeIngredientsReport.AllFound"/>여야 함).</param>
    /// <param name="appExePath">주입할 자체 포함 단일 exe(publish 산출물).</param>
    /// <param name="workRoot">작업 폴더(이 안에만 씀). 완성 미디어는 <c>{workRoot}\media</c>.</param>
    public async Task<WinPeBuildResult> BuildAsync(
        WinPeIngredientsReport ingredients, string appExePath, string workRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ingredients);
        if (!ingredients.AllFound)
            return new(false, null, 0, L.T(
                "재료가 부족해 미디어를 만들 수 없습니다 (--pe-check 참조).",
                "Missing ingredients — cannot build the media (see --pe-check)."));
        if (!File.Exists(appExePath))
            return new(false, null, 0, L.T(
                $"주입할 앱 실행 파일이 없습니다: {appExePath}",
                $"The app executable to inject was not found: {appExePath}"));

        string media = Path.Combine(workRoot, "media");
        string mount = Path.Combine(workRoot, "mount");
        string bootWim = Path.Combine(media, "sources", "boot.wim");

        try
        {
            // --- 0) 작업 폴더 준비 ---------------------------------------------
            Report(L.T("작업 폴더 준비", "Preparing work folder"), 0.00);
            if (Directory.Exists(workRoot)) TryCleanup(workRoot, mount);
            Directory.CreateDirectory(Path.Combine(media, "sources"));
            Directory.CreateDirectory(Path.Combine(media, "boot"));
            Directory.CreateDirectory(Path.Combine(media, @"EFI\Boot"));
            Directory.CreateDirectory(Path.Combine(media, @"EFI\Microsoft\Boot"));
            Directory.CreateDirectory(mount);

            // --- 1) 재료 복사 ---------------------------------------------------
            Report(L.T("복구 환경 이미지 복사 (수백 MB — 잠시 걸립니다)",
                       "Copying recovery image (hundreds of MB — this takes a moment)"), 0.02);
            await Task.Run(() => CopyFile(ingredients.WinreWimPath!, bootWim, ct,
                f => Report(L.T($"복구 환경 이미지 복사 ({f:P0})",
                                $"Copying recovery image ({f:P0})"), 0.02 + 0.28 * f)), ct);
            // Winre.wim은 원본 위치에서 읽기 전용 속성이 있을 수 있어, 사본은 수정 가능하게 풉니다.
            File.SetAttributes(bootWim, FileAttributes.Normal);

            CopyFile(ingredients.BootSdiPath!, Path.Combine(media, "boot", "boot.sdi"));
            CopyFile(ingredients.BootMgfwEfiPath!, Path.Combine(media, @"EFI\Boot", "bootx64.efi"));

            // --- 2) 앱 주입 (DISM 마운트) --------------------------------------
            Report(L.T("이미지 열기 (DISM 마운트)", "Opening image (DISM mount)"), 0.32);
            RunOrThrow(ingredients.DismPath!,
                $"/Mount-Wim /WimFile:\"{bootWim}\" /Index:1 /MountDir:\"{mount}\"", ct);

            bool committed = false;
            try
            {
                Report(L.T("DiskMigrator 앱 넣기", "Injecting the DiskMigrator app"), 0.48);
                string appDir = Path.Combine(mount, "DiskMigrator");
                Directory.CreateDirectory(appDir);
                CopyFile(appExePath, Path.Combine(appDir, "DiskMigrator.exe"));

                // 부팅 순서: 장치 인식(wpeinit) → 우리 앱 → 폴백 셸.
                // winpeshl.ini는 [LaunchApps]의 항목을 차례로 실행하며 마지막이 끝나면 재부팅합니다.
                string winpeshl = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
                File.WriteAllText(winpeshl,
                    "[LaunchApps]\r\n" +
                    "wpeinit\r\n" +
                    "%SYSTEMDRIVE%\\DiskMigrator\\DiskMigrator.exe\r\n" +
                    "cmd.exe\r\n",
                    new UTF8Encoding(false));

                Report(L.T("이미지 저장 (DISM 커밋 — 몇 분 걸릴 수 있습니다)",
                           "Saving image (DISM commit — this can take a few minutes)"), 0.52);
                RunOrThrow(ingredients.DismPath!, $"/Unmount-Wim /MountDir:\"{mount}\" /Commit", ct);
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try { Run(ingredients.DismPath!, $"/Unmount-Wim /MountDir:\"{mount}\" /Discard", ct, out _); }
                    catch { /* 정리 실패는 무시 — 다음 빌드의 TryCleanup이 다시 시도 */ }
                }
            }

            // --- 3) 부팅 구성(BCD) 생성 ----------------------------------------
            Report(L.T("부팅 구성(BCD) 만들기", "Creating boot configuration (BCD)"), 0.92);
            BuildBcd(ingredients.BcdeditPath!, Path.Combine(media, @"EFI\Microsoft\Boot", "BCD"), ct);

            long wimBytes = new FileInfo(bootWim).Length;
            _logger.LogInformation("부팅 미디어 완성: {Media} (boot.wim {Bytes:N0}바이트)", media, wimBytes);
            return new(true, media, wimBytes, L.T(
                $"부팅 미디어가 준비됐습니다. boot.wim {wimBytes / 1048576.0:F0} MB.",
                $"The boot media is ready. boot.wim {wimBytes / 1048576.0:F0} MB."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 미디어 빌드 실패.");
            return new(false, null, 0, L.T(
                $"부팅 미디어 빌드에 실패했습니다: {ex.Message}",
                $"Building the boot media failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// USB 없이 만든 미디어로 램디스크 부팅 BCD를 생성합니다.
    /// </summary>
    /// <remarks>
    /// <c>[boot]</c>는 "부팅한 그 장치"라는 뜻이라 USB가 어떤 문자를 받든 동작합니다.
    /// 부트 메뉴 없이 바로 우리 이미지로 들어가게 timeout을 0으로 둡니다.
    /// </remarks>
    private void BuildBcd(string bcdedit, string storePath, CancellationToken ct)
    {
        if (File.Exists(storePath)) File.Delete(storePath);

        RunOrThrow(bcdedit, $"/createstore \"{storePath}\"", ct);
        string s = $"/store \"{storePath}\"";

        RunOrThrow(bcdedit, $"{s} /create {{ramdiskoptions}} /d \"DiskMigrator Ramdisk\"", ct);
        RunOrThrow(bcdedit, $"{s} /set {{ramdiskoptions}} ramdisksdidevice boot", ct);
        RunOrThrow(bcdedit, $"{s} /set {{ramdiskoptions}} ramdisksdipath \\boot\\boot.sdi", ct);

        // osloader 항목을 만들고 bcdedit가 출력한 새 GUID를 받아 씁니다.
        Run(bcdedit, $"{s} /create /d \"DiskMigrator 부팅 도구\" /application osloader", ct, out string createOut);
        var m = Regex.Match(createOut, @"\{[0-9a-fA-F-]{36}\}");
        if (!m.Success)
            throw new InvalidOperationException(L.T(
                $"bcdedit가 새 항목 GUID를 돌려주지 않았습니다: {createOut.Trim()}",
                $"bcdedit did not return a new entry GUID: {createOut.Trim()}"));
        string guid = m.Value;

        RunOrThrow(bcdedit, $"{s} /set {guid} device ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}", ct);
        RunOrThrow(bcdedit, $"{s} /set {guid} osdevice ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}", ct);
        RunOrThrow(bcdedit, $"{s} /set {guid} path \\windows\\system32\\winload.efi", ct);
        RunOrThrow(bcdedit, $"{s} /set {guid} systemroot \\windows", ct);
        RunOrThrow(bcdedit, $"{s} /set {guid} detecthal yes", ct);
        RunOrThrow(bcdedit, $"{s} /set {guid} winpe yes", ct);

        RunOrThrow(bcdedit, $"{s} /create {{bootmgr}} /d \"Boot Manager\"", ct);
        RunOrThrow(bcdedit, $"{s} /set {{bootmgr}} default {guid}", ct);
        RunOrThrow(bcdedit, $"{s} /set {{bootmgr}} timeout 0", ct);
    }

    /// <summary>이전 빌드 잔재를 지웁니다. 마운트가 남아 있으면 먼저 버립니다.</summary>
    private void TryCleanup(string workRoot, string mount)
    {
        try
        {
            if (Directory.Exists(mount) && Directory.EnumerateFileSystemEntries(mount).Any())
            {
                // 이전 실행이 중단돼 마운트가 남은 경우 — 버리고 계속합니다.
                Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\Dism.exe"),
                    $"/Unmount-Wim /MountDir:\"{mount}\" /Discard", CancellationToken.None, out _);
            }
        }
        catch { /* 남은 마운트가 없으면 그만 */ }

        try { Directory.Delete(workRoot, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "이전 작업 폴더 정리 실패 — 계속 진행합니다."); }
    }

    private void Report(string step, double fraction)
    {
        _logger.LogInformation("부팅 미디어: {Step}", step);
        Progress?.Invoke(step, fraction);
    }

    /// <summary>볼륨 GUID·GLOBALROOT 경로도 다루도록 스트림 복사를 씁니다.</summary>
    private static void CopyFile(string source, string destination,
        CancellationToken ct = default, Action<double>? onProgress = null)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        if (onProgress is null || src.Length == 0)
        {
            src.CopyTo(dst, 1 << 20);
            return;
        }

        // 큰 파일(winre.wim 수백 MB)은 10% 단위로 진행을 알립니다.
        var buffer = new byte[1 << 20];
        long copied = 0;
        int lastDecile = -1;
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, read);
            copied += read;
            int decile = (int)(copied * 10 / src.Length);
            if (decile > lastDecile)
            {
                lastDecile = decile;
                onProgress(copied / (double)src.Length);
            }
        }
    }

    private void RunOrThrow(string exe, string args, CancellationToken ct)
    {
        int code = Run(exe, args, ct, out string output);
        if (code != 0)
        {
            throw new InvalidOperationException(L.T(
                $"{Path.GetFileName(exe)} {args} 실패 (종료코드 {code}): {Truncate(output)}",
                $"{Path.GetFileName(exe)} {args} failed (exit code {code}): {Truncate(output)}"));
        }
    }

    private int Run(string exe, string args, CancellationToken ct, out string output)
    {
        _logger.LogInformation("실행: {Exe} {Args}", Path.GetFileName(exe), args);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException(L.T(
                $"{exe}를 시작하지 못했습니다.", $"Failed to start {exe}."));
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        // DISM 커밋은 수 분 걸릴 수 있습니다. 취소 요청이 오면 프로세스를 죽입니다.
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 이미 종료 */ }
                ct.ThrowIfCancellationRequested();
            }
        }
        return p.ExitCode;
    }

    private static string Truncate(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
