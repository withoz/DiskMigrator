using DiskMigrator.Core.Abstractions;

namespace DiskMigrator.Core.Tests.Fakes;

/// <summary>
/// 크기만 크다고 말하고 실제로는 첫 섹터만 담는 디스크.
/// </summary>
/// <remarks>
/// MBR 재작성은 0번 섹터만 읽고 씁니다. 그런데 배치가 맞는지 판단하려면 디스크가
/// 수백 GB라고 <b>말해야</b> 하므로, 그 크기만큼 실제 배열을 잡는 기존 페이크로는
/// 테스트할 수 없습니다(250 GB 배열).
/// </remarks>
public sealed class SparseDisk(long length, int sectorSize = 512) : IBlockDevice
{
    public byte[] Sector0 { get; } = new byte[sectorSize];

    public string Id => "sparse";
    public long Length { get; } = length;
    public int SectorSize { get; } = sectorSize;
    public bool CanWrite => true;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset != 0) throw new InvalidOperationException($"0번 섹터만 담는 페이크입니다(요청 {offset}).");

        int n = Math.Min(buffer.Length, Sector0.Length);
        Sector0.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (offset != 0) throw new InvalidOperationException($"0번 섹터만 담는 페이크입니다(요청 {offset}).");

        buffer[..Math.Min(buffer.Length, Sector0.Length)].CopyTo(Sector0);
    }

    public void Flush() { }
    public void Dispose() { }
}
