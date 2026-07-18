using DiskMigrator.Cli;
using DiskMigrator.Core.Engine;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Safety;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using DiskMigrator.Windows.Jobs;
using DiskMigrator.Windows.Snapshots;
using Microsoft.Extensions.Logging;

// DiskMigrator의 명령줄 프런트엔드. WPF 앱과 똑같은 엔진·안전장치를 쓰며,
// 확인 절차도 동일합니다: 대상 디스크의 모델명을 정확히 입력해야만 진행합니다.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = CliOptions.Parse(args);
if (options is null) return 2;

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddProvider(new ConsoleLogProvider()));

var diskService = new WindowsDiskService(loggerFactory.CreateLogger<WindowsDiskService>());
var snapshotProvider = new VssSnapshotProvider(loggerFactory.CreateLogger<VssSnapshotProvider>());

if (!diskService.IsElevated)
{
    Console.Error.WriteLine("오류: 관리자 권한이 필요합니다.");
    return 3;
}

var disks = await diskService.EnumerateDisksAsync();

var source = disks.FirstOrDefault(d => d.DeviceNumber == options.SourceNumber);
var target = disks.FirstOrDefault(d => d.DeviceNumber == options.TargetNumber);

if (source is null || target is null)
{
    Console.Error.WriteLine($"오류: 디스크 {options.SourceNumber} 또는 {options.TargetNumber}를 찾을 수 없습니다.");
    return 4;
}

Console.WriteLine("=== DiskMigrator CLI ===\n");
Report.PrintDisk("원본", source);
Report.PrintDisk("대상", target);

Console.WriteLine($"스냅샷: {(options.UseSnapshot ? "사용" : "미사용")}   " +
                  $"검증: {(options.Verify ? "함" : "안 함")}   " +
                  $"불량 섹터: {(options.ZeroFillBadSectors ? "0으로 채우고 계속" : "발견 시 중단")}");
Console.WriteLine($"VSS 사용 가능: {snapshotProvider.IsAvailable}\n");

// --- 안전 점검 --------------------------------------------------------------

var safety = SafetyGuard.Evaluate(source, target, diskService.IsElevated, options.UseSnapshot);

Console.WriteLine("=== 안전 점검 ===\n");
foreach (var issue in safety.Issues.OrderByDescending(i => i.Severity))
{
    Console.WriteLine($"  [{issue.Severity}] {issue.Code}");
    Console.WriteLine($"      {issue.Message}");
}
if (safety.Issues.Count == 0) Console.WriteLine("  (지적 사항 없음)");
Console.WriteLine();

if (!safety.CanProceed)
{
    Console.Error.WriteLine("차단: 위 사유로 진행할 수 없습니다.");
    return 5;
}

// --- 확인 절차 --------------------------------------------------------------
//
// 대상에 파티션이 없어 SafetyGuard가 확인을 요구하지 않더라도, CLI는 항상
// 모델명 입력을 요구합니다. UI와 달리 스크립트로 자동 실행되기 때문에
// "잘못된 번호를 넘겼다"는 실수가 아무 저항 없이 디스크를 지울 수 있습니다.

if (!SafetyGuard.IsConfirmationValid(target, options.ConfirmModel))
{
    Console.Error.WriteLine(
        $"확인 실패: 대상 디스크의 모든 데이터가 삭제됩니다. 진행하려면 대상 모델명을 정확히 넘기십시오:\n" +
        $"    --confirm \"{target.Model}\"");
    return 6;
}

Console.WriteLine($"확인됨: 대상 [{target.DeviceNumber}] {target.Model} 을(를) 덮어씁니다.\n");

// --- 실행 -------------------------------------------------------------------

var cloneOptions = new CloneOptions
{
    BufferSize = options.BufferSizeMb * 1024 * 1024,
    VerifyAfterClone = options.Verify,
    BadSectorPolicy = options.ZeroFillBadSectors
        ? BadSectorPolicy.ZeroFillAndContinue
        : BadSectorPolicy.Abort,
    ProgressInterval = TimeSpan.FromSeconds(options.ProgressSeconds),
};

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n취소 요청됨 — 안전한 지점에서 중단합니다...");
    cts.Cancel();
};

var progress = new Progress<CloneProgress>(Report.PrintProgress);

try
{
    var orchestrator = new CloneOrchestrator(diskService, snapshotProvider, loggerFactory);

    var report = await orchestrator.RunAsync(
        source, target, options.UseSnapshot, cloneOptions, options.UniversalRestore,
        progress, pause: null, cts.Token);

    Console.WriteLine();
    Report.PrintResult(report);

    return report.Result.Outcome switch
    {
        CloneOutcome.Completed => 0,
        CloneOutcome.CompletedWithBadSectors => 0,
        CloneOutcome.Cancelled => 7,
        _ => 1,
    };
}
catch (SafetyViolationException ex)
{
    Console.Error.WriteLine($"\n*** 안전 검사 위반으로 중단 ***\n{ex.Message}\n대상 디스크에는 아무것도 쓰지 않았습니다.");
    return 5;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\n*** 실패 ***\n{ex}");
    return 1;
}
