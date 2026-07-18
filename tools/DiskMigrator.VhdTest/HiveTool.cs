using DiskMigrator.Core.Registry;
using Microsoft.Extensions.Logging;

namespace DiskMigrator.VhdTest;

/// <summary>
/// 하이브 직접 편집(Universal Restore)을 실제 하이브 파일로 검증하는 진단 명령.
/// </summary>
internal static class HiveTool
{
    private static readonly string[] Drivers =
        ["storahci", "stornvme", "iaStorV", "iaStorAV", "iaStorAC", "msahci", "pciide", "atapi"];

    /// <summary>하이브에서 저장소 드라이버들의 현재 Start 값을 출력합니다 (읽기 전용).</summary>
    public static int Read(string hivePath)
    {
        if (!File.Exists(hivePath)) { Console.Error.WriteLine($"파일 없음: {hivePath}"); return 4; }

        var hive = RegistryHive.Load(hivePath);
        Console.WriteLine($"=== 하이브 읽기: {hivePath} ===\n");

        // 활성 컨트롤 세트
        uint? current = hive.GetDword("Select", "Current");
        Console.WriteLine($"Select\\Current = {(current?.ToString() ?? "?")}\n");

        for (int n = 1; n <= 3; n++)
        {
            string cs = $"ControlSet{n:D3}";
            if (!hive.KeyExists(cs)) continue;
            Console.WriteLine($"[{cs}]");
            foreach (string d in Drivers)
            {
                string key = $"{cs}\\Services\\{d}";
                if (!hive.KeyExists(key)) { Console.WriteLine($"  {d,-10} : (없음)"); continue; }
                uint? start = hive.GetDword(key, "Start");
                string meaning = start switch
                {
                    0 => "부팅 시작 ★",
                    1 => "시스템 시작",
                    3 => "수동",
                    4 => "비활성",
                    _ => "?",
                };
                Console.WriteLine($"  {d,-10} : Start={start} ({meaning})");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>하이브에 Universal Restore를 적용합니다 (저장소 드라이버 Start=0).</summary>
    public static int Fix(string hivePath, ILogger logger)
    {
        if (!File.Exists(hivePath)) { Console.Error.WriteLine($"파일 없음: {hivePath}"); return 4; }

        Console.WriteLine($"=== Universal Restore 적용: {hivePath} ===\n");
        var result = UniversalRestore.Apply(hivePath, logger);

        Console.WriteLine($"\n부팅 시작으로 설정한 드라이버 {result.Enabled.Count}개:");
        foreach (var e in result.Enabled) Console.WriteLine($"  {e}");
        Console.WriteLine($"\n컨트롤 세트: {string.Join(", ", result.ControlSets)}");
        Console.WriteLine(result.AnyChanged
            ? "\n*** 적용 완료 — 하이브가 하드웨어 독립화되었습니다 ***"
            : "\n*** 변경된 드라이버가 없습니다 (이미 설정됨?) ***");
        return 0;
    }
}
