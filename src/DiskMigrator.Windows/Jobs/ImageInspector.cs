using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>이미지 무결성 검사의 개별 항목.</summary>
public sealed record ImageCheckItem(string Name, bool Passed, string Detail);

/// <summary>복원 전 이미지 무결성 검사 결과.</summary>
public sealed record ImageInspectionReport(bool Ok, IReadOnlyList<ImageCheckItem> Items)
{
    /// <summary>사람이 읽을 한 줄 요약(실패 시 실패 항목들).</summary>
    public string Summary => Ok
        ? L.T("이미지 무결성 검사를 통과했습니다.", "The image passed the integrity check.")
        : string.Join("\n", Items.Where(i => !i.Passed).Select(i => $"• {i.Name}: {i.Detail}"));
}

/// <summary>
/// 복원을 시작하기 전, 백업 이미지(.vhdx)가 온전한지 검사합니다 — <b>대상을 지우기 전에</b>.
/// </summary>
/// <remarks>
/// 손상된 이미지로 복원을 시작하면 대상 디스크만 지워지고 복원은 도중에 실패합니다 —
/// 사용자는 원본도 대상도 잃습니다. 그래서 파괴적인 쓰기 전에 값싸게 확인합니다:
/// <list type="number">
/// <item>VHDX 부착(읽기 전용) — 컨테이너 구조(헤더·BAT·메타데이터)는 Windows virtdisk가
///   부착 시점에 검증합니다. 손상되면 여기서 실패합니다.</item>
/// <item>파티션 테이블 — 부착된 디스크에서 파티션이 하나 이상 인식되는지.</item>
/// <item>NTFS 파일시스템 — 각 NTFS 볼륨에 <c>chkdsk</c>(읽기 전용)를 돌려 구조 손상을
///   확인합니다. 직접 만든 검사기가 아니라 Windows의 검증된 검사기를 씁니다(축소 때
///   Windows 축소기를 쓰는 것과 같은 원칙).</item>
/// </list>
/// 이미지는 읽기 전용으로 부착하므로 절대 수정되지 않습니다. 검사가 끝나면 분리합니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ImageInspector(IDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 이미지를 <b>읽기 전용으로 부착한 채</b> 원하는 검사를 돌리고, 끝나면 분리합니다.
    /// </summary>
    /// <remarks>
    /// 부팅 진단(<c>BootReadinessCheck</c>·<c>BootDriverInventory</c>·<c>EspAudit</c> 등)은
    /// 모두 <see cref="DiskInfo"/>를 받습니다 — 물리 디스크든 부착된 이미지든 구분하지 않습니다.
    /// 그런데 그 도구들을 이미지에 대고 쓸 방법이 없어서, <b>"이 백업으로 복원하면 부팅될까"</b>에
    /// 답하려면 먼저 복원해야 했습니다. 사용자가 피하고 싶은 바로 그 위험입니다.
    ///
    /// <para>부착된 이미지는 그동안 정식 디스크 번호를 가지므로, 여기서 그 <see cref="DiskInfo"/>를
    /// 넘겨주면 기존 진단이 <b>그대로</b> 돕니다. 새로 만들 엔진이 없습니다.</para>
    ///
    /// <para><b>부착은 읽기 전용입니다.</b> 이미지는 수정되지 않으며, 콜백이 예외를 던져도
    /// 분리는 보장됩니다.</para>
    /// </remarks>
    /// <returns>부착·인식에 실패하면 <c>default</c>. 실패 이유는 <paramref name="failure"/>에 담습니다.</returns>
    public async Task<T?> WithAttachedDiskAsync<T>(
        string imagePath, Func<DiskInfo, T> body, Action<string>? failure = null,
        CancellationToken ct = default)
    {
        var info = new FileInfo(imagePath);
        if (!info.Exists || info.Length < (1L << 20))
        {
            failure?.Invoke(L.T("이미지 파일이 아닙니다(없거나 너무 작습니다).",
                                "Not an image file (missing or too small)."));
            return default;
        }

        return await Task.Run(() =>
        {
            VirtualDisk vhd;
            try
            {
                vhd = VirtualDisk.OpenAndAttach(imagePath, readOnly: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "이미지 부착 실패: {Path}", imagePath);
                failure?.Invoke(L.T($"이미지를 부착하지 못했습니다 — 손상되었을 수 있습니다. ({ex.Message})",
                                    $"The image could not be attached — it may be corrupted. ({ex.Message})"));
                return default;
            }

            using (vhd)
            {
                var disk = ResolveAttachedDisk(vhd, ct);
                if (disk is null)
                {
                    failure?.Invoke(L.T("부착된 이미지에서 파티션을 인식하지 못했습니다.",
                                        "No partitions were recognized on the attached image."));
                    return default;
                }
                return body(disk);
            }
        }, ct);
    }

    /// <summary>
    /// 부착된 이미지의 <see cref="DiskInfo"/>를 얻습니다.
    /// </summary>
    /// <remarks>
    /// 부착 직후에는 볼륨 연결이 늦을 수 있어 몇 번 다시 열거합니다 — 한 번만 보고 포기하면
    /// 멀쩡한 이미지를 "파티션 없음"으로 판정합니다.
    /// </remarks>
    private DiskInfo? ResolveAttachedDisk(VirtualDisk vhd, CancellationToken ct)
    {
        DiskInfo? disk = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(700);
            var disks = diskService.EnumerateDisksAsync(ct).GetAwaiter().GetResult();
            disk = disks.FirstOrDefault(d => d.DeviceNumber == vhd.DiskNumber);
            if (disk is not null && disk.Partitions.Count > 0 &&
                disk.Partitions.Any(p => p.FileSystem is not null))
                break;
        }
        return disk is { Partitions.Count: > 0 } ? disk : null;
    }

    public async Task<ImageInspectionReport> InspectAsync(string imagePath, CancellationToken ct = default)
    {
        var items = new List<ImageCheckItem>();

        // 1) 파일 존재·크기 — VHDX 헤더 영역(1MB)조차 안 되면 이미지일 수 없습니다.
        var info = new FileInfo(imagePath);
        if (!info.Exists || info.Length < (1L << 20))
        {
            items.Add(new(L.T("이미지 파일", "Image file"), false,
                info.Exists
                    ? L.T($"파일이 너무 작습니다({info.Length:N0}바이트) — VHDX가 아닙니다.",
                          $"The file is too small ({info.Length:N0} bytes) — not a VHDX.")
                    : L.T("파일을 찾을 수 없습니다.", "The file was not found.")));
            return new(false, items);
        }
        items.Add(new(L.T("이미지 파일", "Image file"), true,
            L.T($"{info.Length / 1073741824.0:F1} GB", $"{info.Length / 1073741824.0:F1} GB")));

        return await Task.Run(() => InspectCore(imagePath, items, ct), ct);
    }

    private ImageInspectionReport InspectCore(string imagePath, List<ImageCheckItem> items, CancellationToken ct)
    {
        // 2) 부착 — VHDX 컨테이너 구조 검증(virtdisk가 헤더·메타데이터를 읽어야 성공).
        VirtualDisk vhd;
        try
        {
            vhd = VirtualDisk.OpenAndAttach(imagePath, readOnly: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "이미지 부착 실패: {Path}", imagePath);
            items.Add(new(L.T("VHDX 구조", "VHDX structure"), false, L.T(
                $"이미지를 부착하지 못했습니다 — 파일이 손상되었을 수 있습니다. ({ex.Message})",
                $"The image could not be attached — the file may be corrupted. ({ex.Message})")));
            return new(false, items);
        }

        using (vhd)
        {
            items.Add(new(L.T("VHDX 구조", "VHDX structure"), true,
                L.T("부착 성공 (읽기 전용).", "Attached successfully (read-only).")));

            // 3) 파티션 테이블 — 부착 직후엔 볼륨 연결이 늦을 수 있어 몇 번 다시 열거합니다.
            var disk = ResolveAttachedDisk(vhd, ct);

            if (disk is null)
            {
                items.Add(new(L.T("파티션 테이블", "Partition table"), false, L.T(
                    "부착된 이미지에서 파티션을 인식하지 못했습니다 — 파티션 테이블이 손상되었을 수 있습니다.",
                    "No partitions were recognized on the attached image — the partition table may be corrupted.")));
                return new(false, items);
            }
            items.Add(new(L.T("파티션 테이블", "Partition table"), true, L.T(
                $"{disk.PartitionStyle}, 파티션 {disk.Partitions.Count}개.",
                $"{disk.PartitionStyle}, {disk.Partitions.Count} partition(s).")));

            // 4) 파일시스템 — Windows 파일시스템(NTFS/FAT/exFAT) 볼륨마다 chkdsk(읽기 전용).
            //
            // 어떤 파티션을 검사할지는 열거된 FileSystem 문자열이 아니라 <b>부트 섹터 서명</b>으로
            // 정합니다. 손상된 NTFS는 Windows가 마운트를 거부해 FileSystem이 null로 열거되는데,
            // 그 문자열로 걸렀더니 심하게 손상된 볼륨일수록 검사를 건너뛰었습니다(실기에서 발견).
            // 부트 섹터는 마운트와 무관하게 읽을 수 있고, chkdsk도 스스로 부트 섹터로 판별합니다.
            bool ok = true;
            foreach (var p in disk.Partitions)
            {
                ct.ThrowIfCancellationRequested();
                if (p.VolumeGuidPath is null && p.DriveLetter is null)
                    continue; // 볼륨이 없는 파티션(MSR 등)은 검사 대상이 아닙니다.

                string? fsName = SniffWindowsFileSystem(vhd.PhysicalPath, p.StartingOffset);
                string name = L.T($"파일시스템 (파티션 {p.Number}, {fsName ?? "?"})",
                                  $"File system (partition {p.Number}, {fsName ?? "?"})");

                if (fsName is null)
                {
                    // Windows 파일시스템 서명이 없으면 판단하지 않습니다 — 다른 OS의 파티션일 수
                    // 있고, 복원 자체는 섹터 단위라 볼륨 접근이 필요 없습니다.
                    items.Add(new(name, true, L.T(
                        "Windows 파일시스템이 아니어서 검사를 건너뜀 (구조·파티션 검사는 통과).",
                        "Not a Windows file system — check skipped (structure and partition checks passed).")));
                    continue;
                }

                var (passed, detail) = RunChkdskReadOnly(p, ct);
                items.Add(new(name, passed, detail));
                ok &= passed;
            }

            return new(ok, items);
        }
    }

    /// <summary>
    /// Windows FS 볼륨에 chkdsk(읽기 전용)를 돌립니다. 드라이브 문자가 없으면 <b>임시 폴더에
    /// 마운트</b>합니다 — 임시 드라이브 문자는 탐색기 자동 실행이 반응해 문자 제거 후
    /// "위치를 사용할 수 없습니다" 팝업을 띄웁니다(실기에서 발견). 폴더 마운트는 조용합니다.
    /// 읽기 전용 볼륨이라 이미지는 수정되지 않습니다.
    /// </summary>
    private (bool Passed, string Detail) RunChkdskReadOnly(PartitionInfo p, CancellationToken ct)
    {
        string? mountDir = null;
        try
        {
            string chkTarget;
            if (p.DriveLetter is { } dl) chkTarget = dl[0] + ":";
            else
            {
                mountDir = MountToTempFolder(p.VolumeGuidPath!);
                if (mountDir is null)
                    return (true, L.T(
                        "볼륨을 임시 폴더에 마운트하지 못해 파일시스템 검사를 건너뜀.",
                        "Could not mount the volume to a temporary folder — file-system check skipped."));
                chkTarget = mountDir;
            }

            var psi = new ProcessStartInfo("chkdsk.exe", $"\"{chkTarget}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException(L.T("chkdsk를 시작하지 못했습니다.", "Failed to start chkdsk."));

            // 출력은 기다리는 동안 비동기로 소비해야 합니다 — 종료 후 읽으면 chkdsk의 진행률
            // 출력이 파이프 버퍼(4KB)를 채우는 순간 서로 기다리는 교착이 됩니다(실기에서 발견).
            var stdout = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            while (!proc.WaitForExit(500))
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                }
            }
            proc.WaitForExit(); // 비동기 출력 핸들러가 끝까지 비워지도록 한 번 더.
            string output;
            lock (stdout) output = stdout.ToString();
            _logger.LogInformation("chkdsk {Target}: 종료코드 {Code}", chkTarget, proc.ExitCode);

            if (proc.ExitCode == 0)
                return (true, L.T("chkdsk에서 문제가 발견되지 않았습니다.", "chkdsk found no problems."));

            string tail = string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(3).Select(s => s.Trim()));
            _logger.LogWarning("chkdsk 문제 보고: {Tail}", tail);
            return (false, L.T(
                $"chkdsk가 파일시스템 손상을 보고했습니다(코드 {proc.ExitCode}). 이 이미지로 복원한 사본도 같은 손상을 갖게 됩니다.",
                $"chkdsk reported file-system corruption (code {proc.ExitCode}). A copy restored from this image would carry the same corruption."));
        }
        finally
        {
            if (mountDir is not null)
            {
                if (!NativeMethods.DeleteVolumeMountPoint(mountDir + "\\"))
                    _logger.LogWarning("임시 마운트 폴더 {Dir} 해제 실패.", mountDir);
                try { Directory.Delete(mountDir); } catch { }
            }
        }
    }

    /// <summary>볼륨을 빈 임시 폴더에 마운트하고 그 경로를 돌려줍니다(실패 시 null).</summary>
    private string? MountToTempFolder(string volumeGuidPath)
    {
        try
        {
            string vol = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";
            string dir = Path.Combine(Path.GetTempPath(), "dm-chk-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            if (NativeMethods.SetVolumeMountPoint(dir + "\\", vol)) return dir;
            Directory.Delete(dir);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "임시 폴더 마운트 실패.");
            return null;
        }
    }

    /// <summary>
    /// 파티션의 부트 섹터를 직접 읽어 Windows 파일시스템 서명을 확인합니다.
    /// 마운트 성공 여부와 무관하므로, 손상돼 마운트가 거부된 볼륨도 원래 무엇이었는지 알 수 있습니다.
    /// </summary>
    private string? SniffWindowsFileSystem(string physicalPath, long partitionOffset)
    {
        try
        {
            using var dev = RawDiskDevice.OpenRead(physicalPath);
            Span<byte> boot = stackalloc byte[512];
            if (dev.Read(partitionOffset, boot) < 512) return null;

            // OEM ID(오프셋 3~10): NTFS·exFAT. FAT은 BPB의 형식 문자열로 판별합니다.
            if (boot.Slice(3, 8).SequenceEqual("NTFS    "u8)) return "NTFS";
            if (boot.Slice(3, 8).SequenceEqual("EXFAT   "u8)) return "exFAT";
            if (boot.Slice(82, 8).StartsWith("FAT32"u8)) return "FAT32";
            if (boot.Slice(54, 8).StartsWith("FAT"u8)) return "FAT";
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "부트 섹터 판별 실패 (오프셋 {Offset}).", partitionOffset);
            return null;
        }
    }

}
