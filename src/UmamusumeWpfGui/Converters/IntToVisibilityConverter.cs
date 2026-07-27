using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UmamusumeWpfGui.Converters;

/// <summary>
/// Converts an integer value to <see cref="Visibility"/>.
/// Used to show/hide content panels based on <see cref="ViewModels.SettingsViewModel.SelectedMenuIndex"/>.
///
/// Use with a <c>ConverterParameter</c> equal to the target index.
/// <c>Visible</c> when the value matches the parameter; <c>Collapsed</c> otherwise.
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int intValue = value is int i ? i : 0;
        int target = parameter is string s && int.TryParse(s, out int p) ? p : 0;
        return intValue == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
