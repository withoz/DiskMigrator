using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// 오프라인 Windows(SYSTEM 하이브 파일)의 최대 절전·빠른 시작을 <b>강제로</b> 끕니다 —
/// 값이 없으면 만들어서라도.
/// </summary>
/// <remarks>
/// 자체 하이브 편집기(<c>RegistryHive.SetDword</c>)는 기존 값 덮어쓰기 전용이라, 원본
/// 레지스트리에 해당 값이 애초에 없던 시스템에서는 끄지 못하고 조용히 넘어갔습니다 — 실기에서
/// "부팅 복구 → 한 번 부팅 → 종료 때 재개 이미지 재생성 → 다시 멈춤"의 무한 루프로
/// 나타났습니다. 값 생성은 regf 셀 할당이 필요한 큰 수술이라, 검증된 도구인 <c>reg.exe</c>의
/// load/add/unload로 처리합니다(없으면 생성, 있으면 덮어씀).
///
/// <para>끄는 값 (모든 ControlSet00N에):</para>
/// <list type="bullet">
/// <item><c>Control\Session Manager\Power\HiberbootEnabled=0</c> — 빠른 시작 끔</item>
/// <item><c>Control\Power\HibernateEnabled=0</c> — 최대 절전 자체를 끔(빠른 시작 설정이
///   되살아나도 재개 이미지를 만들 수단이 없어짐 — <c>powercfg /h off</c>와 같은 효과)</item>
/// <item><c>Control\Power\HibernateEnabledDefault=0</c> — 일부 시스템이 기본값 복원에 쓰는 값</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OfflineHibernationDisabler
{
    private const string MountName = @"HKLM\DM_OFFSYS";

    /// <summary>
    /// SYSTEM 하이브 파일에 적용합니다. 하나 이상의 값이 기록되면 true.
    /// 실패는 로그만 남기고 false — 호출자의 큰 흐름(복구·UR)을 막지 않습니다.
    /// </summary>
    public static bool Apply(string systemHivePath, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;

        if (!File.Exists(systemHivePath))
        {
            log.LogWarning("SYSTEM 하이브가 없습니다: {Path}", systemHivePath);
            return false;
        }

        var (loadCode, loadOut) = Run("load", MountName, systemHivePath);
        if (loadCode != 0)
        {
            log.LogWarning("reg load 실패({Code}): {Out}", loadCode, loadOut.Trim());
            return false;
        }

        bool any = false;
        try
        {
            for (int n = 1; n <= 9; n++)
            {
                string cs = $@"{MountName}\ControlSet{n:D3}";
                if (Run("query", cs).Code != 0) continue;

                any |= Add($@"{cs}\Control\Session Manager\Power", "HiberbootEnabled", log);
                any |= Add($@"{cs}\Control\Power", "HibernateEnabled", log);
                any |= Add($@"{cs}\Control\Power", "HibernateEnabledDefault", log);
            }
        }
        finally
        {
            // 언로드에 실패하면 하이브가 잠겨 대상 부팅에 문제가 될 수 있으므로 재시도합니다.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var (code, _) = Run("unload", MountName);
                if (code == 0) break;
                Thread.Sleep(300);
                if (attempt == 4) log.LogWarning("reg unload 실패 — 하이브 핸들이 남아 있을 수 있습니다.");
            }
        }

        if (any) log.LogInformation("오프라인 최대 절전/빠른 시작 강제 끔: {Path}", systemHivePath);
        return any;
    }

    private static bool Add(string key, string valueName, ILogger log)
    {
        var (code, output) = Run("add", key, "/v", valueName, "/t", "REG_DWORD", "/d", "0", "/f");
        if (code != 0)
        {
            log.LogWarning("reg add {Key}\\{Value} 실패({Code}): {Out}", key, valueName, code, output.Trim());
            return false;
        }
        return true;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // 출력이 작아도 순서는 지킵니다: 끝까지 읽은 뒤 대기(파이프 교착 예방 — chkdsk 교훈).
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
