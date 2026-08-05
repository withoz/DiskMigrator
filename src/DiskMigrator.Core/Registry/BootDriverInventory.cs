namespace DiskMigrator.Core.Registry;

/// <summary>부팅 시작(Start=0) 드라이버 하나.</summary>
/// <param name="ServiceName">서비스 키 이름.</param>
/// <param name="Group">로드 그룹(예: "SCSI Miniport"). 없으면 빈 문자열.</param>
/// <param name="ImagePath">레지스트리에 적힌 경로. 비어 있으면 관례상 System32\drivers\&lt;이름&gt;.sys입니다.</param>
/// <param name="ResolvedPath">실제로 찾아본 전체 경로.</param>
/// <param name="FileExists">그 파일이 실제로 있는지.</param>
/// <param name="FileSizeBytes">파일 크기(없으면 null).</param>
/// <param name="IsOutsideSystem32">표준 위치(System32) 밖인지 — 서드파티 의심 신호.</param>
public sealed record BootDriverEntry(
    string ServiceName,
    string Group,
    string ImagePath,
    string ResolvedPath,
    bool FileExists,
    long? FileSizeBytes,
    bool IsOutsideSystem32);

/// <summary>부팅 시작 드라이버 목록과 그 요약.</summary>
/// <param name="ControlSet">읽은 컨트롤 세트 이름.</param>
/// <param name="Drivers">Start=0인 커널·파일시스템 드라이버 전부.</param>
/// <param name="MissingFiles">파일이 없는 드라이버 — <b>하나라도 있으면 부팅이 멈출 수 있습니다</b>.</param>
/// <param name="OutsideSystem32">표준 위치 밖의 드라이버(서드파티 의심).</param>
public sealed record BootDriverInventoryResult(
    string ControlSet,
    IReadOnlyList<BootDriverEntry> Drivers,
    IReadOnlyList<BootDriverEntry> MissingFiles,
    IReadOnlyList<BootDriverEntry> OutsideSystem32);

/// <summary>
/// 부트로더가 커널보다 먼저 메모리에 올리는 <b>부팅 시작 드라이버</b>를 전수 조사합니다.
/// </summary>
/// <remarks>
/// 부팅 구성이 모두 정상인데도 사본이 로고에서 멈추는 일이 있습니다. 원인 중 하나가
/// <b>Start=0으로 등록됐는데 파일이 없는 드라이버</b>입니다 — winload가 그 파일을 찾다 멈춥니다.
/// 레지스트리만 보는 검사는 이것을 놓칩니다(등록은 멀쩡하니까).
///
/// <para>2026-08-04 부팅 조사에서 손으로 확인했던 항목을 제품 코드로 옮긴 것입니다.
/// 그때는 드라이버 89개 전부의 파일 존재를 일일이 확인해 "누락 0"을 확인했고,
/// 그 사실이 원인 후보를 좁히는 데 결정적이었습니다.</para>
/// </remarks>
public static class BootDriverInventory
{
    /// <summary>
    /// Windows 볼륨 루트를 받아 부팅 시작 드라이버를 조사합니다.
    /// </summary>
    /// <param name="windowsRoot">예: <c>"C:\"</c> 또는 <c>"\\?\Volume{...}\"</c>.</param>
    /// <param name="controlSet">읽을 컨트롤 세트. 기본은 ControlSet001.</param>
    public static BootDriverInventoryResult Inspect(string windowsRoot, string controlSet = "ControlSet001")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsRoot);

        string root = windowsRoot.EndsWith('\\') ? windowsRoot : windowsRoot + "\\";
        string hivePath = Path.Combine(root, "Windows", "System32", "config", "SYSTEM");

        var hive = RegistryHive.Load(hivePath);
        string services = controlSet + "\\Services";
        if (!hive.KeyExists(services))
            return new(controlSet, [], [], []);

        var found = new List<BootDriverEntry>();

        foreach (string name in hive.EnumerateSubKeyNames(services))
        {
            string key = services + "\\" + name;

            if (hive.GetDword(key, "Start") != 0) continue;

            // Type 1=커널 드라이버, 2=파일시스템 드라이버. 서비스(16·32)는 Start=0을 쓰지 않습니다.
            uint? type = hive.GetDword(key, "Type");
            if (type is not (1 or 2)) continue;

            string image = hive.GetString(key, "ImagePath") ?? "";
            string relative = NormalizeImagePath(image, name);
            string full = relative.Length > 1 && relative[1] == ':'
                ? relative                                   // 절대 경로가 박힌 경우
                : Path.Combine(root, "Windows", relative);

            bool exists = false;
            long? size = null;
            try
            {
                var fi = new FileInfo(full);
                exists = fi.Exists;
                if (exists) size = fi.Length;
            }
            catch
            {
                // 경로가 이상해 열 수 없는 경우 — "없음"으로 두되 예외로 조사를 멈추지 않습니다.
            }

            bool outside = !relative.StartsWith("System32", StringComparison.OrdinalIgnoreCase);

            found.Add(new BootDriverEntry(
                ServiceName: name,
                Group: hive.GetString(key, "Group") ?? "",
                ImagePath: image,
                ResolvedPath: full,
                FileExists: exists,
                FileSizeBytes: size,
                IsOutsideSystem32: outside));
        }

        var ordered = found.OrderBy(d => d.ServiceName, StringComparer.OrdinalIgnoreCase).ToList();

        return new BootDriverInventoryResult(
            ControlSet: controlSet,
            Drivers: ordered,
            MissingFiles: ordered.Where(d => !d.FileExists).ToList(),
            OutsideSystem32: ordered.Where(d => d.IsOutsideSystem32).ToList());
    }

    /// <summary>
    /// ImagePath를 Windows 폴더 기준 상대 경로로 정리합니다.
    /// </summary>
    /// <remarks>
    /// 값이 비어 있는 드라이버가 많은데, 그 경우 관례상 <c>System32\drivers\&lt;이름&gt;.sys</c>입니다.
    /// 이걸 모르고 "경로 없음"으로 처리하면 멀쩡한 드라이버를 누락으로 오판합니다.
    /// </remarks>
    private static string NormalizeImagePath(string image, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(image))
            return Path.Combine("System32", "drivers", serviceName + ".sys");

        return image
            .Replace("\\SystemRoot\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\\??\\", "", StringComparison.OrdinalIgnoreCase)
            .TrimStart('\\');
    }
}
