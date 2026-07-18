using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;

namespace DiskMigrator.Probe;

/// <summary>
/// 두 디스크의 같은 오프셋을 실제로 읽어 바이트 단위로 비교합니다. 읽기 전용입니다.
/// </summary>
/// <remarks>
/// 검증 실패의 원인을 추측이 아니라 측정으로 좁히기 위한 도구입니다.
/// "어느 쪽이 바뀌었는가"를 알아야 원인(우리가 잘못 썼는가 / 다른 무언가가 나중에 썼는가)을
/// 구분할 수 있습니다.
/// </remarks>
internal static class DiffCheck
{
    public static void Run(int diskA, int diskB, long offset, long length)
    {
        using var a = RawDiskDevice.OpenRead($@"\\.\PhysicalDrive{diskA}");
        using var b = RawDiskDevice.OpenRead($@"\\.\PhysicalDrive{diskB}");

        Console.WriteLine($"=== 디스크 {diskA} vs 디스크 {diskB} 비교 ===");
        Console.WriteLine($"범위: 오프셋 {offset:N0} 부터 {SizeFormatter.Format(length)}\n");

        int sector = Math.Max(a.SectorSize, b.SectorSize);
        const int chunkSize = 4 * 1024 * 1024;

        using var bufA = new AlignedBuffer(chunkSize);
        using var bufB = new AlignedBuffer(chunkSize);

        long position = 0;
        long differingBytes = 0;
        int ranges = 0;
        long firstDiff = -1;
        var samples = new List<(long Offset, byte A, byte B)>();

        while (position < length)
        {
            int chunk = (int)Math.Min(chunkSize, length - position);
            chunk -= chunk % sector;
            if (chunk == 0) break;

            long at = offset + position;

            int readA = a.Read(at, bufA.SpanOf(chunk));
            int readB = b.Read(at, bufB.SpanOf(chunk));
            int comparable = Math.Min(readA, readB);

            if (comparable == 0) break;

            var spanA = bufA.SpanOf(comparable);
            var spanB = bufB.SpanOf(comparable);

            if (!spanA.SequenceEqual(spanB))
            {
                // 섹터 단위로 좁혀서 어디가 다른지 셉니다.
                bool inRange = false;

                for (int s = 0; s + sector <= comparable; s += sector)
                {
                    if (spanA.Slice(s, sector).SequenceEqual(spanB.Slice(s, sector)))
                    {
                        inRange = false;
                        continue;
                    }

                    differingBytes += sector;
                    if (!inRange) { ranges++; inRange = true; }
                    if (firstDiff < 0) firstDiff = at + s;

                    if (samples.Count < 8)
                    {
                        for (int k = 0; k < sector; k++)
                        {
                            if (spanA[s + k] != spanB[s + k])
                            {
                                samples.Add((at + s + k, spanA[s + k], spanB[s + k]));
                                break;
                            }
                        }
                    }
                }
            }

            position += comparable;
        }

        Console.WriteLine($"  비교한 바이트 : {SizeFormatter.Format(position)}");
        Console.WriteLine($"  다른 바이트   : {SizeFormatter.Format(differingBytes)} " +
                          $"({(position > 0 ? differingBytes * 100.0 / position : 0):F4}%)");
        Console.WriteLine($"  불일치 구간   : {ranges:N0}개");
        Console.WriteLine($"  첫 불일치     : {(firstDiff < 0 ? "없음" : $"오프셋 {firstDiff:N0}")}");

        if (samples.Count > 0)
        {
            Console.WriteLine("\n  차이 샘플 (오프셋: 디스크A → 디스크B):");
            foreach (var (o, va, vb) in samples)
            {
                Console.WriteLine($"    {o,15:N0}: 0x{va:X2} → 0x{vb:X2}");
            }
        }

        Console.WriteLine(differingBytes == 0 ? "\n  *** 완전히 일치 ***" : "\n  *** 차이 있음 ***");
    }

    /// <summary>지정한 오프셋의 한 섹터를 16진수로 덤프합니다.</summary>
    public static void Dump(int disk, long offset, int bytes = 512)
    {
        using var d = RawDiskDevice.OpenRead($@"\\.\PhysicalDrive{disk}");
        using var buf = new AlignedBuffer(Math.Max(4096, bytes));

        int read = d.Read(offset, buf.SpanOf(Math.Max(d.SectorSize, bytes)));
        var span = buf.SpanOf(bytes);

        Console.WriteLine($"=== 디스크 {disk} 오프셋 {offset:N0} ({read}바이트 읽음) ===\n");

        // Span은 람다에 캡처할 수 없으므로 평범한 루프로 씁니다.
        for (int i = 0; i < bytes; i += 16)
        {
            var hex = new System.Text.StringBuilder();
            var ascii = new System.Text.StringBuilder();

            for (int j = i; j < Math.Min(i + 16, bytes); j++)
            {
                hex.Append(span[j].ToString("X2")).Append(' ');
                ascii.Append(span[j] is >= 32 and < 127 ? (char)span[j] : '.');
            }

            Console.WriteLine($"  {i:X4}  {hex.ToString().TrimEnd(),-47}  {ascii}");
        }
    }
}
