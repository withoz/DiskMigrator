namespace DiskMigrator.Mcp.Dto;

/// <summary>부팅 흔적 분석 결과.</summary>
/// <param name="LastAttemptUtc">마지막 부팅 시도 시각(부트로더가 남긴 것). 없으면 판정 불가.</param>
/// <param name="Progress">
/// 어디까지 갔는지 — BootloaderOnly · KernelStarted · DevicesEnumerated · BootCompleted.
/// </param>
/// <param name="Verdict">
/// 그 단계가 <b>무엇을 뜻하는지</b>와 다음에 무엇을 봐야 하는지. Claude가 그대로 인용해도 되는 문장.
/// </param>
/// <param name="Files">확인한 파일과 각각의 마지막 기록 시각.</param>
/// <param name="NtbtlogTail">부팅 로깅이 켜져 있었다면 마지막 줄들 — 멈춘 지점의 단서.</param>
/// <param name="NtbtlogNotLoaded">로드되지 못한 드라이버.</param>
public sealed record BootTraceDto(
    DateTime? LastAttemptUtc,
    string Progress,
    string Verdict,
    IReadOnlyList<BootTraceFileDto> Files,
    IReadOnlyList<string> NtbtlogTail,
    IReadOnlyList<string> NtbtlogNotLoaded);

/// <summary>흔적 파일 하나.</summary>
public sealed record BootTraceFileDto(
    string Name,
    bool Exists,
    DateTime? LastWriteUtc,
    long? SizeBytes,
    string Stage,
    string Meaning);
