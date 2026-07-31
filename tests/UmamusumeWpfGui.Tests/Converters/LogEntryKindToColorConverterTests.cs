using System.Globalization;
using System.Windows.Media;
using UmamusumeWpfGui.Converters;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Converters;

public sealed class LogEntryKindToColorConverterTests
{
    private static readonly LogEntryKindToColorConverter Converter = new();
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;





    [Fact]
    public void InfoKind_ReturnsInfoFallback()
    {
        var result = Converter.Convert(LogEntryKind.Info, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x4D, 0x73, 0x9B), brush.Color);
    }

    [Fact]
    public void SuccessKind_ReturnsSuccessFallback()
    {
        var result = Converter.Convert(LogEntryKind.Success, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x31, 0x7B, 0x62), brush.Color);
    }

    [Fact]
    public void FailureKind_ReturnsErrorFallback()
    {
        var result = Converter.Convert(LogEntryKind.Failure, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0xB8, 0x4B, 0x58), brush.Color);
    }





    [Fact]
    public void NullValue_ReturnsInfoFallback()
    {
        var result = Converter.Convert(null, typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x4D, 0x73, 0x9B), brush.Color);
    }

    [Fact]
    public void NonLogEntryKindValue_ReturnsInfoFallback()
    {
        var result = Converter.Convert("not a LogEntryKind", typeof(Brush), null, Invariant);
        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.FromRgb(0x4D, 0x73, 0x9B), brush.Color);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack(new SolidColorBrush(Colors.Red), typeof(LogEntryKind), null, Invariant));
    }










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
