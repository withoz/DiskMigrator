using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Devices;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 실기에서 VSS 섀도 복사본이 "크기는 볼륨 전체로 보고하면서 마지막 4KB는 못 읽는" 동작을
/// 보였고, 보고된 크기를 믿었더니 복제가 끝에서 실패했습니다. 그 대응 로직의 경계 조건을
/// 검증합니다 — 이진 탐색은 off-by-one을 내기 쉬운 코드입니다.
/// </summary>
public class ReadableLengthProbeTests
{
    private const int Sector = 512;

    /// <summary>
    /// 크기는 <paramref name="reportedLength"/>로 보고하지만, 실제로는
    /// <paramref name="readableLength"/>까지만 읽히는 장치.
    /// </summary>
    private sealed class TruncatedDevice(long reportedLength, long readableLength, bool throwInsteadOfZero = false)
        : IBlockDevice
    {
        public int ReadCount { get; private set; }

        public string Id => "truncated";
        public long Length => reportedLength;
        public int SectorSize => Sector;
        public bool CanWrite => false;

        public int Read(long offset, Span<byte> buffer)
        {
            ReadCount++;

            if (offset >= readableLength)
            {
                // VSS 섀도 복사본은 0을 돌려주지만, 다른 장치는 예외를 던질 수 있습니다.
                if (throwInsteadOfZero) throw new IOException("모의 읽기 실패");
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, readableLength - offset);
            buffer[..toRead].Clear();
            return toRead;
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public void 끝까지_읽히면_상한을_그대로_돌려준다()
    {
        var device = new TruncatedDevice(reportedLength: 1024 * Sector, readableLength: 1024 * Sector);

        Assert.Equal(1024 * Sector, ReadableLengthProbe.Probe(device, 1024 * Sector));

        // 흔한 경우이므로 한 번의 읽기로 끝나야 합니다.
        Assert.Equal(1, device.ReadCount);
    }

    [Fact]
    public void 실기에서_관측된_4KB_부족을_정확히_찾아낸다()
    {
        // 파티션 1,056,899,072바이트 / 스냅샷 실제 읽기 가능 1,056,894,976바이트
        const long partitionLength = 1_056_899_072;
        const long readable = 1_056_894_976;

        var device = new TruncatedDevice(reportedLength: partitionLength, readableLength: readable);

        Assert.Equal(readable, ReadableLengthProbe.Probe(device, partitionLength));
    }

    [Theory]
    [InlineData(1)]    // 마지막 한 섹터만 못 읽음
    [InlineData(2)]
    [InlineData(8)]    // 실기에서 본 4096바이트 = 8섹터
    [InlineData(100)]
    [InlineData(1023)] // 첫 섹터만 읽힘
    public void 못_읽는_섹터_수가_얼마든_경계를_정확히_찾는다(int badSectors)
    {
        const int totalSectors = 1024;
        long readable = (long)(totalSectors - badSectors) * Sector;

        var device = new TruncatedDevice(reportedLength: totalSectors * Sector, readableLength: readable);

        Assert.Equal(readable, ReadableLengthProbe.Probe(device, totalSectors * Sector));
    }

    [Fact]
    public void 하나도_못_읽으면_0을_돌려준다()
    {
        var device = new TruncatedDevice(reportedLength: 1024 * Sector, readableLength: 0);

        Assert.Equal(0, ReadableLengthProbe.Probe(device, 1024 * Sector));
    }

    [Fact]
    public void 예외를_던지는_장치도_동일하게_처리한다()
    {
        var device = new TruncatedDevice(
            reportedLength: 1024 * Sector, readableLength: 1000 * Sector, throwInsteadOfZero: true);

        Assert.Equal(1000 * Sector, ReadableLengthProbe.Probe(device, 1024 * Sector));
    }

    [Fact]
    public void 상한이_장치_크기보다_작으면_상한을_넘지_않는다()
    {
        var device = new TruncatedDevice(reportedLength: 1024 * Sector, readableLength: 1024 * Sector);

        Assert.Equal(500 * Sector, ReadableLengthProbe.Probe(device, 500 * Sector));
    }

    [Fact]
    public void 상한이_섹터_정렬이_아니면_내림_정렬한다()
    {
        var device = new TruncatedDevice(reportedLength: 1024 * Sector, readableLength: 1024 * Sector);

        Assert.Equal(500 * Sector, ReadableLengthProbe.Probe(device, (500 * Sector) + 100));
    }

    [Fact]
    public void 이진_탐색이라_읽기_횟수가_로그_수준이다()
    {
        // 1GB / 512B = 2,097,152 섹터 → 선형 탐색이면 수백만 번, 이진 탐색이면 ~22번.
        const long oneGb = 1024L * 1024 * 1024;
        var device = new TruncatedDevice(reportedLength: oneGb, readableLength: oneGb - 4096);

        long result = ReadableLengthProbe.Probe(device, oneGb);

        Assert.Equal(oneGb - 4096, result);
        Assert.InRange(device.ReadCount, 1, 30);
    }
}
