using System.Text;

namespace DiskMigrator.Core.Registry;

/// <summary>
/// Windows 레지스트리 하이브(regf) 파일을 직접 파싱·수정합니다. reg.exe / RegLoadKey에
/// 의존하지 않습니다.
/// </summary>
/// <remarks>
/// 왜 직접 파싱하는가: 실기에서 클론 대상의 SYSTEM 하이브를 다른 PC의 reg.exe로 offline
/// 로드하려 했더니 환경·버전 문제로 실패했습니다. 하드웨어 독립 마이그레이션(어떤 PC에서도
/// 부팅)을 확실히 하려면, OS의 레지스트리 API에 기대지 않고 하이브 바이너리를 우리가 직접
/// 다뤄야 합니다.
///
/// 다행히 우리가 바꿀 값(저장소 드라이버의 Start)은 전부 REG_DWORD(4바이트)이고 하이브에
/// 인라인으로 저장되므로, 하이브의 크기·구조를 전혀 바꾸지 않고 <b>4바이트만 덮어쓰면</b>
/// 됩니다. 셀 할당/재배치가 필요 없어 구현이 견고합니다.
///
/// 참고 포맷:
/// - 베이스 블록(0..0x1000): "regf" 시그니처, 시퀀스 번호 2개, 루트 셀 오프셋, 체크섬.
/// - hbin/셀: 파일 오프셋 0x1000 + 셀오프셋 위치. 셀 = int32 크기(음수=사용중) + 데이터.
/// - nk(키), vk(값), lf/lh/li/ri(하위키 목록).
/// </remarks>
public sealed class RegistryHive
{
    private const int BaseBlockSize = 0x1000;
    private const uint InvalidOffset = 0xFFFFFFFF;
    private const uint InlineDataFlag = 0x80000000;

    private readonly byte[] _data;

    public RegistryHive(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < BaseBlockSize)
            throw new InvalidDataException(DiskMigrator.Core.Localization.L.T("하이브가 너무 작습니다 (베이스 블록 미만).", "The hive is too small (smaller than a base block)."));
        if (Encoding.ASCII.GetString(data, 0, 4) != "regf")
            throw new InvalidDataException(DiskMigrator.Core.Localization.L.T("regf 시그니처가 없습니다 — 레지스트리 하이브가 아닙니다.", "Missing regf signature — this is not a registry hive."));

