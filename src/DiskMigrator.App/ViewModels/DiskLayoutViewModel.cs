using System.Text;
using System.Windows.Media;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;

namespace DiskMigrator.App.ViewModels;

/// <summary>디스크 배치 막대의 한 조각 — 파티션 하나이거나 그 사이의 미할당 공간.</summary>
public sealed class DiskSegmentViewModel
{
    /// <summary>막대에서 이 조각이 차지할 비율(0~1). 모든 조각의 합은 1입니다.</summary>
    public required double Fraction { get; init; }

    public required string Title { get; init; }
    public required string SizeText { get; init; }
    public required Brush Fill { get; init; }
    public required string Tooltip { get; init; }

    /// <summary>볼륨 사용량을 알 수 있는지(마운트된 파일시스템만). 미할당·MSR 등은 false.</summary>
    public bool HasUsage { get; init; }

    /// <summary>조각 안에서 사용 중인 비율(0~1). <see cref="HasUsage"/>가 true일 때만 의미 있습니다.</summary>
    public double UsedFraction { get; init; }

    public string UsageText { get; init; } = "";
}

/// <summary>
/// 디스크 하나의 파티션 배치를 막대 그래프로 그리기 위한 표현.
/// </summary>
/// <remarks>
/// 구간을 나누는 계산은 <see cref="DiskLayoutMap"/>(Core, 단위 테스트 있음)이 하고, 여기서는
/// 색·글자·툴팁 같은 표현만 입힙니다.
/// </remarks>
public sealed class DiskLayoutViewModel
{
    public required IReadOnlyList<DiskSegmentViewModel> Segments { get; init; }

    /// <summary>"파티션이 차지한 끝 616 MB · 디스크 2.00 GB" 같은 요약.</summary>
    public required string SummaryText { get; init; }

    /// <summary>마지막 파티션 뒤에 남은 빈 공간 안내(맞춤 클론에서 버려지는 부분). 없으면 null.</summary>
    public string? TrailingFreeText { get; init; }

    /// <summary>디스크가 없으면 null을 돌려줍니다(화면에서 숨김).</summary>
    public static DiskLayoutViewModel? For(DiskInfo? disk)
    {
        if (disk is null || disk.SizeBytes <= 0) return null;

        var spans = DiskLayoutMap.Build(disk);
        if (spans.Count == 0) return null;

        long trailing = DiskLayoutMap.TrailingFreeBytes(disk);

        string summary = disk.Partitions.Count == 0
            ? $"파티션 없음 · 디스크 {SizeFormatter.Format(disk.SizeBytes)}"
            : $"파티션이 차지한 끝 {SizeFormatter.Format(DiskLayoutMap.OccupiedEnd(disk))} · " +
              $"디스크 {SizeFormatter.Format(disk.SizeBytes)}";

        return new DiskLayoutViewModel
        {
            Segments = spans.Select(ToSegment).ToList(),
            SummaryText = summary,
            TrailingFreeText = trailing >= DiskLayoutMap.GapNoiseThreshold
                ? $"뒤쪽 빈 공간 {SizeFormatter.Format(trailing)} — 더 작은 디스크로 옮길 때 이만큼은 복제되지 않습니다."
                : null,
        };
    }

    private static DiskSegmentViewModel ToSegment(DiskLayoutSpan span)
    {
        if (span.Kind == DiskSpanKind.Unallocated || span.Partition is null)
        {
            return new DiskSegmentViewModel
            {
                Fraction = span.DisplayFraction,
                Title = "미할당",
                SizeText = SizeFormatter.Format(span.LengthBytes),
                Fill = UnallocatedBrush,
                Tooltip = $"미할당 공간 {SizeFormatter.Format(span.LengthBytes)}\n" +
                          "어떤 파티션에도 속하지 않아 복제 대상이 아닙니다.",
            };
        }

        var p = span.Partition;
        string role = DescribeRole(p);
        string letter = p.DriveLetter is { } dl ? $"{dl}: " : "";
        string label = string.IsNullOrWhiteSpace(p.VolumeLabel) ? "" : $"{p.VolumeLabel} ";
        string title = $"{letter}{label}".Trim();
        if (title.Length == 0) title = role;

        bool hasUsage = p.FreeSpaceBytes is >= 0 && p.LengthBytes > 0;
        long used = hasUsage ? Math.Max(0, p.LengthBytes - p.FreeSpaceBytes!.Value) : 0;

        var tip = new StringBuilder();
        tip.Append($"파티션 {p.Number}");
        if (p.DriveLetter is { } d) tip.Append($" ({d}:)");
        tip.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.VolumeLabel)) tip.AppendLine($"레이블: {p.VolumeLabel}");
        tip.AppendLine($"종류: {role}");
        tip.AppendLine($"크기: {SizeFormatter.Format(p.LengthBytes)}  (디스크의 {span.TrueFraction:P1})");
        tip.AppendLine($"위치: {SizeFormatter.Format(span.StartOffset)} 부터");
        tip.Append(hasUsage
            ? $"사용: {SizeFormatter.Format(used)} / 여유: {SizeFormatter.Format(p.FreeSpaceBytes!.Value)}"
            : "사용량: 알 수 없음 (마운트된 볼륨이 아님)");

        return new DiskSegmentViewModel
        {
            Fraction = span.DisplayFraction,
            Title = title,
            SizeText = SizeFormatter.Format(p.LengthBytes),
            Fill = BrushFor(p),
            Tooltip = tip.ToString(),
            HasUsage = hasUsage,
            UsedFraction = hasUsage ? Math.Clamp((double)used / p.LengthBytes, 0, 1) : 0,
            UsageText = hasUsage
                ? $"사용 {SizeFormatter.Format(used)} / 여유 {SizeFormatter.Format(p.FreeSpaceBytes!.Value)}"
                : "",
        };
    }

    // 잘 알려진 GPT 타입 GUID. Windows 프로젝트에도 같은 표가 있지만 internal이라 여기서 다시 둡니다.
    private static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid WindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    private static string DescribeRole(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EFI 시스템";
        if (p.GptPartitionType == MicrosoftReserved) return "MSR (예약)";
        if (p.GptPartitionType == WindowsRecovery) return "복구";
        return p.FileSystem ?? "RAW";
    }

    // 역할별 색 — 채도를 낮춰 화면과 톤을 맞추되, 색상(hue)은 서로 벌려 둡니다.
    // 막대에서 조각을 구분하는 것이 이 색의 유일한 일이라 예쁨보다 구별이 우선입니다.
    private static readonly Brush UnallocatedBrush = Frozen("#E4DED5");   // 밝은 모래빛
    private static readonly Brush EfiBrush = Frozen("#C3944F");           // 황토
    private static readonly Brush ReservedBrush = Frozen("#A9A29A");      // 따뜻한 회색
    private static readonly Brush DataBrush = Frozen("#6E8FAD");          // 청색
    private static readonly Brush OtherBrush = Frozen("#74A08F");         // 청록

    private static Brush BrushFor(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return EfiBrush;
        if (p.GptPartitionType == MicrosoftReserved) return ReservedBrush;
        if (p.DriveLetter is not null) return DataBrush;
        return OtherBrush;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
