using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
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
    private static readonly SolidColorBrush Blocker = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Confirm = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Info = new(Color.FromRgb(0x6B, 0x72, 0x80));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SafetySeverity.Blocker => Blocker,
            SafetySeverity.RequiresConfirmation => Confirm,
            SafetySeverity.Warning => Warn,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SafetySeverity.Blocker => "차단",
            SafetySeverity.RequiresConfirmation => "확인 필요",
            SafetySeverity.Warning => "경고",
            _ => "참고",
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
