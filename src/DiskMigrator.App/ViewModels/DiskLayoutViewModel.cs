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

    /// <summary>범례 오른쪽 칸에 들어갈 크기·사용량 한 덩어리.</summary>
    /// <remarks>
    /// 크기와 사용량을 따로 두면 조각마다 조각 수가 달라(사용량이 없는 MSR·미할당)
    /// 칸 경계가 어긋납니다. 한 칸으로 합치고 가운뎃점으로 나눕니다.
    /// </remarks>
    public string DetailText => HasUsage ? $"{SizeText} · {UsageText}" : SizeText;

    // --- 막대 안 라벨 ------------------------------------------------------
    //
    // 조각 위에 드라이브 문자를 직접 적습니다. 범례를 눈으로 따라가지 않아도
    // 어느 조각이 C: 인지 바로 알 수 있습니다.

    /// <summary>막대 안에 적을 짧은 이름 — "C:" 또는 "EFI"·"MSR"·"복구"·"미할당".</summary>
    public required string BarLabel { get; init; }

    /// <summary>글자가 들어갈 만큼 조각이 넓은지. 좁으면 글자가 잘려 지저분해집니다.</summary>
    public required bool ShowBarLabel { get; init; }

    /// <summary>조각 색이 어두우면 흰 글자, 밝으면(미할당) 어두운 글자.</summary>
    public required Brush BarLabelBrush { get; init; }
}

/// <summary>막대가 무엇을 그리는지.</summary>
public enum DiskRole
{
    Source,

    /// <summary>대상의 현재 내용 — 지워질 것.</summary>
    Target,

    /// <summary>대상의 복제 후 예상 배치 — 만들어질 것.</summary>
    TargetAfter,
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

    // --- 막대 머리글 -------------------------------------------------------
    //
    // 열 제목("원본 (읽기)")은 목록 맨 위에 있어서, 막대와 범례를 들여다보는 동안
    // 시야에서 사라집니다. 이 도구에서 가장 위험한 실수가 원본과 대상을 뒤바꾸는 것이므로
    // 막대 옆에서 한 번 더 말해 줍니다.

    /// <summary>"원본" 또는 "대상".</summary>
    public required string RoleText { get; init; }

    /// <summary>역할 칩 배경색 — 원본은 강조색, 대상은 위험색.</summary>
    public required Brush RoleBrush { get; init; }

    public required string DiskName { get; init; }
    public required string DiskSizeText { get; init; }

    /// <summary>이 디스크에 무슨 일이 일어나는지 — "읽기만 합니다" / "이 디스크가 지워집니다".</summary>
    public required string ActionText { get; init; }

    /// <summary>대상일 때만 위험색으로 강조합니다.</summary>
    public required Brush ActionBrush { get; init; }

    /// <summary>막대 테두리 — 역할을 옅은 색으로 한 번 더 되풀이합니다.</summary>
    public required Brush BarBorderBrush { get; init; }

    /// <summary>"파티션이 차지한 끝 616 MB · 디스크 2.00 GB" 같은 요약.</summary>
    public required string SummaryText { get; init; }

    /// <summary>마지막 파티션 뒤에 남은 빈 공간 안내(맞춤 클론에서 버려지는 부분). 없으면 null.</summary>
    public string? TrailingFreeText { get; init; }

