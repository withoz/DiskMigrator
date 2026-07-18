using DiskMigrator.Core.Models;
using DiskMigrator.Core.Registry;

namespace DiskMigrator.VhdTest;

/// <summary>
/// 디스크의 마운트된 볼륨을 통해 부팅 구성을 정적으로 검사하는 진단(읽기 전용).
/// 클론 직후 자동으로, 또는 <c>--boot-check &lt;디스크번호&gt;</c>로 단독 실행합니다.
/// </summary>
internal static class BootCheck
{
    public static int Run(DiskInfo disk)
    {
        Console.WriteLine($"\n=== 부팅 구성 정적 검사: [{disk.DeviceNumber}] {disk.Model} ===\n");

        var input = BootReadinessCheck.ResolveInput(disk);
        Console.WriteLine($"  부팅 방식      : {(input.Uefi ? "UEFI (GPT/EFI 파티션)" : "BIOS (MBR/활성 파티션)")}");
        Console.WriteLine($"  시스템 파티션  : {input.SystemRoot ?? "(마운트 안 됨)"}");
        Console.WriteLine($"  Windows 파티션 : {input.WindowsRoot ?? "(찾지 못함)"}");
        Console.WriteLine();

        if (input.SystemRoot is null && input.WindowsRoot is null)
        {
            Console.WriteLine("경고: 이 디스크의 볼륨이 하나도 마운트되어 있지 않습니다.");
            Console.WriteLine("      (원본과 같은 서명의 클론이 오프라인 상태이면 흔합니다. 디스크를 온라인으로");
            Console.WriteLine("       전환한 뒤 다시 실행하거나, 실제 대상 PC에서 확인하세요.)\n");
        }

        var report = BootReadinessCheck.Inspect(input);
        Print(report);

        bool anyFatalFailed = report.Items.Any(i =>
            i.Severity == BootCheckSeverity.Fatal && i.Passed == false);

        Console.WriteLine();
        if (report.WouldBoot && !report.HasWarnings)
            Console.WriteLine("*** 부팅 준비 완료 — 치명 항목 모두 통과 ***");
        else if (report.WouldBoot)
            Console.WriteLine("*** 부팅 가능하나 경고 있음 — 위 경고 항목을 확인하세요 ***");
        else if (anyFatalFailed)
            Console.WriteLine("*** 부팅 불가 위험 — 치명(Fatal) 항목이 실패했습니다 ***");
        else
            Console.WriteLine("*** 판정 불가 — 치명 항목을 확인하지 못했습니다 " +
                              "(오프라인 클론 대상에서 실행하세요) ***");

        return report.WouldBoot ? 0 : 1;
    }

    private static void Print(BootReadinessReport report)
    {
        foreach (var item in report.Items)
        {
            string mark = item.Passed switch
            {
                true => "[통과]",
                false => item.Severity == BootCheckSeverity.Fatal ? "[실패]" : "[경고]",
                null => "[생략]",
            };
            string sev = item.Severity switch
            {
                BootCheckSeverity.Fatal => "치명",
                BootCheckSeverity.Warning => "경고",
                _ => "정보",
            };
            Console.WriteLine($"  {mark} ({sev}) {item.Name}");
            Console.WriteLine($"          {item.Detail}");
        }
    }
}
