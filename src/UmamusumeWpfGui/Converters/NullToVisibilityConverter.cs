using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UmamusumeWpfGui.Converters;




public sealed class NullToVisibilityConverter : IValueConverter
{



    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        bool showWhenNull = Invert;
        return (isNull == showWhenNull) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
