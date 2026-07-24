using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Jobs;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.VhdTest;

/// <summary>
/// 이미지 백업/복원 CLI(통합 테스트용). 백업은 디스크를 읽어 새 VHDX로 저장하고, 복원은
/// VHDX를 디스크로 되돌립니다. <b>복원 대상은 안전을 위해 가상 디스크만 허용</b>합니다
/// (이 도구로 실디스크를 실수로 파괴하지 못하게).
/// </summary>
internal static class ImageTool
{
    public static async Task<int> BackupAsync(int sourceDiskNumber, string imagePath, bool verify, ILoggerFactory lf)
    {
        var diskService = new WindowsDiskService(lf.CreateLogger<WindowsDiskService>());
        if (!diskService.IsElevated) { Console.Error.WriteLine("오류: 관리자 권한이 필요합니다."); return 3; }

        if (File.Exists(imagePath))
        {
            Console.Error.WriteLine($"이미지 파일이 이미 있습니다(덮어쓰지 않음): {imagePath}");
            return 2;
        }

        var disks = await diskService.EnumerateDisksAsync();
        var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceDiskNumber);
        if (source is null) { Console.Error.WriteLine($"디스크 {sourceDiskNumber}을(를) 찾지 못했습니다."); return 2; }

        Console.WriteLine($"백업: [{source.DeviceNumber}] {source.Model} " +
                          $"({SizeFormatter.Format(source.SizeBytes)}) → {imagePath}");

        var options = new CloneOptions { BufferSize = 4 * 1024 * 1024, VerifyAfterClone = verify };
        var svc = new ImageBackupService(lf);
        var result = await svc.BackupAsync(source.DevicePath, imagePath, options, MakeProgress());

        return Report(result, imagePath);
    }

    public static async Task<int> RestoreAsync(string imagePath, int targetDiskNumber, bool verify, ILoggerFactory lf)
    {
        var diskService = new WindowsDiskService(lf.CreateLogger<WindowsDiskService>());
        if (!diskService.IsElevated) { Console.Error.WriteLine("오류: 관리자 권한이 필요합니다."); return 3; }

        if (!File.Exists(imagePath)) { Console.Error.WriteLine($"이미지 파일을 찾지 못했습니다: {imagePath}"); return 2; }

        var disks = await diskService.EnumerateDisksAsync();
        var target = disks.FirstOrDefault(d => d.DeviceNumber == targetDiskNumber);
        if (target is null) { Console.Error.WriteLine($"대상 디스크 {targetDiskNumber}을(를) 찾지 못했습니다."); return 2; }

        // 안전: 이 테스트 도구는 가상 디스크에만 복원합니다(실디스크를 실수로 파괴하지 못하게).
        if (target.BusType is not (DiskBusType.FileBackedVirtual or DiskBusType.Virtual))
        {
            Console.Error.WriteLine(
                $"거부: 대상 디스크 {targetDiskNumber}의 버스가 {target.BusType}입니다. " +
                "이 도구는 가상 디스크(VHD/VHDX)에만 복원합니다.");
            return 4;
        }

        Console.WriteLine($"복원: {imagePath} → [{target.DeviceNumber}] {target.Model} " +
                          $"({SizeFormatter.Format(target.SizeBytes)}) — 이 디스크의 데이터가 사라집니다.");

        var options = new CloneOptions { BufferSize = 4 * 1024 * 1024, VerifyAfterClone = verify };
        var svc = new ImageRestoreService(diskService, lf);
        var result = await svc.RestoreAsync(imagePath, target, options, MakeProgress());

        return Report(result, imagePath);
    }

    private static IProgress<CloneProgress> MakeProgress()
    {
        int lastPct = -1;
        return new Progress<CloneProgress>(p =>
        {
            int pct = (int)p.Percent;
            if (pct == lastPct) return;
            lastPct = pct;
            Console.Write($"\r  {p.Phase} {p.Percent,5:F1}%   ");
        });
    }

    private static int Report(CloneResult result, string imagePath)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"결과: {result.Outcome}, {SizeFormatter.Format(result.BytesCopied)} 복사, 소요 {result.Duration}");

        if (result.VerificationPassed == false)
        {
            Console.Error.WriteLine("검증 실패 — 대상을 신뢰하지 마십시오.");
            return 1;
        }

        if (result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors)
        {
            if (File.Exists(imagePath))
                Console.WriteLine($"이미지 파일 실제 크기: {SizeFormatter.Format(new FileInfo(imagePath).Length)} " +
                                  "(동적 VHDX — 쓴 블록만 할당)");
            return 0;
        }

        return 1;
    }
}
