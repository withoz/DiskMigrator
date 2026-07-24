using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Jobs;
using DiskMigrator.Windows.Snapshots;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.VhdTest;

/// <summary>
/// 이미지 백업/복원 CLI(통합 테스트용). 백업은 디스크를 읽어 새 VHDX로 저장하고, 복원은
/// VHDX를 디스크로 되돌립니다. <b>복원 대상은 안전을 위해 가상 디스크만 허용</b>합니다
/// (이 도구로 실디스크를 실수로 파괴하지 못하게).
/// </summary>
internal static class ImageTool
{
    public static async Task<int> BackupAsync(
        int sourceDiskNumber, string imagePath, bool useSnapshot, bool skipUnused, bool verify, ILoggerFactory lf)
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

        // 스마트 클론(빈 영역 건너뛰기)은 NTFS 할당 비트맵을 스냅샷에서 읽으므로 스냅샷이 전제입니다.
        if (skipUnused) useSnapshot = true;

        Console.WriteLine($"백업: [{source.DeviceNumber}] {source.Model} " +
                          $"({SizeFormatter.Format(source.SizeBytes)}) → {imagePath}  " +
                          $"(스냅샷={useSnapshot}, 스마트={skipUnused})");

        var snapshotProvider = new VssSnapshotProvider(lf.CreateLogger<VssSnapshotProvider>());
        var options = new CloneOptions { BufferSize = 4 * 1024 * 1024, VerifyAfterClone = verify };
        var svc = new ImageBackupService(diskService, snapshotProvider, lf);
        var result = await svc.BackupAsync(source, imagePath, useSnapshot, skipUnused, options, MakeProgress());

        return Report(result, imagePath);
    }

    public static async Task<int> RestoreAsync(
        string imagePath, int targetDiskNumber, bool universalRestore, bool verify, ILoggerFactory lf)
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
                          $"({SizeFormatter.Format(target.SizeBytes)}) — 이 디스크의 데이터가 사라집니다. " +
                          $"(UR={universalRestore})");

        var options = new CloneOptions { BufferSize = 4 * 1024 * 1024, VerifyAfterClone = verify };
        var svc = new ImageRestoreService(diskService, lf);
        var report = await svc.RestoreAsync(imagePath, target, universalRestore, options, MakeProgress());

        int code = Report(report.Result, imagePath);
        if (report.GptRepair is { } g)
            Console.WriteLine($"GPT 보정: {(g.WasRepaired ? "적용됨" : "미적용")} — {g.Description}");
        if (report.UniversalRestore is { } u)
            Console.WriteLine($"Universal Restore: {u.Message}");
        return code;
    }

    /// <summary>
    /// 부모 이미지의 차등 자식을 만들어 그 안의 파티션을 축소합니다(축소 리사이즈 3단계 실기 검증).
    /// 부모 이미지가 그대로 보존되는지도 함께 확인합니다.
    /// </summary>
    public static async Task<int> ShrinkAsync(
        string parentImagePath, string childImagePath, int partitionNumber, double newSizeGb, ILoggerFactory lf)
    {
        var diskService = new WindowsDiskService(lf.CreateLogger<WindowsDiskService>());
        if (!diskService.IsElevated) { Console.Error.WriteLine("오류: 관리자 권한이 필요합니다."); return 3; }

        if (!File.Exists(parentImagePath))
        {
            Console.Error.WriteLine($"부모 이미지를 찾지 못했습니다: {parentImagePath}");
            return 2;
        }
        if (File.Exists(childImagePath))
        {
            Console.Error.WriteLine($"자식 이미지가 이미 있습니다(덮어쓰지 않음): {childImagePath}");
            return 2;
        }

        long parentSizeBefore = new FileInfo(parentImagePath).Length;
        long newBytes = (long)(newSizeGb * 1024 * 1024 * 1024);

        Console.WriteLine($"축소: 부모 {parentImagePath} → 차등 자식 {childImagePath}");
        Console.WriteLine($"      파티션 {partitionNumber}을(를) 약 {SizeFormatter.Format(newBytes)}로 줄입니다.");

        var shrinker = new VhdxShrinker(diskService, lf.CreateLogger<VhdxShrinker>());
        var result = await shrinker.ShrinkInDifferencingChildAsync(
            parentImagePath, childImagePath, partitionNumber, newBytes);

        Console.WriteLine();
        Console.WriteLine($"결과: {(result.Success ? "성공" : "실패")} — {result.Message}");
        Console.WriteLine($"  요청 {SizeFormatter.Format(result.RequestedBytes)}, " +
                          $"실제 {SizeFormatter.Format(result.AchievedPartitionBytes)}");

        // 부모 보존 확인 — 차등 자식의 핵심 안전장치.
        long parentSizeAfter = new FileInfo(parentImagePath).Length;
        bool parentIntact = parentSizeBefore == parentSizeAfter;
        Console.WriteLine($"  부모 이미지: {SizeFormatter.Format(parentSizeBefore)} → " +
                          $"{SizeFormatter.Format(parentSizeAfter)} " +
                          $"({(parentIntact ? "변화 없음 ✓" : "변경됨 ✗ — 버그!")})");
        if (File.Exists(childImagePath))
            Console.WriteLine($"  차등 자식: {SizeFormatter.Format(new FileInfo(childImagePath).Length)} (변경분만 저장)");

        return result.Success && parentIntact ? 0 : 1;
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
