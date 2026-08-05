namespace DiskMigrator.Core.Registry;

/// <summary>부팅이 어디까지 진행됐는지.</summary>
public enum BootProgress
{
    /// <summary>판단할 근거가 없습니다(파일이 없거나 시각을 못 읽음).</summary>
    Unknown,

    /// <summary>
    /// 부트로더까지만 실행됐습니다. 커널은 시작하지 못했거나 디스크에 손도 대지 못했습니다.
    /// </summary>
    BootloaderOnly,

    /// <summary>커널이 시작해 레지스트리를 열었습니다.</summary>
    KernelStarted,

    /// <summary>장치 열거·드라이버 설치까지 갔습니다.</summary>
    DevicesEnumerated,

    /// <summary>부팅이 끝까지 진행됐습니다(로그온 단계 도달).</summary>
    BootCompleted,
}

/// <summary>부팅 흔적 파일 하나.</summary>
/// <param name="Name">파일의 짧은 이름.</param>
/// <param name="Exists">있는지.</param>
/// <param name="LastWriteUtc">마지막으로 쓰인 시각.</param>
/// <param name="SizeBytes">크기.</param>
/// <param name="Stage">이 파일이 갱신됐다면 어느 단계까지 갔다는 뜻인지.</param>
/// <param name="Meaning">사람이 읽을 설명.</param>
public sealed record BootTraceFile(
    string Name,
    bool Exists,
    DateTime? LastWriteUtc,
    long? SizeBytes,
    BootProgress Stage,
    string Meaning);

/// <summary>부팅 흔적 분석 결과.</summary>
/// <param name="Files">확인한 파일들.</param>
/// <param name="LastAttemptUtc">마지막 부팅 시도 시각(부트로더가 남긴 것).</param>
/// <param name="Progress">그 시도가 어디까지 갔는지.</param>
/// <param name="NtbtlogTailLines">부팅 로깅이 켜져 있었다면 마지막 몇 줄 — 멈춘 지점의 단서.</param>
/// <param name="NtbtlogNotLoaded">로드되지 못한 드라이버 줄.</param>
public sealed record BootTraceResult(
    IReadOnlyList<BootTraceFile> Files,
    DateTime? LastAttemptUtc,
    BootProgress Progress,
    IReadOnlyList<string> NtbtlogTailLines,
    IReadOnlyList<string> NtbtlogNotLoaded);

/// <summary>
/// 디스크에 남은 흔적으로 <b>마지막 부팅이 어디까지 갔는지</b> 판정합니다.
/// </summary>
/// <remarks>
/// 부팅이 실패하면 화면에는 아무 단서가 없지만, 디스크에는 남습니다.
/// 부트로더와 커널이 <b>서로 다른 파일</b>을 건드리기 때문에, 무엇이 갱신됐고 무엇이 그대로인지를
/// 비교하면 어느 구간에서 멈췄는지 알 수 있습니다.
///
/// <para>2026-08-04 조사에서 결정적이었던 판단이 이것입니다 — <c>bootstat.dat</c>만 최신이고
/// 하이브·이벤트로그는 며칠 전 그대로라는 사실 하나로 "커널이 시작조차 못 했다"가 확정됐고,
/// 원인 후보가 단숨에 좁혀졌습니다. 그때는 눈으로 타임스탬프를 대조했습니다.</para>
/// </remarks>
public static class BootTraceAnalysis
{
    /// <summary>
    /// 같은 부팅으로 볼 시간 창. 부트로더가 쓴 시각에서 이 안에 갱신된 파일은 같은 시도로 봅니다.
    /// </summary>
    /// <remarks>
    /// 첫 부팅은 드라이버 설치·업데이트 정리로 길어질 수 있어 넉넉하게 잡습니다.
    /// 너무 좁으면 실제로는 이어진 한 번의 부팅을 별개로 오판합니다.
    /// </remarks>
    private static readonly TimeSpan SameBootWindow = TimeSpan.FromHours(2);

    /// <param name="windowsRoot">예: <c>"C:\"</c> 또는 <c>"\\?\Volume{...}\"</c>.</param>
    public static BootTraceResult Inspect(string windowsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsRoot);
        string root = windowsRoot.EndsWith('\\') ? windowsRoot : windowsRoot + "\\";

        // 각 파일이 "어느 단계에서 쓰이는가"가 판정의 근거입니다.
        (string Rel, string Name, BootProgress Stage, string Meaning)[] targets =
        [
            (@"Windows\bootstat.dat", "bootstat.dat", BootProgress.BootloaderOnly,
             "Written by the boot manager/loader at the start of a boot attempt."),
            (@"Windows\System32\config\SYSTEM", "SYSTEM hive", BootProgress.KernelStarted,
             "The kernel opens and writes this once it starts."),
            (@"Windows\System32\config\SOFTWARE", "SOFTWARE hive", BootProgress.KernelStarted,
             "Written once the system is running."),
            (@"Windows\System32\winevt\Logs\System.evtx", "System event log", BootProgress.KernelStarted,
             "Event logging starts after the kernel is up."),
            (@"Windows\INF\setupapi.dev.log", "setupapi.dev.log", BootProgress.DevicesEnumerated,
             "Device enumeration and driver installation reached."),
            (@"Windows\Logs\CBS\CBS.log", "CBS.log", BootProgress.BootCompleted,
             "Servicing runs late in boot, after logon is possible."),
            (@"Windows\ntbtlog.txt", "ntbtlog.txt", BootProgress.KernelStarted,
             "Present only when boot logging is enabled; the kernel writes it as drivers load."),
        ];

