namespace DiskMigrator.Windows.Jobs;

/// <summary>두 리포트 사이의 차이 하나.</summary>
/// <param name="Area">어느 영역인지(예: "BootDrivers", "FastStartup").</param>
/// <param name="What">무엇이 바뀌었는지.</param>
/// <param name="Before">이전 값.</param>
/// <param name="After">이후 값.</param>
public sealed record DiagnosticChange(string Area, string What, string? Before, string? After);

/// <summary>비교 결과.</summary>
/// <param name="SameDisk">두 리포트가 같은 디스크를 본 것인지.</param>
/// <param name="Changes">달라진 항목.</param>
/// <param name="Summary">한 줄 요약 — <b>아무것도 안 바뀌었다는 사실이 중요할 때가 있습니다.</b></param>
public sealed record DiagnosticDiffResult(bool SameDisk, IReadOnlyList<DiagnosticChange> Changes, string Summary);

/// <summary>
/// 조치 전후의 진단 리포트를 비교합니다 — <b>정말로 바뀌었는지</b> 확인하는 용도입니다.
/// </summary>
/// <remarks>
/// 2026-08-04 조사에서 "복구했는데 원래대로"를 여러 번 겪었습니다. 부팅 복구를 돌리고 다시
/// 부팅해도 같은 증상이면, 복구가 실제로 적용됐는지부터 확인해야 합니다.
///
/// <para>그때 전후 스냅샷을 비교해 <b>디스크가 전혀 바뀌지 않았다</b>는 사실을 확인한 것이
/// 결정적이었습니다 — 그 한 가지로 "원인은 디스크 밖에 있다"가 확정됐습니다.
/// 변화가 없다는 것도 훌륭한 정보입니다.</para>
/// </remarks>
public static class DiagnosticDiff
{
    public static DiagnosticDiffResult Compare(DiagnosticReport before, DiagnosticReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // 디스크 식별자로 같은 대상인지 봅니다. 장치 번호는 재연결하면 바뀌므로 믿지 않습니다.
        bool sameDisk =
            string.Equals(before.Disk.Model, after.Disk.Model, StringComparison.OrdinalIgnoreCase) &&
            before.Disk.SizeBytes == after.Disk.SizeBytes &&
            string.Equals(before.Disk.DiskGuid, after.Disk.DiskGuid, StringComparison.OrdinalIgnoreCase);

        var changes = new List<DiagnosticChange>();

        Add(changes, "Partitions", "count",
            before.Partitions.Count.ToString(), after.Partitions.Count.ToString());

        if (before.BootDrivers is { } bd && after.BootDrivers is { } ad)
        {
            Add(changes, "BootDrivers", "total", bd.TotalCount.ToString(), ad.TotalCount.ToString());
            Add(changes, "BootDrivers", "missing files", bd.MissingCount.ToString(), ad.MissingCount.ToString());
            Add(changes, "BootDrivers", "missing names",
                string.Join(",", bd.MissingNames), string.Join(",", ad.MissingNames));
            Add(changes, "BootDrivers", "outside System32",
                string.Join(",", bd.OutsideNames), string.Join(",", ad.OutsideNames));
        }

        if (before.FastStartup is { } bf && after.FastStartup is { } af)
        {
            Add(changes, "FastStartup", "HiberbootEnabled", bf.HiberbootEnabled?.ToString(), af.HiberbootEnabled?.ToString());
            Add(changes, "FastStartup", "hiberfil.sys present", bf.HiberfilExists.ToString(), af.HiberfilExists.ToString());
            Add(changes, "FastStartup", "resume would be attempted",
                bf.ResumeWouldBeAttempted.ToString(), af.ResumeWouldBeAttempted.ToString());
        }

        if (before.Esp is { } be && after.Esp is { } ae)
        {
            Add(changes, "ESP", "boot manager present", be.BootManagerPresent.ToString(), ae.BootManagerPresent.ToString());
            Add(changes, "ESP", "fallback path present", be.FallbackPresent.ToString(), ae.FallbackPresent.ToString());
            Add(changes, "ESP", "BCD present", be.BcdPresent.ToString(), ae.BcdPresent.ToString());
            // 서명이 바뀌는 것은 중대한 사건입니다 — bcdboot 같은 도구가 덮어썼을 수 있습니다.
            Add(changes, "ESP", "boot manager signing authority", be.SignatureAuthority, ae.SignatureAuthority);
            Add(changes, "ESP", "file count", be.TotalFileCount.ToString(), ae.TotalFileCount.ToString());
            Add(changes, "ESP", "foreign boot folders",
                string.Join(",", be.ForeignBootFolders), string.Join(",", ae.ForeignBootFolders));
        }

        if (before.BootTrace is { } bt && after.BootTrace is { } at)
        {
            Add(changes, "BootTrace", "last attempt",
                bt.LastAttemptUtc?.ToString("u"), at.LastAttemptUtc?.ToString("u"));
            Add(changes, "BootTrace", "progress", bt.Progress, at.Progress);
        }

        // 부팅 검사 항목은 코드로 대조합니다 — 문구는 언어에 따라 달라집니다.
        foreach (var b in before.BootCheck.Where(i => i.Code is not null))
        {
            var a = after.BootCheck.FirstOrDefault(i => i.Code == b.Code);
            if (a is null) continue;
            Add(changes, "BootCheck", b.Code!, b.Passed?.ToString() ?? "unknown", a.Passed?.ToString() ?? "unknown");
        }

        string summary = !sameDisk
            ? "These two reports describe DIFFERENT disks — comparing them is unlikely to be meaningful."
            : changes.Count == 0
                ? "Nothing changed between the two reports. If an action was taken in between, it did not " +
                  "alter the disk — the cause is likely outside the disk (firmware, hardware, or the other PC)."
                : $"{changes.Count} difference(s) between the two reports.";

        return new DiagnosticDiffResult(sameDisk, changes, summary);
    }

    private static void Add(List<DiagnosticChange> list, string area, string what, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        list.Add(new DiagnosticChange(area, what, before, after));
    }
}
