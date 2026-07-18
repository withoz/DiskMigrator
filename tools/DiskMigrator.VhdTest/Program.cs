using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.VhdTest;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Jobs;
using DiskMigrator.Windows.Snapshots;
using Microsoft.Extensions.Logging;

// 하이브 편집(Universal Restore) 진단 — 인자를 미리 처리 (디스크 열거 불필요).
if (args.Length >= 2 && args[0] == "--hive-read")
    return DiskMigrator.VhdTest.HiveTool.Read(args[1]);
if (args.Length >= 2 && args[0] == "--hive-fix")
{
    using var lf0 = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new ConsoleLogProvider()));
    return DiskMigrator.VhdTest.HiveTool.Fix(args[1], lf0.CreateLogger("HiveFix"));
}

// 가상 디스크(VHD)를 대상으로 클론 전체 경로를 실제로 실행해 보는 통합 테스트 도구입니다.
// 쓰기 경로 · 볼륨 잠금 · VSS 스냅샷 · GPT 보정은 단위 테스트로는 확인할 수 없습니다.

Console.OutputEncoding = System.Text.Encoding.UTF8;

bool useSnapshot = args.Contains("--snapshot");
bool verify = !args.Contains("--no-verify");
bool planOnly = args.Contains("--plan-only");
bool snapStability = args.Contains("--snapshot-stability");
bool bootCheckOnly = args.Contains("--boot-check");
bool runBootCheck = !args.Contains("--no-boot-check");

if (args.Length < 1 || !int.TryParse(args[0], out int sourceNumber))
{
    Console.Error.WriteLine(
        "사용법:\n" +
        "  DiskMigrator.VhdTest <원본디스크번호> <대상디스크번호> [--snapshot] [--no-verify] [--no-boot-check]\n" +
        "      가상 디스크(VHD) 대상으로 실제 클론을 실행하고, 성공 시 부팅 구성을 정적 검사합니다.\n" +
        "  DiskMigrator.VhdTest <디스크번호> --boot-check\n" +
        "      실제 부팅 없이 부트로더·BCD·winload·저장소 드라이버 무결성만 점검합니다 (읽기 전용).\n" +
        "  DiskMigrator.VhdTest <원본디스크번호> --plan-only [--snapshot]\n" +
        "      대상 없이 복사 계획만 만들어 검증합니다. 어떤 디스크에도 쓰지 않습니다.\n" +
        "  DiskMigrator.VhdTest <디스크번호> --snapshot-stability [--wait <초>]\n" +
        "      VSS 스냅샷이 시간에 따라 바뀌는지 측정합니다. 어떤 디스크에도 쓰지 않습니다.");
    return 2;
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddProvider(new ConsoleLogProvider()));

var diskService = new WindowsDiskService(loggerFactory.CreateLogger<WindowsDiskService>());
var snapshotProvider = new VssSnapshotProvider(loggerFactory.CreateLogger<VssSnapshotProvider>());

if (!diskService.IsElevated)
{
    Console.Error.WriteLine("오류: 관리자 권한이 필요합니다.");
    return 3;
}

// 부팅 구성 정적 검사 — 읽기 전용이므로 어떤 디스크에도 허용합니다(가상/실물 무관).
// 클론한 실물 대상(USB/SSD)을 물리적으로 옮기기 전에 값싸게 부팅 가능성을 점검하는 용도.
if (bootCheckOnly)
{
    var allDisks = await diskService.EnumerateDisksAsync();
    var disk = allDisks.FirstOrDefault(d => d.DeviceNumber == sourceNumber);
    if (disk is null)
    {
        Console.Error.WriteLine($"오류: 디스크 {sourceNumber}를 찾을 수 없습니다.");
        return 4;
    }
    return BootCheck.Run(disk);
}

