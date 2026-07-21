using System.IO;

namespace DiskMigrator.App;

/// <summary>
/// EULA 동의 여부를 사용자별로 저장·확인합니다.
/// </summary>
/// <remarks>
/// <para>
/// 동의 표시는 <c>%LocalAppData%\DiskMigrator\eula-accepted-v{버전}.txt</c> 파일로 남깁니다.
/// 파일명에 버전을 넣으므로, <see cref="Version"/>을 올리면 예전 동의 파일은 무시되고
/// 사용자에게 다시 동의를 받습니다 — EULA 내용이 실질적으로 바뀌면 재동의가 필요합니다.
/// </para>
/// <para>
/// 레지스트리가 아니라 파일을 쓰는 이유: 앱이 관리자 권한으로 실행되지만 동의는 "사람"에게
/// 받는 것이라 사용자 프로필(LocalAppData)에 두는 편이 자연스럽고, 로그와 같은 폴더라
/// 문제 신고 시 함께 확인됩니다.
/// </para>
/// </remarks>
public static class EulaAcceptance
{
    /// <summary>현재 EULA 버전. Resources\EULA.txt 및 docs\EULA.md와 일치해야 합니다.</summary>
    public const string Version = "1.0";

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMigrator",
        $"eula-accepted-v{Version}.txt");

    /// <summary>이 사용자가 현재 버전의 EULA에 이미 동의했는지.</summary>
    public static bool IsAccepted() => File.Exists(MarkerPath);

    /// <summary>동의를 기록합니다. 동의 시각을 파일 내용으로 남깁니다.</summary>
    public static void RecordAcceptance()
    {
        string dir = Path.GetDirectoryName(MarkerPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            MarkerPath,
            $"DiskMigrator EULA v{Version}\r\naccepted: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n");
    }

    /// <summary>내장된 EULA 본문을 읽어옵니다.</summary>
    public static string LoadText()
    {
        var asm = typeof(EulaAcceptance).Assembly;
        // 리소스 이름은 "<루트네임스페이스>.Resources.EULA.txt" 형태입니다.
        string resourceName = $"{typeof(EulaAcceptance).Namespace}.Resources.EULA.txt";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "사용권 계약 본문을 불러오지 못했습니다.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
