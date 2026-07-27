using System.Globalization;
using System.Windows.Media;
using UmamusumeWpfGui.Converters;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Converters;

public sealed class LogEntryKindToColorConverterTests
{
    private static readonly LogEntryKindToColorConverter Converter = new();
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ================================================================
    // Fallback colors (Application.Current is null in unit tests)
    // ================================================================

    [Fact]
    public void InfoKind_ReturnsGrayFallback()
    {
        var result = Converter.Convert(LogEntryKind.Info, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x88, 0x88, 0x88), brush.Color);
    }

    [Fact]
    public void SuccessKind_ReturnsPinkFallback()
    {
        var result = Converter.Convert(LogEntryKind.Success, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0xE9, 0x1E, 0x8C), brush.Color);
    }

    [Fact]
    public void FailureKind_ReturnsRedFallback()
    {
        var result = Converter.Convert(LogEntryKind.Failure, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0xF4, 0x43, 0x36), brush.Color);
    }

    // ================================================================
    // Edge cases
    // ================================================================

    [Fact]
    public void NullValue_ReturnsInfoFallback()
    {
        var result = Converter.Convert(null, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x88, 0x88, 0x88), brush.Color);
    }

    [Fact]
    public void NonLogEntryKindValue_ReturnsInfoFallback()
    {
        var result = Converter.Convert("not a LogEntryKind", typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x88, 0x88, 0x88), brush.Color);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack(new SolidColorBrush(Colors.Red), typeof(LogEntryKind), null, Invariant));
    }

    // ================================================================
    // All values covered — no default fallthrough surprise
    // ================================================================

    /// <summary>
    /// Ensures every defined <see cref="LogEntryKind"/> member maps to a
    /// non-null brush. If a new kind is added and the switch is not updated,
    /// this test will catch it.
    /// </summary>
    [Fact]
    public void AllLogEntryKinds_MapToABrush()
    {
        foreach (LogEntryKind kind in Enum.GetValues<LogEntryKind>())
        {
            var result = Converter.Convert(kind, typeof(Brush), null, Invariant);
            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.NotNull(brush);
        }
    }
}