        _data = data;
    }

    public static RegistryHive Load(string path) => new(File.ReadAllBytes(path));

    public void Save(string path) => File.WriteAllBytes(path, _data);

    public byte[] ToArray() => _data;

    // --- 저수준 읽기 ---------------------------------------------------------

    private uint U32(int off) => BitConverter.ToUInt32(_data, off);
    private ushort U16(int off) => BitConverter.ToUInt16(_data, off);

    /// <summary>셀 오프셋(0x1000 기준)을 셀 데이터의 파일 오프셋으로 (4바이트 크기 헤더 건너뜀).</summary>
    private static int CellData(uint cellOffset) => BaseBlockSize + (int)cellOffset + 4;

    private uint RootCellOffset => U32(0x24);

    // --- 키/값 탐색 ----------------------------------------------------------

    /// <summary>루트 키의 nk 레코드 파일 오프셋.</summary>
    private int RootKey()
    {
        int nk = CellData(RootCellOffset);
        EnsureSig(nk, "nk");
        return nk;
    }

    private void EnsureSig(int off, string sig)
    {
        if (Encoding.ASCII.GetString(_data, off, sig.Length) != sig)
            throw new InvalidDataException(
                $"오프셋 {off}에서 '{sig}' 시그니처를 찾지 못했습니다 (하이브 손상 가능).");
    }

    private string KeyName(int nk)
    {
        ushort flags = U16(nk + 0x02);
        ushort nameLen = U16(nk + 0x48);
        bool ascii = (flags & 0x20) != 0;
        return ascii
            ? Encoding.ASCII.GetString(_data, nk + 0x4C, nameLen)
            : Encoding.Unicode.GetString(_data, nk + 0x4C, nameLen);
    }

    private IEnumerable<int> Subkeys(int nk)
    {
        // 안정(stable) 하위키 목록(nk+0x1C)만 읽습니다. 휘발성 하위키(nk+0x20)는 런타임 전용이라
        // 디스크 하이브에 저장되지 않으므로 오프라인 편집 대상이 아닙니다 — 의도적으로 무시합니다.
        uint listOffset = U32(nk + 0x1C);
        if (listOffset is 0 or InvalidOffset) yield break;

        foreach (int child in WalkSubkeyList(listOffset))
            yield return child;
    }

    private IEnumerable<int> WalkSubkeyList(uint listCellOffset)
    {
        int rec = CellData(listCellOffset);
        string sig = Encoding.ASCII.GetString(_data, rec, 2);
        ushort count = U16(rec + 2);

        switch (sig)
        {
            case "lf":
            case "lh": // 각 항목 8바이트: {하위키 오프셋(4), 이름 힌트(4)}
                for (int i = 0; i < count; i++)
                    yield return CellData(U32(rec + 4 + (i * 8)));
                break;

            case "li": // 각 항목 4바이트: 하위키 오프셋
                for (int i = 0; i < count; i++)
                    yield return CellData(U32(rec + 4 + (i * 4)));
                break;

            case "ri": // 하위키 목록들의 인덱스
                for (int i = 0; i < count; i++)
                    foreach (int child in WalkSubkeyList(U32(rec + 4 + (i * 4))))
                        yield return child;
                break;

            default:
                throw new InvalidDataException(DiskMigrator.Core.Localization.L.T($"알 수 없는 하위키 목록 시그니처: '{sig}'", $"Unknown subkey list signature: '{sig}'"));
        }
    }

    private int FindSubkey(int nk, string name)
    {
        foreach (int child in Subkeys(nk))
            if (KeyName(child).Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        return -1;
    }

    /// <summary>키 경로(예: "ControlSet001\Services\storahci")를 따라가 nk 오프셋을 반환. 없으면 -1.</summary>
    public int FindKey(string path)
    {
        int nk = RootKey();
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            nk = FindSubkey(nk, part);
            if (nk < 0) return -1;
        }
        return nk;
    }

    private IEnumerable<int> Values(int nk)
    {
        uint count = U32(nk + 0x24);
        uint listOffset = U32(nk + 0x28);
        if (count == 0 || listOffset is 0 or InvalidOffset) yield break;

        int listRec = CellData(listOffset);
        for (int i = 0; i < count; i++)
            yield return CellData(U32(listRec + (i * 4)));
    }

    private string ValueName(int vk)
    {
        ushort nameLen = U16(vk + 0x02);
        if (nameLen == 0) return ""; // 기본값
        ushort flags = U16(vk + 0x10);
        bool ascii = (flags & 0x1) != 0;
        return ascii
            ? Encoding.ASCII.GetString(_data, vk + 0x14, nameLen)
            : Encoding.Unicode.GetString(_data, vk + 0x14, nameLen);
    }

    private int FindValue(int nk, string name)
    {
        foreach (int vk in Values(nk))
            if (ValueName(vk).Equals(name, StringComparison.OrdinalIgnoreCase))
                return vk;
        return -1;
    }

    // --- DWORD 읽기/쓰기 -----------------------------------------------------

    /// <summary>키의 DWORD 값을 읽습니다. 키·값이 없거나 DWORD가 아니면 null.</summary>
    public uint? GetDword(string keyPath, string valueName)
    {
        int nk = FindKey(keyPath);
        if (nk < 0) return null;
        int vk = FindValue(nk, valueName);
        if (vk < 0) return null;

        uint dataLen = U32(vk + 0x04);
        bool inline = (dataLen & InlineDataFlag) != 0;
        uint actualLen = dataLen & ~InlineDataFlag;

        if (inline) return U32(vk + 0x08);
        if (actualLen >= 4) return U32(CellData(U32(vk + 0x08)));
        return null;
    }

    /// <summary>
    /// 키의 DWORD 값을 덮어씁니다. 성공하면 true. 값이 4바이트 이하로 저장돼 있어야 합니다
    /// (Start 등 모든 표준 DWORD가 이에 해당).
    /// </summary>
    public bool SetDword(string keyPath, string valueName, uint value)
    {
        int nk = FindKey(keyPath);
        if (nk < 0) return false;
        int vk = FindValue(nk, valueName);
        if (vk < 0) return false;

        uint dataLen = U32(vk + 0x04);
        bool inline = (dataLen & InlineDataFlag) != 0;

        int writeAt = inline ? vk + 0x08 : CellData(U32(vk + 0x08));
        BitConverter.GetBytes(value).CopyTo(_data, writeAt);
        return true;
    }

    /// <summary>키가 존재하는지.</summary>
    public bool KeyExists(string keyPath) => FindKey(keyPath) >= 0;

    // --- 문자열/하위키 읽기 (BCD 등 진단용) ----------------------------------

    /// <summary>
    /// 키의 REG_SZ / REG_EXPAND_SZ 값을 읽습니다. 키·값이 없거나 문자열 타입이 아니면 null.
    /// </summary>
    /// <remarks>
    /// BCD 스토어도 regf 하이브라, OS 로더의 ApplicationPath 같은 값을 이 메서드로 읽어
    /// 부팅 구성을 정적으로 검사합니다.
    /// </remarks>
    public string? GetString(string keyPath, string valueName)
    {
        int nk = FindKey(keyPath);
        if (nk < 0) return null;
        int vk = FindValue(nk, valueName);
        if (vk < 0) return null;

        uint type = U32(vk + 0x0C);
        if (type is not (1 or 2)) return null; // REG_SZ(1) / REG_EXPAND_SZ(2)

        uint dataLen = U32(vk + 0x04);
        bool inline = (dataLen & InlineDataFlag) != 0;
        int actualLen = (int)(dataLen & ~InlineDataFlag);
        if (actualLen <= 0) return "";

        int dataAt = inline ? vk + 0x08 : CellData(U32(vk + 0x08));
        if (dataAt < 0 || dataAt + actualLen > _data.Length) return null;

        string s = Encoding.Unicode.GetString(_data, dataAt, actualLen);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    /// <summary>
    /// 키의 값 데이터를 원시 바이트로 읽습니다(REG_BINARY 등 타입 무관). 없으면 null.
    /// </summary>
    /// <remarks>
    /// BCD의 장치 참조(device/osdevice)는 REG_BINARY로 저장되며 대상 디스크·파티션 GUID를
    /// 내장합니다. 이 바이트에서 디스크 GUID 일치 여부를 확인해 부팅 참조 유효성을 검사합니다.
    /// </remarks>
    public byte[]? GetBinary(string keyPath, string valueName)
    {
        int nk = FindKey(keyPath);
        if (nk < 0) return null;
        int vk = FindValue(nk, valueName);
        if (vk < 0) return null;

        uint dataLen = U32(vk + 0x04);
        bool inline = (dataLen & InlineDataFlag) != 0;
        int actualLen = (int)(dataLen & ~InlineDataFlag);
        if (actualLen <= 0) return [];

        if (inline)
        {
            int n = Math.Min(actualLen, 4);
            return _data.AsSpan(vk + 0x08, n).ToArray();
        }

        int dataAt = CellData(U32(vk + 0x08));
        if (dataAt < 0 || dataAt + actualLen > _data.Length) return null;
        return _data.AsSpan(dataAt, actualLen).ToArray();
    }

    /// <summary>키의 직속 하위키 이름들을 반환합니다. 키가 없으면 빈 목록.</summary>
    public IReadOnlyList<string> EnumerateSubKeyNames(string keyPath)
    {
        int nk = FindKey(keyPath);
        if (nk < 0) return [];

        var names = new List<string>();
        foreach (int child in Subkeys(nk))
            names.Add(KeyName(child));
        return names;
    }

    // --- 저장 전 정리 --------------------------------------------------------

    /// <summary>
    /// 하이브를 "깨끗함"으로 표시하고 베이스 블록 체크섬을 다시 계산합니다.
    /// </summary>
    /// <remarks>
    /// 두 시퀀스 번호를 같게 만들면 Windows가 로드 시 "복구 불필요"로 판단해 로그를 재생하지
    /// 않고 하이브를 그대로 씁니다 → 우리 수정이 유지됩니다. 체크섬이 틀리면 하이브를
    /// 거부하므로 반드시 다시 계산합니다. 이 메서드는 모든 수정을 마친 뒤 마지막에 호출합니다.
    /// </remarks>
    public void MarkCleanAndUpdateChecksum()
    {
        uint seq1 = U32(0x04);
        BitConverter.GetBytes(seq1).CopyTo(_data, 0x08); // seq2 = seq1

        uint checksum = 0;
        for (int i = 0; i < 0x1FC; i += 4)
            checksum ^= U32(i);

        if (checksum == 0) checksum = 1;
        else if (checksum == 0xFFFFFFFF) checksum = 0xFFFFFFFE;

        BitConverter.GetBytes(checksum).CopyTo(_data, 0x1FC);
    }
}
