using DiskMigrator.Core.Abstractions;

namespace DiskMigrator.Core.Tests.Fakes;

/// <summary>
/// 메모리 기반 블록 장치. 특정 섹터를 "불량"으로 지정하거나, N번째 읽기까지만
/// 실패하게 만들어 불량 섹터 처리 경로를 테스트합니다.
/// </summary>
public class FaultyBlockDevice : IBlockDevice
{
    private readonly byte[] _data;
    private readonly Dictionary<long, int> _failuresRemaining = [];
    private readonly HashSet<long> _permanentlyBad = [];

    public string Id { get; }
    public long Length => _data.Length;
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }

    public FaultyBlockDevice(long length, int sectorSize = 512, bool canWrite = true, string id = "fake")
    {
        _data = new byte[length];
        SectorSize = sectorSize;
        CanWrite = canWrite;
        Id = id;
    }

    public byte[] Data => _data;

    /// <summary>이 오프셋의 섹터는 항상 읽기에 실패합니다 (완전히 죽은 섹터).</summary>
    public FaultyBlockDevice WithPermanentBadSector(long offset)
    {
        _permanentlyBad.Add(offset);
        return this;
    }

    /// <summary>
    /// 이 오프셋의 섹터는 처음 <paramref name="times"/>번 읽기에 실패하고 그 뒤엔 성공합니다.
    /// 노후 HDD가 재시도로 결국 읽히는 상황을 재현합니다.
    /// </summary>
    public FaultyBlockDevice WithTransientFailure(long offset, int times)
    {
        _failuresRemaining[offset] = times;
        return this;
    }

    /// <summary>장치를 검증 가능한 패턴으로 채웁니다.</summary>
    public FaultyBlockDevice FillWithPattern(int seed = 12345)
    {
        new Random(seed).NextBytes(_data);
        return this;
    }

    public virtual int Read(long offset, Span<byte> buffer)
    {
        ReadCount++;

        if (offset % SectorSize != 0) throw new ArgumentException("정렬되지 않은 오프셋", nameof(offset));
        if (buffer.Length % SectorSize != 0) throw new ArgumentException("정렬되지 않은 길이", nameof(buffer));

        // 요청 범위에 걸친 모든 섹터를 확인합니다.
        for (long sector = offset; sector < offset + buffer.Length; sector += SectorSize)
        {
            if (_permanentlyBad.Contains(sector))
            {
                throw new IOException($"모의 불량 섹터: 오프셋 {sector}");
            }

            if (_failuresRemaining.TryGetValue(sector, out int remaining) && remaining > 0)
            {
                _failuresRemaining[sector] = remaining - 1;
                throw new IOException($"모의 일시적 읽기 실패: 오프셋 {sector} (남은 실패 {remaining - 1}회)");
            }
        }

        if (offset >= Length) return 0;

        int toRead = (int)Math.Min(buffer.Length, Length - offset);
        _data.AsSpan((int)offset, toRead).CopyTo(buffer);
        return toRead;
    }

    public virtual void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        WriteCount++;

        if (!CanWrite) throw new InvalidOperationException("읽기 전용 장치");
        if (offset % SectorSize != 0) throw new ArgumentException("정렬되지 않은 오프셋", nameof(offset));
        if (buffer.Length % SectorSize != 0) throw new ArgumentException("정렬되지 않은 길이", nameof(buffer));
        if (offset + buffer.Length > Length) throw new IOException("장치 끝을 넘어선 쓰기");

        buffer.CopyTo(_data.AsSpan((int)offset));
    }

    public void Flush() { }

    public void Dispose() { }
}