// 스냅샷 안정성 측정 — 대상 없이 원본 디스크의 스냅샷만 두 번 읽어 비교. 쓰기 없음.
if (snapStability)
{
    // --immediate: 즉시 재읽기 일관성 테스트 (읽기 방식 vs 시간 드리프트 구분)
    if (args.Contains("--immediate"))
    {
        int limitGb = 64;
        int li = Array.IndexOf(args, "--limit-gb");
        if (li >= 0 && li + 1 < args.Length) int.TryParse(args[li + 1], out limitGb);
        return await SnapshotStabilityProbe.RunImmediateAsync(
            diskService, snapshotProvider, sourceNumber, limitGb);
    }

    int waitSec = 60;
    int wi = Array.IndexOf(args, "--wait");
    if (wi >= 0 && wi + 1 < args.Length) int.TryParse(args[wi + 1], out waitSec);

    // --diff-volume <경로>: 섀도 저장소를 다른 볼륨에 두고 테스트 (드리프트 원인 검증)
    int dvi = Array.IndexOf(args, "--diff-volume");
    if (dvi >= 0 && dvi + 1 < args.Length) snapshotProvider.DiffAreaVolumeOverride = args[dvi + 1];

    return await SnapshotStabilityProbe.RunAsync(
        diskService, snapshotProvider,
        loggerFactory.CreateLogger("SnapshotStability"), sourceNumber, waitSec);
}

// 계획 미리보기는 대상을 열지 않고 어디에도 쓰지 않으므로, 아래의 "가상 디스크에만 쓴다"
// 안전장치보다 앞에 둡니다. 실제 시스템 디스크를 원본으로 검사하는 것이 이 모드의 목적입니다.
if (planOnly)
{
    return await PlanPreview.RunAsync(
        diskService, snapshotProvider, loggerFactory, sourceNumber, useSnapshot);
}

if (args.Length < 2 || !int.TryParse(args[1], out int targetNumber))
{
    Console.Error.WriteLine("오류: 대상 디스크 번호가 필요합니다 (--plan-only가 아니라면).");
    return 2;
}

var disks = await diskService.EnumerateDisksAsync();

var source = disks.FirstOrDefault(d => d.DeviceNumber == sourceNumber);
var target = disks.FirstOrDefault(d => d.DeviceNumber == targetNumber);

if (source is null || target is null)
{
    Console.Error.WriteLine($"오류: 디스크 {sourceNumber} 또는 {targetNumber}를 찾을 수 없습니다.");
    return 4;
}

// ============================================================================
//  안전장치 — 이 도구는 가상 디스크에만 씁니다.
//
//  통합 테스트 도구는 정의상 "쓰기 경로에 버그가 있는지 모르는 상태"에서 돌립니다.
//  대상이 실제 물리 디스크일 가능성을 코드 수준에서 완전히 차단합니다.
//  SafetyGuard와 별개로 한 겹 더 두는 이유는, 지금 검증하려는 대상이
//  바로 그 SafetyGuard가 포함된 경로이기 때문입니다.
// ============================================================================

foreach (var (label, disk) in new[] { ("원본", source), ("대상", target) })
{
    if (disk.BusType is not (DiskBusType.FileBackedVirtual or DiskBusType.Virtual))
    {
        Console.Error.WriteLine(
            $"거부: {label} 디스크 [{disk.DeviceNumber}] {disk.Model} 의 버스 종류가 " +
            $"{disk.BusType} 입니다. 이 도구는 가상 디스크(VHD)에만 사용할 수 있습니다.");
        return 5;
    }

    if (disk.IsSystemDisk || disk.IsBootDisk || disk.HasPageFile)
    {
        Console.Error.WriteLine($"거부: {label} 디스크가 시스템/부팅/페이지파일 디스크입니다.");
        return 5;
    }
}

Console.WriteLine("=== VHD 클론 통합 테스트 ===\n");
Console.WriteLine($"원본: [{source.DeviceNumber}] {source.Model} — {SizeFormatter.Format(source.SizeBytes)} ({source.BusType})");
Console.WriteLine($"대상: [{target.DeviceNumber}] {target.Model} — {SizeFormatter.Format(target.SizeBytes)} ({target.BusType})");
Console.WriteLine($"스냅샷: {(useSnapshot ? "사용" : "미사용")}   검증: {(verify ? "함" : "안 함")}");
Console.WriteLine($"VSS 사용 가능: {snapshotProvider.IsAvailable}\n");

