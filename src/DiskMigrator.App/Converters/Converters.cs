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
