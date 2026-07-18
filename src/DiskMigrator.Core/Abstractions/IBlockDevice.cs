namespace DiskMigrator.Core.Abstractions;

/// <summary>
/// 섹터 단위로 임의 접근 가능한 블록 장치. 실제 물리 디스크일 수도 있고,
/// 테스트용 파일일 수도 있습니다.
/// </summary>
/// <remarks>
/// 이 인터페이스가 이 프로젝트의 안전성 설계에서 가장 중요한 부분입니다.
/// 클론 엔진은 오직 이 인터페이스만 알기 때문에, 실제 디스크를 건드리지 않고
/// 파일 기반 가짜 장치로 엔진 전체를 테스트할 수 있습니다.
/// </remarks>
public interface IBlockDevice : IDisposable
{
    /// <summary>사람이 읽을 수 있는 식별자. 로그와 오류 메시지에 사용합니다.</summary>
    string Id { get; }

    /// <summary>장치의 전체 크기(바이트).</summary>
    long Length { get; }

    /// <summary>논리 섹터 크기(바이트). 모든 읽기/쓰기 오프셋과 길이는 이 값의 배수여야 합니다.</summary>
    int SectorSize { get; }

    bool CanWrite { get; }

    /// <summary>
    /// <paramref name="offset"/>에서 <paramref name="buffer"/> 길이만큼 읽습니다.
    /// </summary>
    /// <param name="offset">섹터 정렬된 바이트 오프셋.</param>
    /// <param name="buffer">길이가 섹터 크기의 배수인 버퍼.</param>
    /// <returns>실제로 읽은 바이트 수. 장치 끝에서는 요청보다 적을 수 있습니다.</returns>
    /// <exception cref="IOException">읽기 실패(예: 불량 섹터).</exception>
    int Read(long offset, Span<byte> buffer);

    /// <summary>
    /// <paramref name="offset"/>에 <paramref name="buffer"/> 전체를 씁니다.
    /// </summary>
    /// <param name="offset">섹터 정렬된 바이트 오프셋.</param>
    /// <param name="buffer">길이가 섹터 크기의 배수인 버퍼.</param>
    void Write(long offset, ReadOnlySpan<byte> buffer);

    /// <summary>버퍼된 쓰기를 물리 매체까지 밀어냅니다.</summary>
    void Flush();
}