    /// <summary>디스크가 없으면 null을 돌려줍니다(화면에서 숨김).</summary>
    public static DiskLayoutViewModel? For(DiskInfo? disk, DiskRole role)
    {
        if (disk is null || disk.SizeBytes <= 0) return null;

        var spans = DiskLayoutMap.Build(disk);
        if (spans.Count == 0) return null;

        long trailing = DiskLayoutMap.TrailingFreeBytes(disk);

        string summary = disk.Partitions.Count == 0
            ? $"파티션 없음 · 디스크 {SizeFormatter.Format(disk.SizeBytes)}"
            : $"파티션이 차지한 끝 {SizeFormatter.Format(DiskLayoutMap.OccupiedEnd(disk))} · " +
              $"디스크 {SizeFormatter.Format(disk.SizeBytes)}";

        // 세 막대는 같은 모양으로 그리고 라벨로만 시점을 가릅니다. 한쪽만 다르게 그리면
        // 다른 의미의 그래프로 착각하게 됩니다.
        (string roleText, Brush roleBrush, string actionText, Brush actionBrush, Brush lineBrush) = role switch
        {
            DiskRole.Target => ("대상", DangerBrush, "이 디스크가 지워집니다", DangerBrush, DangerLineBrush),
            DiskRole.TargetAfter => ("변경 후", AccentBrush, "복제가 끝나면 이렇게 됩니다", MutedBrush, AccentLineBrush),
            _ => ("원본", AccentBrush, "읽기만 합니다", MutedBrush, AccentLineBrush),
        };

        return new DiskLayoutViewModel
        {
            RoleText = roleText,
            RoleBrush = roleBrush,
            DiskName = disk.Model,
            DiskSizeText = SizeFormatter.Format(disk.SizeBytes),
            ActionText = actionText,
            ActionBrush = actionBrush,
            BarBorderBrush = lineBrush,
            Segments = spans.Select(ToSegment).ToList(),
            SummaryText = summary,

            // 이 경고는 원본 막대에서만 뜻이 통합니다 — "이 디스크를 더 작은 곳으로 옮기면
            // 뒤쪽은 안 따라간다". 복제 후 예상 막대에서는 사용자가 방금 '그대로 둡니다'를
            // 골라 의도적으로 남긴 공간이라, 같은 문구를 쓰면 앞뒤가 맞지 않습니다.
            TrailingFreeText = role == DiskRole.Source && trailing >= DiskLayoutMap.GapNoiseThreshold
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
                BarLabel = "미할당",
                ShowBarLabel = span.DisplayFraction >= LabelMinFraction,
                BarLabelBrush = DarkLabelBrush,   // 미할당은 밝은 색이라 어두운 글자
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
            // 여유는 적지 않습니다 — 크기에서 사용을 뺀 값이라 새 정보가 없는데 줄만 길어져
            // 두 칸 격자에서 잘렸습니다. 막대의 흰 띠가 이미 비율을 보여 주고,
            // 정확한 값은 툴팁에 있습니다.
            UsageText = hasUsage ? $"사용 {SizeFormatter.Format(used)}" : "",

            // 드라이브 문자가 있으면 그것이 가장 알아보기 쉽습니다. 없으면 역할을 짧게.
            BarLabel = p.DriveLetter is { } dl2 ? $"{dl2}:" : ShortRole(p),
            ShowBarLabel = span.DisplayFraction >= LabelMinFraction,
            BarLabelBrush = LightLabelBrush,
        };
    }

    /// <summary>막대 안에 넣을 짧은 역할 이름 — 좁은 조각에 긴 글자는 안 들어갑니다.</summary>
    private static string ShortRole(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EFI";
        if (p.GptPartitionType == MicrosoftReserved) return "MSR";
        if (p.IsWindowsRecovery) return "복구";
        return p.FileSystem ?? "RAW";
    }

    // 잘 알려진 GPT 타입 GUID. Windows 프로젝트에도 같은 표가 있지만 internal이라 여기서 다시 둡니다.
    // 복구 파티션 판별은 MBR(0x27)도 함께 봐야 해서 PartitionInfo.IsWindowsRecovery로 옮겼습니다.
    private static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    private static string DescribeRole(PartitionInfo p)
    {
        if (p.IsEfiSystemPartition) return "EFI 시스템";
        if (p.GptPartitionType == MicrosoftReserved) return "MSR (예약)";
        if (p.IsWindowsRecovery) return "복구";
        return p.FileSystem ?? "RAW";
    }

    /// <summary>
    /// 이 비율보다 좁은 조각에는 글자를 넣지 않습니다.
    /// </summary>
    /// <remarks>
    /// 좁은 조각에 글자를 넣으면 잘려서 오히려 지저분해집니다. 최소 표시 비율이 2%이므로
    /// 그보다 넉넉한 값을 씁니다 — "미할당"(3글자)이 들어갈 만큼.
    /// </remarks>
    private const double LabelMinFraction = 0.07;

    // 막대 안 글자색 — 색 조각 위에는 흰 글자, 밝은 미할당 위에는 어두운 글자.
    private static readonly Brush LightLabelBrush = Frozen("#FFFFFF");
    private static readonly Brush DarkLabelBrush = Frozen("#6B6259");

    // 머리글용 색 — App.xaml의 팔레트와 같은 값입니다.
    private static readonly Brush AccentBrush = Frozen("#52738F");
    private static readonly Brush DangerBrush = Frozen("#DC2626");
    private static readonly Brush MutedBrush = Frozen("#837B72");

    // 막대 테두리는 역할을 옅게 되풀이합니다 — 칩은 위쪽 한 곳뿐이라 막대만 보고 있으면
    // 어느 쪽인지 놓칩니다. 채도를 낮춰 배지의 경고를 잡아먹지 않게 합니다.
    private static readonly Brush AccentLineBrush = Frozen("#C3CEDA");
    private static readonly Brush DangerLineBrush = Frozen("#FECACA");

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
