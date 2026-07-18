namespace DiskMigrator.Cli;

internal sealed class CliOptions
{
    public required int SourceNumber { get; init; }
    public required int TargetNumber { get; init; }
    public bool UseSnapshot { get; init; } = true;
    public bool Verify { get; init; } = true;
    public bool ZeroFillBadSectors { get; init; }
    public bool UniversalRestore { get; init; }
    public string? ConfirmModel { get; init; }
    public int BufferSizeMb { get; init; } = 4;
    public int ProgressSeconds { get; init; } = 10;

    public static CliOptions? Parse(string[] args)
    {
        if (args.Length < 2 ||
            !int.TryParse(args[0], out int source) ||
            !int.TryParse(args[1], out int target))
        {
            PrintUsage();
            return null;
        }

        return new CliOptions
        {
            SourceNumber = source,
            TargetNumber = target,
            UseSnapshot = !args.Contains("--no-snapshot"),
            Verify = !args.Contains("--no-verify"),
            ZeroFillBadSectors = args.Contains("--skip-bad-sectors"),
            UniversalRestore = args.Contains("--universal-restore"),
            ConfirmModel = GetValue(args, "--confirm"),
            BufferSizeMb = int.TryParse(GetValue(args, "--buffer-mb"), out int mb) ? mb : 4,
            ProgressSeconds = int.TryParse(GetValue(args, "--progress-seconds"), out int s) ? s : 10,
        };
    }

    private static string? GetValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            DiskMigrator CLI — 디스크 전체를 섹터 단위로 복제합니다.

            사용법:
              DiskMigrator.Cli <원본디스크번호> <대상디스크번호> --confirm "<대상 모델명>" [옵션]

            대상 디스크의 모든 데이터가 영구히 삭제됩니다. 실수를 막기 위해 대상 디스크의
            모델명을 --confirm 으로 정확히 넘겨야만 진행합니다. 디스크 번호는 장치를
            다시 연결하면 바뀔 수 있으므로 번호만으로는 확인이 되지 않습니다.

            옵션:
              --confirm "<모델명>"     대상 디스크 모델명 (필수)
              --no-snapshot            VSS 스냅샷을 쓰지 않습니다. 실행 중인 디스크를 원본으로
                                       삼는다면 결과물이 일관되지 않으므로 권장하지 않습니다.
              --no-verify              복제 후 검증을 생략합니다 (시간 절반, 정확성 미확인).
              --skip-bad-sectors       읽지 못한 섹터를 0으로 채우고 계속합니다.
              --universal-restore      클론 후 대상 Windows를 하드웨어 독립화합니다.
                                       (표준 저장소 드라이버를 부팅 시작으로 → 다른 PC에서도 부팅)
                                       기본값은 불량 섹터 발견 시 중단입니다.
              --buffer-mb <N>          I/O 버퍼 크기 (기본 4).
              --progress-seconds <N>   진행 표시 간격 (기본 10).

            디스크 번호는 진단 도구로 확인하십시오:
              DiskMigrator.Probe

            종료 코드: 0 성공 / 1 실패 / 3 권한 없음 / 5 안전 검사 차단 / 6 확인 실패 / 7 취소
            """);
    }
}
