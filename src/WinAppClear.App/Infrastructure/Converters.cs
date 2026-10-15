using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinAppClear.Core.Health;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.App.Infrastructure;

public sealed class SizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => PathUtil.FormatSize(l),
        int i => PathUtil.FormatSize(i),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime d when parameter as string == "time" => d.ToString("g", CultureInfo.CurrentCulture),
        DateTime d => d.ToString("d", CultureInfo.CurrentCulture),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"3 days ago", "2 months ago", or "Unknown".</summary>
public sealed class RelativeDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime d) return parameter as string ?? "";
        var span = DateTime.Now - d;
        if (span.TotalDays < 1) return "Today";
        if (span.TotalDays < 2) return "Yesterday";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} month(s) ago";
        return $"{(int)(span.TotalDays / 365)} year(s) ago";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            int i => i > 0,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Maps Confidence / HealthSeverity / named states to (soft) brushes for pills.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            Confidence.High => "Accent",
            Confidence.Medium => "Warning",
            Confidence.Low => "Muted",
            HealthSeverity.Danger => "Danger",
            HealthSeverity.Warning => "Warning",
            HealthSeverity.Info => "Info",
            string s => s,
            _ => "Muted",
        };
        var key = (tone, Soft) switch
        {
            ("Accent", false) => "AccentDarkBrush",
            ("Accent", true) => "AccentSoftBrush",
            ("Warning", false) => "WarningBrush",
            ("Warning", true) => "WarningSoftBrush",
            ("Danger", false) => "DangerBrush",
            ("Danger", true) => "DangerSoftBrush",
            ("Info", false) => "InfoBrush",
            ("Info", true) => "InfoSoftBrush",
            (_, false) => "TextSecondaryBrush",
            _ => "HoverBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
