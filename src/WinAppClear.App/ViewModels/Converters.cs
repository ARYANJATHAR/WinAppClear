using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinAppClear.App.ViewModels;

/// <summary>Small converters referenced with x:Static from XAML.</summary>
public static class Converters
{
    public static IMultiValueConverter AllTrueToVisibility { get; } = new AllTrueConverter();

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