foreach (var p in source.Partitions)
{
    Console.WriteLine($"  원본 파티션 {p.Number}: {p.DriveLetter ?? "-"} {p.FileSystem ?? "RAW"} " +
                      $"{SizeFormatter.Format(p.LengthBytes)} @ {p.StartingOffset:N0}" +
                      $"{(p.IsEfiSystemPartition ? " [EFI]" : "")}");
}
Console.WriteLine();

var options = new CloneOptions
{
    BufferSize = 4 * 1024 * 1024,
    VerifyAfterClone = verify,
    BadSectorPolicy = BadSectorPolicy.Abort,
    ProgressInterval = TimeSpan.FromMilliseconds(500),
};

string lastPhase = "";
var progress = new Progress<CloneProgress>(p =>
{
    if (p.Phase != lastPhase)
    {
        lastPhase = p.Phase;
        Console.WriteLine($"\n--- {p.Phase} 단계 ---");
    }

    Console.WriteLine($"  {p.Percent,5:F1}%  {SizeFormatter.Format(p.BytesProcessed),10} / " +
                      $"{SizeFormatter.Format(p.TotalBytes),-10} {SizeFormatter.FormatSpeed(p.SpeedBytesPerSecond),12}  {p.CurrentRegion}");
});

try
{
    var orchestrator = new CloneOrchestrator(diskService, snapshotProvider, loggerFactory);
    var report = await orchestrator.RunAsync(
        source, target, useSnapshot, options, universalRestore: false, progress);

    Console.WriteLine("\n=== 결과 ===\n");
    Console.WriteLine($"  상태        : {report.Result.Outcome}");
    Console.WriteLine($"  복사        : {SizeFormatter.Format(report.Result.BytesCopied)}");
    Console.WriteLine($"  소요 시간   : {SizeFormatter.FormatDuration(report.Result.Duration)}");
    Console.WriteLine($"  평균 속도   : {SizeFormatter.FormatSpeed(report.Result.AverageSpeedBytesPerSecond)}");
    Console.WriteLine($"  검증        : {report.Result.VerificationPassed switch { true => "통과", false => "실패", null => "수행 안 함" }}");
    Console.WriteLine($"  불량 섹터   : {report.Result.BadSectors.Count}건");

    if (report.SnapshotTimeUtc is { } t)
    {
        Console.WriteLine($"  스냅샷 시점 : {t.ToLocalTime():HH:mm:ss}");
    }

    if (report.UnsnapshottedPartitions.Count > 0)
    {
        Console.WriteLine($"  원시 복사   : {string.Join(", ", report.UnsnapshottedPartitions)}");
    }

    Console.WriteLine($"  GPT 보정    : {report.GptRepair?.Description ?? "해당 없음"}");

    if (report.Result.ErrorMessage is { } err)
    {
        Console.WriteLine($"  오류        : {err}");
    }

    bool success = report.Result.Outcome is CloneOutcome.Completed or CloneOutcome.CompletedWithBadSectors
                   && report.Result.VerificationPassed != false;

    Console.WriteLine($"\n{(success ? "*** 클론 성공 ***" : "*** 클론 실패 ***")}");

    // 클론 성공 시, 대상 디스크의 부팅 구성을 정적으로 점검합니다.
    // 클론으로 파티션 테이블이 바뀌었으므로 대상 디스크를 새로 열거해 최신 볼륨 정보를 얻습니다.
    if (success && runBootCheck)
    {
        var refreshed = await diskService.EnumerateDisksAsync();
        var freshTarget = refreshed.FirstOrDefault(d => d.DeviceNumber == targetNumber);
        if (freshTarget is not null)
        {
            BootCheck.Run(freshTarget);
        }
        else
        {
            Console.WriteLine("\n(부팅 검사 생략: 대상 디스크를 다시 찾지 못했습니다.)");
        }
    }

    return success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\n*** 예외 발생 ***\n{ex}");
    return 1;
}
