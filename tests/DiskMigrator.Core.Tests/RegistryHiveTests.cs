using System.Text;
using DiskMigrator.Core.Registry;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 하이브 직접 편집기(Universal Restore의 핵심)를 검증합니다.
/// </summary>
/// <remarks>
/// 실제 하이브로는 실기에서 검증했지만(하이브를 reg.exe가 못 여는 상황에서도 우리 파서가
/// 읽고 씀), 회귀 방지를 위해 최소 합성 하이브로 핵심 로직 — DWORD 인라인 읽기/쓰기,
/// 값 탐색, 체크섬 재계산 — 을 고정합니다.
/// </remarks>
public class RegistryHiveTests
{
    private const int BaseBlock = 0x1000;

    /// <summary>
    /// 루트 키 하나에 REG_DWORD 값 "Start"(인라인)를 가진 최소 유효 하이브를 만듭니다.
    /// </summary>
    private static byte[] BuildMinimalHive(uint startValue)
    {
        var data = new byte[0x2000]; // 4KB 베이스 + 4KB hbin

        // --- 베이스 블록 ---
        Encoding.ASCII.GetBytes("regf").CopyTo(data, 0);
        WriteU32(data, 0x04, 1);          // seq1
        WriteU32(data, 0x08, 1);          // seq2
        WriteU32(data, 0x14, 1);          // major
        WriteU32(data, 0x18, 3);          // minor
        WriteU32(data, 0x1C, 0);          // type = primary
        WriteU32(data, 0x20, 1);          // format
        WriteU32(data, 0x24, 0x20);       // 루트 셀 오프셋 (0x1000 기준)
        WriteU32(data, 0x28, 0x1000);     // hbin 데이터 크기
        WriteU32(data, 0x2C, 1);          // clustering

        // --- hbin (0x1000) ---
        Encoding.ASCII.GetBytes("hbin").CopyTo(data, 0x1000);
        WriteU32(data, 0x1004, 0);        // 첫 hbin 기준 오프셋
        WriteU32(data, 0x1008, 0x1000);   // hbin 크기

        // 셀들은 hbin 헤더(0x20) 다음부터. 셀 오프셋(0x1000 기준):
        //   루트 NK  = 0x20  (파일 0x1020)
        //   값 목록  = 0x80  (파일 0x1080)
        //   VK Start = 0x90  (파일 0x1090)

        // 루트 NK 셀 @ 0x1020
        WriteI32(data, 0x1020, -0x60);            // 셀 크기(음수=사용중)
        int nk = 0x1024;
        Encoding.ASCII.GetBytes("nk").CopyTo(data, nk + 0x00);
        WriteU16(data, nk + 0x02, 0x20);          // flags: 이름 ASCII
        WriteU32(data, nk + 0x14, 0);             // 하위키 수
        WriteU32(data, nk + 0x1C, 0xFFFFFFFF);    // 하위키 목록 없음
        WriteU32(data, nk + 0x24, 1);             // 값 수 = 1
        WriteU32(data, nk + 0x28, 0x80);          // 값 목록 오프셋
        WriteU16(data, nk + 0x48, 4);             // 이름 길이
        Encoding.ASCII.GetBytes("Root").CopyTo(data, nk + 0x4C);

        // 값 목록 셀 @ 0x1080 : uint 하나(VK 오프셋)
        WriteI32(data, 0x1080, -0x10);
        WriteU32(data, 0x1084, 0x90);             // VK 셀 오프셋

        // VK "Start" 셀 @ 0x1090
        WriteI32(data, 0x1090, -0x30);
        int vk = 0x1094;
        Encoding.ASCII.GetBytes("vk").CopyTo(data, vk + 0x00);
        WriteU16(data, vk + 0x02, 5);             // 이름 길이 "Start"
        WriteU32(data, vk + 0x04, 4u | 0x80000000u); // 데이터 길이 4, 인라인
        WriteU32(data, vk + 0x08, startValue);    // 인라인 데이터
        WriteU32(data, vk + 0x0C, 4);             // 타입 REG_DWORD
        WriteU16(data, vk + 0x10, 1);             // flags: 이름 ASCII
        Encoding.ASCII.GetBytes("Start").CopyTo(data, vk + 0x14);

        return data;
    }

    private static void WriteU32(byte[] d, int off, uint v) => BitConverter.GetBytes(v).CopyTo(d, off);
    private static void WriteI32(byte[] d, int off, int v) => BitConverter.GetBytes(v).CopyTo(d, off);
    private static void WriteU16(byte[] d, int off, ushort v) => BitConverter.GetBytes(v).CopyTo(d, off);

    [Fact]
    public void regf가_아니면_거부한다()
    {
        var bad = new byte[0x2000];
        Assert.Throws<InvalidDataException>(() => new RegistryHive(bad));
    }

    [Fact]
    public void 인라인_DWORD를_읽는다()
    {
        var hive = new RegistryHive(BuildMinimalHive(startValue: 3));
        Assert.Equal(3u, hive.GetDword("", "Start"));
    }

    [Fact]
    public void 인라인_DWORD를_쓰고_다시_읽으면_바뀐다()
    {
        var hive = new RegistryHive(BuildMinimalHive(startValue: 3));

        Assert.True(hive.SetDword("", "Start", 0));
        Assert.Equal(0u, hive.GetDword("", "Start"));

        // 직렬화 후 다시 파싱해도 유지되어야 합니다.
        var round = new RegistryHive(hive.ToArray());
        Assert.Equal(0u, round.GetDword("", "Start"));
    }

    [Fact]
    public void 없는_값은_null이고_쓰기는_false다()
    {
        var hive = new RegistryHive(BuildMinimalHive(3));
        Assert.Null(hive.GetDword("", "NoSuchValue"));
        Assert.False(hive.SetDword("", "NoSuchValue", 0));
    }

    [Fact]
    public void 없는_키는_존재하지_않는다()
    {
        var hive = new RegistryHive(BuildMinimalHive(3));
        Assert.False(hive.KeyExists("ControlSet001\\Services\\storahci"));
        Assert.Null(hive.GetDword("NoKey", "Start"));
    }

    [Fact]
    public void 체크섬을_다시_계산하면_시퀀스가_일치한다()
    {
        var data = BuildMinimalHive(3);
        // seq2를 일부러 어긋나게
        WriteU32(data, 0x08, 99);
        var hive = new RegistryHive(data);

        hive.SetDword("", "Start", 0);
        hive.MarkCleanAndUpdateChecksum();

        var final = hive.ToArray();
        uint seq1 = BitConverter.ToUInt32(final, 0x04);
        uint seq2 = BitConverter.ToUInt32(final, 0x08);
        Assert.Equal(seq1, seq2); // 깨끗함 표시

        // 체크섬 = 앞 0x1FC 바이트의 DWORD XOR
        uint xor = 0;
        for (int i = 0; i < 0x1FC; i += 4) xor ^= BitConverter.ToUInt32(final, i);
        if (xor == 0) xor = 1; else if (xor == 0xFFFFFFFF) xor = 0xFFFFFFFE;
        Assert.Equal(xor, BitConverter.ToUInt32(final, 0x1FC));
    }
}