        var files = new List<BootTraceFile>();
        foreach (var t in targets)
        {
            DateTime? when = null;
            long? size = null;
            bool exists = false;
            try
            {
                var fi = new FileInfo(Path.Combine(root, t.Rel));
                exists = fi.Exists;
                if (exists) { when = fi.LastWriteTimeUtc; size = fi.Length; }
            }
            catch
            {
                // 접근 불가는 "없음"으로 두고 계속합니다 — 한 파일 때문에 분석을 포기하지 않습니다.
            }

            files.Add(new BootTraceFile(t.Name, exists, when, size, t.Stage, t.Meaning));
        }

        DateTime? attempt = LastAttemptOf(files);
        BootProgress progress = Judge(files);

        var (tail, notLoaded) = ReadNtbtlog(Path.Combine(root, @"Windows\ntbtlog.txt"));

        return new BootTraceResult(files, attempt, progress, tail, notLoaded);
    }

    /// <summary>
    /// 부트로더가 마지막으로 흔적을 남긴 시각.
    /// </summary>
    /// <remarks>
    /// <b>이것을 "부팅을 시작한 시각"으로 읽으면 안 됩니다.</b> <c>bootstat.dat</c>는 부팅을 시작할 때뿐
    /// 아니라 <b>종료할 때도</b> 갱신됩니다 — 성공한 부팅에서는 이 값이 사실상 종료 시각입니다.
    /// </remarks>
    public static DateTime? LastAttemptOf(IReadOnlyList<BootTraceFile> files) =>
        files.FirstOrDefault(f => f.Stage == BootProgress.BootloaderOnly)?.LastWriteUtc;

    /// <summary>
    /// 흔적 파일들의 시각만 보고 <b>어디까지 갔는지</b>를 판정합니다 — 파일시스템을 건드리지 않는
    /// 순수 함수입니다.
    /// </summary>
    /// <remarks>
    /// 이 판정이 도구의 핵심 산출물이라 따로 떼어 두었습니다. 파일 I/O에 묶여 있으면
    /// "부트로더만 실행됨" 같은 상황을 테스트로 고정할 수 없습니다.
    /// </remarks>
    public static BootProgress Judge(IReadOnlyList<BootTraceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        // 부트로더 흔적이 없으면 부팅을 시도한 적이 있는지조차 알 수 없습니다.
        if (LastAttemptOf(files) is null) return BootProgress.Unknown;

        // 기준은 '이 디스크의 가장 최근 활동'입니다.
        //
        // 부트로더 시각을 원점으로 삼으면 안 됩니다 — bootstat.dat는 부팅을 시작할 때뿐 아니라
        // 종료할 때도 갱신되기 때문입니다. 성공한 부팅에서는 그 값이 사실상 종료 시각이 되고,
        // 부팅 도중에 쓰인 파일들이 전부 '그보다 이전'으로 보여 단계를 낮게 판정하게 됩니다.
        // (실물 M.2 검증에서 발견 — 1시간을 쓰고 정상 종료한 부팅을 DevicesEnumerated로 읽었습니다.)
        //
        // 부팅이 실패한 경우에는 bootstat이 곧 가장 최근 시각이므로 판정이 달라지지 않습니다.
        DateTime latest = files.Where(f => f.LastWriteUtc is not null).Max(f => f.LastWriteUtc!.Value);

        var progress = BootProgress.BootloaderOnly;
        foreach (var f in files)
        {
            if (f.LastWriteUtc is not { } tf || f.Stage == BootProgress.BootloaderOnly) continue;

            // 마지막 활동으로부터 같은 부팅으로 볼 만큼 가까우면 그 단계에 도달한 것입니다.
            if (latest - tf < SameBootWindow && f.Stage > progress)
                progress = f.Stage;
        }

        return progress;
    }

    /// <summary>
    /// 부팅 로깅 파일에서 마지막 줄과 실패 항목을 추립니다. 전문을 싣지는 않습니다 — 수백 줄입니다.
    /// </summary>
    private static (IReadOnlyList<string> Tail, IReadOnlyList<string> NotLoaded) ReadNtbtlog(string path)
    {
        try
        {
            if (!File.Exists(path)) return ([], []);

            var lines = File.ReadAllLines(path);
            var tail = lines.TakeLast(20).ToList();
            var notLoaded = lines
                .Where(l => l.Contains("NOT_LOADED", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("did not load", StringComparison.OrdinalIgnoreCase))
                .TakeLast(20)
                .ToList();
            return (tail, notLoaded);
        }
        catch
        {
            return ([], []);
        }
    }
}
