namespace DiskMigrator.Core.Partitioning;

/// <summary>
/// GPT가 요구하는 CRC-32 (IEEE 802.3, zlib와 동일한 다항식/초기값).
/// </summary>
/// <remarks>
/// 외부 패키지를 쓰지 않고 직접 둔 이유: GPT 헤더 검사합이 틀리면 Windows가
/// 디스크를 손상된 것으로 보고 사용자가 클론 실패로 오해합니다. 구현이 20줄이라
/// 의존성을 늘릴 이유가 없고, 테스트로 알려진 값을 고정해 검증합니다.
/// </remarks>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }
            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
