namespace DiskMigrator.Core.Util;

/// <summary>
/// 증분 백업 파일 이름 사슬을 해석합니다: <c>base.vhdx → base-01.vhdx → base-02.vhdx …</c>
/// </summary>
/// <remarks>
/// 증분 백업은 차등(differencing) VHDX 자식이므로 <b>새 파일</b>이 필요하고, 자식은 부모 파일
/// 경로에 묶입니다(이동·이름 변경 시 사슬이 끊어짐). 사용자가 사슬의 어느 파일을 고르든
/// 같은 답이 나오도록 — 기본 이름을 복원한 뒤 같은 폴더에서 가장 높은 번호를 부모로,
/// 그 다음 번호를 새 자식으로 정합니다. 번호는 두 자리(<c>-01</c>~<c>-99</c>)입니다.
/// </remarks>
public static class BackupChain
{
    /// <summary>사슬 해석 결과.</summary>
    /// <param name="ParentPath">새 증분의 부모가 될 기존 파일(사슬의 최신 구성원).</param>
    /// <param name="ChildPath">만들 새 증분 파일 경로(아직 없음).</param>
    /// <param name="ChainLength">기존 사슬 구성원 수(기본 이미지 포함).</param>
    public sealed record Resolution(string ParentPath, string ChildPath, int ChainLength);

    /// <summary>
    /// 사용자가 고른 기존 백업 파일에서 증분 사슬을 해석합니다.
    /// 반환이 null이면 사슬을 만들 수 없는 상태입니다(기본 이미지가 없거나 번호 소진).
    /// </summary>
    public static Resolution? Resolve(string pickedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pickedPath);

        string dir = Path.GetDirectoryName(pickedPath) ?? "";
        string ext = Path.GetExtension(pickedPath);
        string stem = Path.GetFileNameWithoutExtension(pickedPath);

        // 사용자가 자식(base-NN)을 골랐어도 기본 이름으로 되돌립니다.
        string baseStem = StripIndex(stem);
        string basePath = Path.Combine(dir, baseStem + ext);
        if (!File.Exists(basePath)) return null; // 사슬의 뿌리가 없으면 증분 불가.

        int highest = 0;
        for (int i = 1; i <= 99; i++)
        {
            if (File.Exists(Path.Combine(dir, $"{baseStem}-{i:D2}{ext}"))) highest = i;
        }
        if (highest >= 99) return null;

        string parent = highest == 0 ? basePath : Path.Combine(dir, $"{baseStem}-{highest:D2}{ext}");
        string child = Path.Combine(dir, $"{baseStem}-{highest + 1:D2}{ext}");
        return new(parent, child, highest + 1);
    }

    /// <summary>파일 이름 끝의 <c>-NN</c>(두 자리 증분 번호)을 제거합니다.</summary>
    private static string StripIndex(string stem)
    {
        if (stem.Length > 3 && stem[^3] == '-' &&
            char.IsAsciiDigit(stem[^2]) && char.IsAsciiDigit(stem[^1]))
        {
            return stem[..^3];
        }
        return stem;
    }
}
