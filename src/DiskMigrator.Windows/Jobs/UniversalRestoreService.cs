using System.Diagnostics;
using System.Runtime.Versioning;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

public sealed record UniversalRestoreReport(
    bool Applied,
    string? WindowsVolume,
    IReadOnlyList<string> EnabledDrivers,
    string Message);

/// <summary>
/// 클론이 끝난 대상 디스크가 다른 하드웨어에서도 부팅되도록 SYSTEM 하이브를 손봅니다.
/// </summary>
/// <remarks>
/// 클론 직후, 대상 디스크가 다시 온라인이 되어 볼륨이 마운트된 뒤에 호출합니다.
/// 대상에서 Windows가 설치된 파티션을 찾아 그 SYSTEM 하이브에
/// <see cref="UniversalRestore"/>를 적용합니다 (표준 저장소 드라이버를 부팅 시작으로).
///
/// 하이브는 reg.exe/RegLoadKey가 아니라 우리 파서로 직접 편집하므로, OS 버전·환경에
/// 관계없이 동작합니다. 실기에서 reg.exe가 못 열던 하이브도 정상 수정됨을 확인했습니다.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UniversalRestoreService(
    IDiskService diskService,
    ILogger<UniversalRestoreService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<UniversalRestoreService>.Instance;

    /// <summary>
    /// 대상 디스크의 Windows 파티션을 찾아 하드웨어 독립화를 적용합니다.
    /// </summary>
    /// <param name="target">클론이 끝나 온라인 상태인 대상 디스크.</param>
    /// <param name="mountSettleDelay">볼륨이 마운트되고 드라이브 문자가 붙기를 기다리는 시간.</param>
    public async Task<UniversalRestoreReport> ApplyAsync(
        DiskInfo target,
        TimeSpan? mountSettleDelay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 온라인 직후엔 볼륨 마운트·드라이브 문자 부여에 잠깐 시간이 걸립니다.
        await Task.Delay(mountSettleDelay ?? TimeSpan.FromSeconds(3), ct);

        // 대상 디스크를 다시 열거해 현재 드라이브 문자를 얻습니다.
        var disks = await diskService.EnumerateDisksAsync(ct);
        var fresh = disks.FirstOrDefault(d =>
            d.SizeBytes == target.SizeBytes &&
            string.Equals(d.Model, target.Model, StringComparison.OrdinalIgnoreCase) &&
            d.DeviceNumber == target.DeviceNumber);

        fresh ??= disks.FirstOrDefault(d => d.DeviceNumber == target.DeviceNumber);

        if (fresh is null)
        {
            return new UniversalRestoreReport(false, null, [],
                "클론 후 대상 디스크를 다시 찾지 못해 하드웨어 독립화를 건너뜁니다.");
        }

        // 대상의 NTFS 파티션들 중 Windows가 설치된 것을 찾습니다.
        foreach (var partition in fresh.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            if (partition.DriveLetter is not { } letter) continue;

            string hivePath = $@"{letter}:\Windows\System32\config\SYSTEM";
            if (!File.Exists(hivePath)) continue;

            _logger.LogInformation("Windows 파티션 발견: {Letter}: — SYSTEM 하이브 편집 시작.", letter);

            try
            {
                var result = UniversalRestore.Apply(hivePath, _logger);

                // 최대 절전 이미지를 지웁니다 — 다른 하드웨어에서 세션 복원(로고 동그라미 멈춤) 예방.
                // 하이브의 빠른시작/최대절전 값은 UniversalRestore.Apply가 이미 껐습니다(재생성 방지).
                bool hiberfilDeleted = TryDeleteHiberfil(letter);

                string driverMsg = result.Enabled.Count > 0
                    ? $"{letter}: 의 저장소 드라이버 {result.Enabled.Count}개를 부팅 시작으로 설정했습니다. " +
                      "이제 다른 하드웨어(대개 AHCI/NVMe)에서도 부팅이 가능합니다. " +
                      "대상 PC가 Intel VMD/RAID 모드라면 BIOS에서 AHCI로 바꾸십시오 " +
                      "(해당 드라이버가 원본에 없으면 레지스트리만으로는 안 됩니다)."
                    : $"{letter}: 의 저장소 드라이버가 이미 부팅 시작으로 설정돼 있습니다.";

                string hiberMsg = (result.HibernationDisabled || hiberfilDeleted)
                    ? " 또한 최대 절전/빠른 시작을 끄고 최대 절전 이미지를 제거해, 다른 PC에서 세션 복원으로 멈추지 않습니다."
                    : "";

                return new UniversalRestoreReport(true, $"{letter}:", result.Enabled, driverMsg + hiberMsg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Letter}: 의 하이브 편집에 실패했습니다.", letter);
                return new UniversalRestoreReport(false, $"{letter}:", [],
                    $"하이브 편집 실패: {ex.Message}. 클론 데이터는 정상입니다.");
            }
        }

        return new UniversalRestoreReport(false, null, [],
            "대상에서 Windows 설치 파티션을 찾지 못했습니다 (Windows 시스템 디스크가 아닐 수 있음).");
    }

    /// <summary>
    /// 대상 볼륨의 <c>hiberfil.sys</c>를 지웁니다. 이게 남아 있으면 다른 하드웨어에서 부팅 시
    /// winresume가 원본 하드웨어의 세션을 복원하려다 로고 동그라미에서 멈춥니다(실기에서 규명).
    /// </summary>
    /// <remarks>
    /// hiberfil.sys는 SYSTEM 소유라 관리자여도 소유권을 먼저 가져와야 지워집니다. 실패해도
    /// 클론 데이터는 정상이며, 부팅 후 <c>powercfg /h off</c>로 정리할 수 있으므로 삼킵니다.
    /// </remarks>
    private bool TryDeleteHiberfil(string letter)
    {
        string path = $@"{letter}:\hiberfil.sys";
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogInformation("{Letter}: 최대 절전 이미지가 없습니다(정리 불필요).", letter);
                return false;
            }

            RunSilent("takeown.exe", "/f", path);
            RunSilent("icacls.exe", path, "/grant", "*S-1-5-32-544:F"); // Administrators
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* 속성 해제 실패는 무해 */ }

            File.Delete(path);
            _logger.LogInformation("{Letter}: 최대 절전 이미지(hiberfil.sys)를 삭제했습니다 — cold boot 강제.", letter);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Letter}: hiberfil.sys 삭제 실패(무해 — 부팅 후 powercfg /h off 로 정리 가능).", letter);
            return false;
        }
    }

    /// <summary>외부 도구를 조용히 실행합니다(출력·오류 무시, best-effort).</summary>
    private static void RunSilent(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch { /* best-effort */ }
    }
}
