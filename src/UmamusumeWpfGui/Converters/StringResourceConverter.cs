using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UmamusumeWpfGui.Converters;

/// <summary>
/// Converts a resource key string to its DynamicResource value from
/// application-level resource dictionaries.
/// Used to bind navigation menu item labels that come from resource keys.
/// </summary>
public sealed class StringResourceConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for XAML binding via
    /// <c>Converter="{x:Static c:StringResourceConverter.Instance}"</c>.
    /// </summary>
    public static readonly StringResourceConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is not null)
        {
            var resource = Application.Current.TryFindResource(key);
            if (resource is not null)
                return resource;
        }

        return value ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
