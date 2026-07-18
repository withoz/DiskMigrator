using DiskMigrator.Core.Abstractions;

namespace DiskMigrator.Core.Devices;

/// <summary>
/// 파일을 블록 장치처럼 다루는 구현.
/// </summary>
/// <remarks>
/// 두 가지 목적이 있습니다.
/// 1) 테스트: 실제 디스크를 건드리지 않고 클론 엔진 전체를 검증합니다. 이 프로젝트에서
///    "위험한 코드를 안전하게 테스트한다"는 설계의 핵심입니다.
/// 2) Phase 6의 디스크 → 이미지 파일(.img) 백업/복원에 그대로 재사용됩니다.
/// </remarks>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private readonly bool _canWrite;

    public string Id { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite => _canWrite;

    private FileBlockDevice(FileStream stream, string id, long length, int sectorSize, bool canWrite)
    {
        _stream = stream;
        Id = id;
        Length = length;
        SectorSize = sectorSize;
        _canWrite = canWrite;
    }

    public static FileBlockDevice OpenRead(string path, int sectorSize = 512)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new FileBlockDevice(stream, path, stream.Length, sectorSize, canWrite: false);
    }

    /// <summary>기존 파일을 읽기/쓰기로 엽니다. 크기는 바뀌지 않습니다.</summary>
    public static FileBlockDevice OpenWrite(string path, int sectorSize = 512)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return new FileBlockDevice(stream, path, stream.Length, sectorSize, canWrite: true);
    }

    /// <summary>지정한 크기의 파일을 새로 만들어 대상 장치로 씁니다.</summary>
    public static FileBlockDevice Create(string path, long length, int sectorSize = 512)
    {
        if (length % sectorSize != 0)
        {
            throw new ArgumentException(
                $"장치 크기({length})는 섹터 크기({sectorSize})의 배수여야 합니다.", nameof(length));
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(length);
        return new FileBlockDevice(stream, path, length, sectorSize, canWrite: true);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        ValidateAlignment(offset, buffer.Length);

        if (offset >= Length) return 0;

        // 장치 끝을 넘어가는 요청은 남은 만큼만 읽습니다 (원시 디스크와 같은 동작).
        int toRead = (int)Math.Min(buffer.Length, Length - offset);

        int total = 0;
        while (total < toRead)
        {
            int read = RandomAccess.Read(_stream.SafeFileHandle, buffer.Slice(total, toRead - total), offset + total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!_canWrite)
        {
            throw new InvalidOperationException($"{Id} 은(는) 읽기 전용으로 열렸습니다.");
        }

        ValidateAlignment(offset, buffer.Length);

        if (offset + buffer.Length > Length)
        {
            throw new IOException(
                $"{Id}: 장치 끝을 넘어서 쓰려 합니다 (오프셋 {offset:N0} + {buffer.Length:N0} > {Length:N0}).");
        }

        RandomAccess.Write(_stream.SafeFileHandle, buffer, offset);
    }

    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Dispose() => _stream.Dispose();

    private void ValidateAlignment(long offset, int length)
    {
        if (offset % SectorSize != 0)
        {
            throw new ArgumentException(
                $"오프셋 {offset}이(가) 섹터 크기 {SectorSize}에 정렬되지 않았습니다.", nameof(offset));
        }

        if (length % SectorSize != 0)
        {
            throw new ArgumentException(
                $"길이 {length}이(가) 섹터 크기 {SectorSize}의 배수가 아닙니다.", nameof(length));
        }
    }
}
