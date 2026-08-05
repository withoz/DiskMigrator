namespace DiskMigrator.Core.Registry;

/// <summary>빠른 시작·최대 절전 상태.</summary>
/// <param name="HiberbootEnabled">
/// 빠른 시작 설정. <b>1이면 종료할 때마다 <c>hiberfil.sys</c>가 다시 만들어집니다.</b>
/// 값이 없으면 null.
/// </param>
/// <param name="HibernateEnabled">최대 절전 설정. 값이 없으면 null.</param>
/// <param name="HiberfilExists">지금 재개 이미지가 있는지.</param>
/// <param name="HiberfilSizeBytes">그 파일 크기(없으면 null).</param>
/// <param name="ResumeWouldBeAttempted">
/// 다음 부팅에서 부팅 관리자가 <c>winload</c> 대신 <c>winresume</c> 경로를 탈 가능성이 있는지.
/// </param>
public sealed record FastStartupStateResult(
    uint? HiberbootEnabled,
    uint? HibernateEnabled,
    bool HiberfilExists,
    long? HiberfilSizeBytes,
    bool ResumeWouldBeAttempted);

/// <summary>
/// 빠른 시작(재개) 상태를 읽습니다 — 다른 하드웨어에서 사본이 멈추는 흔한 원인입니다.
/// </summary>
/// <remarks>
/// Windows 10은 "시스템 종료"를 눌러도 커널 상태를 <c>hiberfil.sys</c>에 저장합니다.
/// 다음 부팅에서 부팅 관리자는 <c>winload.efi</c>가 아니라 <c>winresume.efi</c>를 실행해
/// 그 이미지를 복원하려 하는데, 저장된 상태는 <b>원래 PC의 하드웨어를 전제</b>하므로
/// 다른 PC에서는 복원에 실패하고 로고 화면에서 멈춥니다.
///
/// <para>이것을 확인하지 않으면 "부팅 구성은 다 정상인데 왜 안 뜨지"를 오래 헤매게 됩니다.
/// 진단 플래그(<c>sos</c>)도 <c>winload</c> 경로에만 걸리므로, 재개 경로를 타는 동안에는
/// 아무 텍스트도 나오지 않아 단서마저 사라집니다(2026-08-04 조사에서 실제로 겪음).</para>
/// </remarks>
public static class FastStartupState
{
    /// <param name="windowsRoot">예: <c>"C:\"</c> 또는 <c>"\\?\Volume{...}\"</c>.</param>
    /// <param name="controlSet">읽을 컨트롤 세트. 기본은 ControlSet001.</param>
    public static FastStartupStateResult Inspect(string windowsRoot, string controlSet = "ControlSet001")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsRoot);

        string root = windowsRoot.EndsWith('\\') ? windowsRoot : windowsRoot + "\\";
        string hivePath = Path.Combine(root, "Windows", "System32", "config", "SYSTEM");

        var hive = RegistryHive.Load(hivePath);

        uint? hiberboot = hive.GetDword(controlSet + "\\Control\\Session Manager\\Power", "HiberbootEnabled");
        uint? hibernate = hive.GetDword(controlSet + "\\Control\\Power", "HibernateEnabled");

        bool exists = false;
        long? size = null;
        try
        {
            var fi = new FileInfo(Path.Combine(root, "hiberfil.sys"));
            exists = fi.Exists;
            if (exists) size = fi.Length;
        }
        catch
        {
            // 접근 불가면 "없음"으로 두되 조사를 멈추지 않습니다.
        }

        // 이미지가 실제로 있어야 재개를 시도합니다. 설정만 켜져 있고 파일이 없으면
        // 이번 부팅은 정상 경로로 갑니다 — 다만 종료할 때 다시 만들어집니다.
        return new(hiberboot, hibernate, exists, size, ResumeWouldBeAttempted: exists);
    }
}
