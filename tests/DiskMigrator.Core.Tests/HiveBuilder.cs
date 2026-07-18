using System.Text;

namespace DiskMigrator.Core.Tests;

/// <summary>
/// 테스트용 최소 regf 하이브 작성기. <see cref="DiskMigrator.Core.Registry.RegistryHive"/>
/// 파서가 읽는 구조(nk/vk/li 목록, 인라인 DWORD, 비인라인 REG_SZ)만 정확히 생성합니다.
/// </summary>
/// <remarks>
/// reg.exe의 하이브 저장은 SeBackupPrivilege(관리자)가 필요해 CI/비관리자 환경에서 못 씁니다.
/// 이 작성기는 순수 메모리에서 실제 하이브 바이트를 만들어 그 제약 없이 부팅 검사 로직을
/// 실제 하이브로 검증할 수 있게 합니다. 셀은 8바이트 정렬로 순차 배치합니다.
/// </remarks>
internal sealed class HiveBuilder
{
    private const int BaseBlock = 0x1000;
    private const uint InvalidOffset = 0xFFFFFFFF;
    private const uint InlineFlag = 0x80000000;

    private byte[] _buf = new byte[0x10000];
    private int _pos = BaseBlock + 0x20; // hbin 헤더(0x20) 다음부터 셀 배치
    private int _max = BaseBlock + 0x20;

    /// <summary>인라인 REG_DWORD 값(vk)을 만들고 셀 오프셋을 반환합니다.</summary>
    public int Dword(string name, uint value)
    {
        int cell = AllocCell(0x14 + name.Length);
        int vk = DataAt(cell);
        Ascii("vk", vk);
        U16(vk + 0x02, (ushort)name.Length);
        U32(vk + 0x04, 4u | InlineFlag);
        U32(vk + 0x08, value);
        U32(vk + 0x0C, 4);   // REG_DWORD
        U16(vk + 0x10, 1);   // 이름 ASCII
        Ascii(name, vk + 0x14);
        return CellOffset(cell);
    }

    /// <summary>비인라인 REG_SZ 값(vk)과 데이터 셀을 만들고 vk 셀 오프셋을 반환합니다.</summary>
    public int Sz(string name, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
        int dataCell = AllocCell(bytes.Length);
        bytes.CopyTo(_buf, DataAt(dataCell));

        int cell = AllocCell(0x14 + name.Length);
        int vk = DataAt(cell);
        Ascii("vk", vk);
        U16(vk + 0x02, (ushort)name.Length);
        U32(vk + 0x04, (uint)bytes.Length); // 비인라인
        U32(vk + 0x08, (uint)CellOffset(dataCell));
        U32(vk + 0x0C, 1);   // REG_SZ
        U16(vk + 0x10, 1);
        Ascii(name, vk + 0x14);
        return CellOffset(cell);
    }

    /// <summary>비인라인 REG_BINARY 값(vk)과 데이터 셀을 만들고 vk 셀 오프셋을 반환합니다.</summary>
    public int Binary(string name, byte[] value)
    {
        int dataCell = AllocCell(value.Length);
        value.CopyTo(_buf, DataAt(dataCell));

        int cell = AllocCell(0x14 + name.Length);
        int vk = DataAt(cell);
        Ascii("vk", vk);
        U16(vk + 0x02, (ushort)name.Length);
        U32(vk + 0x04, (uint)value.Length); // 비인라인
        U32(vk + 0x08, (uint)CellOffset(dataCell));
        U32(vk + 0x0C, 3);   // REG_BINARY
        U16(vk + 0x10, 1);
        Ascii(name, vk + 0x14);
        return CellOffset(cell);
    }

    /// <summary>키(nk)를 만듭니다. 값 vk 오프셋과 자식 nk 오프셋은 먼저 만들어 전달합니다.</summary>
    public int AddKey(string name, int[] valueOffsets, int[] childOffsets)
    {
        uint valueListOff = InvalidOffset;
        if (valueOffsets.Length > 0)
        {
            int cell = AllocCell(valueOffsets.Length * 4);
            int list = DataAt(cell);
            for (int i = 0; i < valueOffsets.Length; i++)
                U32(list + i * 4, (uint)valueOffsets[i]);
            valueListOff = (uint)CellOffset(cell);
        }

        uint subkeyListOff = InvalidOffset;
        if (childOffsets.Length > 0)
        {
            int cell = AllocCell(4 + childOffsets.Length * 4); // "li"(2) + count(2) + 오프셋들
            int list = DataAt(cell);
            Ascii("li", list);
            U16(list + 0x02, (ushort)childOffsets.Length);
            for (int i = 0; i < childOffsets.Length; i++)
                U32(list + 4 + i * 4, (uint)childOffsets[i]);
            subkeyListOff = (uint)CellOffset(cell);
        }

        int nkCell = AllocCell(0x4C + name.Length);
        int nk = DataAt(nkCell);
        Ascii("nk", nk);
        U16(nk + 0x02, 0x20); // 이름 ASCII
        U32(nk + 0x14, (uint)childOffsets.Length);
        U32(nk + 0x1C, subkeyListOff);
        U32(nk + 0x24, (uint)valueOffsets.Length);
        U32(nk + 0x28, valueListOff);
        U16(nk + 0x48, (ushort)name.Length);
        if (name.Length > 0) Ascii(name, nk + 0x4C);
        return CellOffset(nkCell);
    }

    /// <summary>베이스 블록과 hbin 헤더를 채우고 완성된 하이브 바이트를 반환합니다.</summary>
    public byte[] Finish(int rootCellOffset)
    {
        Ascii("regf", 0);
        U32(0x04, 1);
        U32(0x08, 1);                    // seq1 == seq2
        U32(0x24, (uint)rootCellOffset); // 루트 셀 오프셋
        int hbinSize = (_max - BaseBlock + 0xFFF) & ~0xFFF; // 4KB 배수로 올림

        Ascii("hbin", BaseBlock);
        U32(BaseBlock + 0x04, 0);
        U32(BaseBlock + 0x08, (uint)hbinSize);
        U32(0x28, (uint)hbinSize);

        var result = new byte[BaseBlock + hbinSize];
        Array.Copy(_buf, result, result.Length);
        return result;
    }

    private int AllocCell(int dataSize)
    {
        int cellStart = _pos;
        int total = (4 + dataSize + 7) & ~7; // 셀 = 크기 헤더(4) + 데이터, 8바이트 정렬
        EnsureCapacity(cellStart + total);
        U32(cellStart, unchecked((uint)(-total))); // 음수 크기 = 사용 중
        _pos = cellStart + total;
        _max = _pos;
        return cellStart;
    }

    private void EnsureCapacity(int need)
    {
        if (need <= _buf.Length) return;
        int n = _buf.Length;
        while (n < need) n *= 2;
        Array.Resize(ref _buf, n);
    }

    // 셀의 파일 오프셋 → 저장용 셀 오프셋(0x1000 기준). CellData가 0x1000+offset+4로 되돌립니다.
    private static int CellOffset(int fileStart) => fileStart - BaseBlock;
    private static int DataAt(int fileStart) => fileStart + 4;

    private void Ascii(string s, int off) => Encoding.ASCII.GetBytes(s).CopyTo(_buf, off);
    private void U32(int off, uint v) => BitConverter.GetBytes(v).CopyTo(_buf, off);
    private void U16(int off, ushort v) => BitConverter.GetBytes(v).CopyTo(_buf, off);
}
