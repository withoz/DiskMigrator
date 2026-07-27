using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// 중단 후 이어하기의 진행 저널 — "이 원본→이 대상 작업이 어디까지 대상에 기록됐는지"를
/// 파일로 남깁니다.
/// </summary>
/// <remarks>
/// 엔진의 <see cref="ClonePlan.FlushCheckpoint"/>가 플러시 <b>후</b>에만 저널을 갱신하므로,
/// 저널의 지점까지는 전원이 끊겼어도 대상에 실제로 기록돼 있습니다. 작업이 끝나면 저널을
/// 지우고, 같은 지문(원본·대상·계획이 동일)의 작업을 다시 시작하면 그 지점부터 이어합니다.
///
/// <para>지문에는 원본 파일의 크기·수정 시각과 대상 디스크의 식별 정보, 계획 총량이 들어가
/// 어느 하나라도 달라지면(다른 이미지, 다른 디스크, 중간에 이미지가 바뀜) 저널이 무시되고
/// 처음부터 시작합니다 — 이어하기가 틀린 대상에 이어 붙는 일이 없도록.</para>
///
/// <para>저장은 임시 파일 후 교체(원자적)로 하고, 읽기 실패·형식 불일치는 전부 "저널 없음"으로
/// 취급합니다 — 이어하기는 어디까지나 보너스이고, 의심스러우면 처음부터가 항상 안전합니다.</para>
/// </remarks>
public static class ResumeJournal
{
    private sealed record Data(string Fingerprint, long CompletedBytes, long TotalBytes, DateTime UpdatedUtc);

    /// <summary>여러 식별 조각으로 작업 지문을 만듭니다(SHA-256).</summary>
    public static string MakeFingerprint(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("", parts.Select(p => p?.ToString() ?? "")))));

    /// <summary>
    /// 저널이 있고 지문·총량이 일치하면 완료 바이트를 돌려줍니다. 아니면 0(처음부터).
    /// </summary>
    public static long TryLoad(string directory, string fingerprint, long totalBytes)
    {
        try
        {
            string path = PathFor(directory, fingerprint);
            if (!File.Exists(path)) return 0;

            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
            if (data is null || data.Fingerprint != fingerprint || data.TotalBytes != totalBytes) return 0;
            return Math.Clamp(data.CompletedBytes, 0, totalBytes);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>체크포인트를 기록합니다(임시 파일 후 교체). 실패해도 던지지 않습니다.</summary>
    public static void Save(string directory, string fingerprint, long completedBytes, long totalBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, fingerprint);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new Data(fingerprint, completedBytes, totalBytes, DateTime.UtcNow)));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 저널을 못 남기면 이어하기 기회를 잃을 뿐, 작업 자체에는 영향이 없어야 합니다.
        }
    }

    /// <summary>작업이 정상 완료되면 저널을 지웁니다. 실패해도 던지지 않습니다.</summary>
    public static void Delete(string directory, string fingerprint)
    {
        try
        {
            string path = PathFor(directory, fingerprint);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string PathFor(string directory, string fingerprint) =>
        Path.Combine(directory, fingerprint[..32] + ".resume.json");
}
