namespace DiskMigrator.Core.Util;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>바이트 수를 사람이 읽는 크기 문자열로 (1024 기준).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }

    /// <summary>초당 바이트를 속도 문자열로.</summary>
    public static string FormatSpeed(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0
            ? $"{Format((long)bytesPerSecond)}/s"
            : "-";

    /// <summary>남은 시간을 사람이 읽는 문자열로.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero || span == TimeSpan.MaxValue) return "-";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일 {span.Hours}시간";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}시간 {span.Minutes}분";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}분 {span.Seconds}초";
        return $"{(int)span.TotalSeconds}초";
    }
}
