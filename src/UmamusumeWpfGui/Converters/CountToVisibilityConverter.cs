using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UmamusumeWpfGui.Converters;

/// <summary>
/// Converts an integer count to <see cref="Visibility"/>.
/// <c>0</c> → <see cref="Visibility.Visible"/>, any other value → <see cref="Visibility.Collapsed"/>.
/// Used to show an empty-state message when a collection is empty.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
