using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Converters;

/// <summary>
/// Converts <see cref="LogEntryKind"/> to a <see cref="Brush"/> resolved
/// from application-level resource dictionaries by canonical design key:
/// <list type="bullet">
///   <item><description>Info → <c>TextSecondaryBrush</c> (gray, #888888)</description></item>
///   <item><description>Success → <c>PrimaryBrush</c> (pink, #E91E8C)</description></item>
///   <item><description>Failure → <c>ErrorBrush</c> (red, #F44336)</description></item>
/// </list>
/// Falls back to hardcoded colors when <see cref="Application.Current"/>
/// is null or the resource key is not found.
/// </summary>
public sealed class LogEntryKindToColorConverter : IValueConverter
{
    private const string InfoKey = "StatusInfoBrush";
    private const string SuccessKey = "StatusSuccessBrush";
    private const string FailureKey = "StatusErrorBrush";

    private static readonly SolidColorBrush FallbackInfo =
        new(Color.FromRgb(0x4D, 0x73, 0x9B));
    private static readonly SolidColorBrush FallbackSuccess =
        new(Color.FromRgb(0x31, 0x7B, 0x62));
    private static readonly SolidColorBrush FallbackFailure =
        new(Color.FromRgb(0xB8, 0x4B, 0x58));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryKind kind)
        {
            return kind switch
            {
                LogEntryKind.Success => Resolve(SuccessKey) ?? FallbackSuccess,
                LogEntryKind.Failure => Resolve(FailureKey) ?? FallbackFailure,
                _ => Resolve(InfoKey) ?? FallbackInfo,
            };
        }

        return Resolve(InfoKey) ?? FallbackInfo;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Resolves a design brush resource by key from <see cref="Application.Current"/>.
    /// Returns null when the resource or application object is unavailable.
    /// </summary>
    private static Brush? Resolve(string key)
    {
        if (Application.Current is null)
            return null;

        var resource = Application.Current.TryFindResource(key);
        return resource as Brush;
    }
}
