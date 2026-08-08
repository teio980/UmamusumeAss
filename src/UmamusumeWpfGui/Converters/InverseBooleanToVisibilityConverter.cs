using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UmamusumeWpfGui.Converters;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is bool boolean && !boolean
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
