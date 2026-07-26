using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Pe;

/// <summary>USB 기록 결과.</summary>
/// <param name="Success">부팅 USB가 완성됐는지.</param>
/// <param name="DriveLetter">완성된 USB의 드라이브 문자(확인용). 실패 시 null.</param>
/// <param name="Message">사람이 읽을 요약.</param>
public sealed record UsbWriteResult(bool Success, string? DriveLetter, string Message);

/// <summary>
/// 완성된 부팅 미디어 폴더(<see cref="WinPeMediaBuilder"/>)를 USB에 씁니다 — <b>USB의 기존
/// 내용은 모두 지워집니다</b>.
/// </summary>
/// <remarks>
/// UEFI는 FAT32 파티션의 <c>\EFI\Boot\bootx64.efi</c>를 찾아 부팅하므로, USB를 FAT32로 새로
/// 포맷하고 미디어를 그대로 복사하면 끝입니다. 두 가지 실무 함정을 처리합니다:
/// <list type="bullet">
/// <item>Windows의 FAT32 포맷은 32GB까지만 됩니다 — 큰 USB에는 32GB 파티션을 만들고 나머지는
///   미할당으로 둡니다(부팅에는 충분).</item>
/// <item>이 클래스는 파괴적입니다 — 호출자가 <b>USB/이동식인지, 시스템 디스크가 아닌지,
///   모델명 확인까지</b> 마친 뒤에만 불러야 합니다. 그래도 여기서 한 번 더 차단합니다.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UsbBootWriter(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>진행 알림: (사람이 읽을 문구, 이 기록기 안에서의 진행률 0~1).</summary>
    public event Action<string, double>? Progress;

    /// <param name="target">지워질 USB 디스크.</param>
    /// <param name="mediaRoot">복사할 미디어 루트(안에 EFI\, boot\, sources\가 있어야 함).</param>
    public async Task<UsbWriteResult> WriteAsync(DiskInfo target, string mediaRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // --- 마지막 안전 관문 -------------------------------------------------
        if (target.IsSystemDisk || target.IsBootDisk || target.HasPageFile)
            return new(false, null, L.T(
                "시스템/부팅/페이지파일 디스크에는 쓸 수 없습니다.",
                "Cannot write to a system/boot/pagefile disk."));
        if (target.BusType != DiskBusType.Usb && !target.IsRemovable)
            return new(false, null, L.T(
                "안전을 위해 USB(이동식) 디스크에만 쓸 수 있습니다.",
                "For safety, only USB (removable) disks can be written to."));

        string bootWimSrc = Path.Combine(mediaRoot, "sources", "boot.wim");
        if (!File.Exists(bootWimSrc))
            return new(false, null, L.T(
                $"미디어가 불완전합니다(boot.wim 없음): {mediaRoot}",
                $"The media is incomplete (boot.wim missing): {mediaRoot}"));

        long neededBytes = new FileInfo(bootWimSrc).Length + (512L << 20); // 여유 512MB
        if (target.SizeBytes < neededBytes)
            return new(false, null, L.T(
                $"USB가 너무 작습니다. 최소 {neededBytes / 1073741824.0:F1} GB 필요.",
                $"The USB is too small. At least {neededBytes / 1073741824.0:F1} GB is required."));

        try
        {
            // --- 1) FAT32로 새로 포맷 (기존 내용 삭제) --------------------------
            Report(L.T("USB 포맷 (FAT32)", "Formatting USB (FAT32)"), 0.02);
            char letter = PickFreeLetter();

            // Windows FAT32 포맷 한계(32GB) 때문에 큰 USB에는 32GB 파티션만 만듭니다.
            long usableMb = Math.Min(target.SizeBytes / (1024 * 1024), 32_000);
            string sizeClause = target.SizeBytes / (1024 * 1024) > 32_000 ? $" size={usableMb}" : "";

            // FAT32 볼륨 레이블은 최대 11자입니다 — 넘으면 VDS가 "레이블이 잘못되었습니다"로
            // 포맷을 거부합니다(실기에서 12자 레이블로 실제 실패).
            RunDiskpart(
                $"select disk {target.DeviceNumber}\r\n" +
                "clean\r\n" +
                $"create partition primary{sizeClause}\r\n" +
                "format fs=fat32 quick label=DMBOOT\r\n" +
                $"assign letter={letter}\r\n", ct);

            string root = $"{letter}:\\";
            if (!Directory.Exists(root))
                return new(false, null, L.T(
                    "포맷 후 USB 볼륨이 나타나지 않았습니다. USB를 다시 연결해 재시도하세요.",
                    "The USB volume did not appear after formatting. Reconnect the USB and try again."));

            // --- 2) 미디어 복사 (boot.wim은 커서 진행률 표시) --------------------
            Report(L.T("부팅 파일 복사", "Copying boot files"), 0.25);
            await Task.Run(() => CopyTree(mediaRoot, root, ct), ct);

            // --- 3) 검증 ---------------------------------------------------------
            Report(L.T("확인", "Verifying"), 0.96);
            string wimDst = Path.Combine(root, "sources", "boot.wim");
            string efiDst = Path.Combine(root, "EFI", "Boot", "bootx64.efi");
            if (!File.Exists(efiDst) || !File.Exists(wimDst) ||
                new FileInfo(wimDst).Length != new FileInfo(bootWimSrc).Length)
            {
                return new(false, null, L.T(
                    "복사 검증에 실패했습니다(파일 누락 또는 크기 불일치). 다시 시도하세요.",
                    "Copy verification failed (missing file or size mismatch). Please try again."));
            }

            _logger.LogInformation("부팅 USB 완성: 디스크 {Num} ({Letter}:)", target.DeviceNumber, letter);
            return new(true, $"{letter}:", L.T(
                $"부팅 USB가 완성됐습니다({letter}:). 대상 PC에서 USB로 UEFI 부팅하면 DiskMigrator가 자동 실행됩니다.",
                $"The boot USB is ready ({letter}:). Boot the target PC from this USB (UEFI) and DiskMigrator starts automatically."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "부팅 USB 기록 실패.");
            return new(false, null, L.T(
                $"부팅 USB 기록에 실패했습니다: {ex.Message}",
                $"Writing the boot USB failed: {ex.Message}"));
        }
    }

    /// <summary>미디어 트리를 복사합니다. 큰 파일(boot.wim)은 조각 복사로 진행률을 알립니다.</summary>
    private void CopyTree(string sourceRoot, string destRoot, CancellationToken ct)
    {
        foreach (string dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, file));

            var info = new FileInfo(file);
            if (info.Length < (64L << 20))
            {
                File.Copy(file, dest, overwrite: true);
                continue;
            }

            // 큰 파일: 진행률을 10% 단위로 알립니다. USB 쓰기는 느릴 수 있어 이 표시가 중요합니다.
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 20];
            long copied = 0;
            int lastPct = -1;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buffer, 0, read);
                copied += read;
                int pct = (int)(copied * 100 / info.Length);
                if (pct / 10 > lastPct / 10)
                {
                    lastPct = pct;
                    // USB 쓰기(0.25~0.95 구간)의 대부분이 이 boot.wim 한 파일입니다.
                    Report(L.T(
                        $"부팅 파일 복사 ({Path.GetFileName(file)} {pct}%)",
                        $"Copying boot files ({Path.GetFileName(file)} {pct}%)"),
                        0.25 + 0.70 * pct / 100.0);
                }
            }
            dst.Flush(flushToDisk: true);   // USB는 캐시에 남으면 뽑을 때 깨집니다.
        }
    }

    private static char PickFreeLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (char c = 'T'; c <= 'Z'; c++)
            if (!used.Contains(c)) return c;
        for (char c = 'G'; c <= 'S'; c++)
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException(L.T(
            "사용할 드라이브 문자가 없습니다.", "No free drive letter is available."));
    }

    private void RunDiskpart(string script, CancellationToken ct)
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, script, Encoding.ASCII);
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tmp}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException(L.T(
                    "diskpart를 시작하지 못했습니다.", "Failed to start diskpart."));
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            while (!p.WaitForExit(500))
            {
                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 이미 종료 */ }
                    ct.ThrowIfCancellationRequested();
                }
            }
            _logger.LogInformation("diskpart 부팅 USB 준비 (종료코드 {Code}):\n{Output}", p.ExitCode, output.Trim());
            if (p.ExitCode != 0)
                throw new InvalidOperationException(L.T(
                    $"diskpart가 실패했습니다(종료코드 {p.ExitCode}).",
                    $"diskpart failed (exit code {p.ExitCode})."));
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 정리 실패 무시 */ }
        }
    }

    private void Report(string step, double fraction)
    {
        _logger.LogInformation("부팅 USB: {Step}", step);
        Progress?.Invoke(step, fraction);
    }
}
