using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskMigrator.App.Localization;
using DiskMigrator.App.ViewModels;
using DiskMigrator.Core.Models;

namespace DiskMigrator.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ConverterParameter로 준 단계일 때만 보이게 합니다.</summary>
public sealed class StageToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AppStage stage || parameter is not string expected) return Visibility.Collapsed;

        return Enum.TryParse<AppStage>(expected, out var target) && stage == target
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>안전 점검 항목의 심각도를 색으로 바꿉니다.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    // 색을 여기 적어 두면 어두운 모드에서 이 자리만 옛 색으로 남습니다 — 팔레트에서 꺼내 씁니다.
    // 확인 필요와 경고는 같은 주황입니다: 둘 다 "멈추진 않지만 읽어야 하는 것"이라 색을 가르면
    // 없는 위계를 만들게 됩니다.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SafetySeverity.Blocker => ThemeBrush.Get("SeverityBlocker"),
            SafetySeverity.RequiresConfirmation => ThemeBrush.Get("SeverityWarn"),
            SafetySeverity.Warning => ThemeBrush.Get("SeverityWarn"),
            _ => ThemeBrush.Get("SeverityInfo"),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SafetySeverity.Blocker => Strings.Get("SevBlocker"),
            SafetySeverity.RequiresConfirmation => Strings.Get("SevRequiresConfirmation"),
            SafetySeverity.Warning => Strings.Get("SevWarning"),
            _ => Strings.Get("SevInfo"),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 비율(0~1)과 컨테이너 실제 폭을 받아 픽셀 폭으로 바꿉니다 — 파티션 배치 막대에 씁니다.
/// </summary>
/// <remarks>
/// WPF에는 "부모 폭의 N%"를 직접 지정하는 방법이 없어(Grid의 star 크기는 정적 선언이 필요),
/// 데이터로 만들어지는 가변 개수의 조각에는 이 방식이 가장 단순합니다.
/// </remarks>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double fraction ||
            values[1] is not double available ||
            double.IsNaN(available) || available <= 0)
        {
            return 0d;
        }

        return Math.Max(0d, fraction * available);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 모든 값이 true일 때만 보입니다.
/// </summary>
/// <remarks>
/// 막대 템플릿은 원본·변경 전·변경 후 세 곳에서 같이 쓰입니다. 조정 손잡이는 "변경 후
/// 막대이면서" "지금 조정 가능한 상태일 때"만 나와야 하는데, 두 조건이 서로 다른 곳
/// (막대 뷰모델 / 화면 뷰모델)에 있어 한 번에 봅니다.
/// </remarks>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
