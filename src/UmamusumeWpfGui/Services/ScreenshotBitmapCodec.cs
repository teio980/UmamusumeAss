using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

internal static class ScreenshotBitmapCodec
{
    public static BitmapSource? ToBitmapSource(AdbScreenshotResult screenshot)
    {
        ArgumentNullException.ThrowIfNull(screenshot);

        if (screenshot.DecodedRaw is { } raw)
        {
            return FromRawRgba(raw);
        }

        if (screenshot.Data.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(screenshot.Data, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var bitmap = new FormatConvertedBitmap(
                frame,
                PixelFormats.Bgra32,
                null,
                0);
            bitmap.Freeze();
            return bitmap;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    public static void SavePng(BitmapSource source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static BitmapSource FromRawRgba(AdbRawScreenshot raw)
    {
        var bgra = new byte[checked(raw.Width * raw.Height * 4)];
        var pixelCount = Math.Min(raw.Width * raw.Height, raw.RgbaBytes.Length / 4);
        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            bgra[offset] = raw.RgbaBytes[offset + 2];
            bgra[offset + 1] = raw.RgbaBytes[offset + 1];
            bgra[offset + 2] = raw.RgbaBytes[offset];
            bgra[offset + 3] = raw.RgbaBytes[offset + 3];
        }

        var bitmap = BitmapSource.Create(
            raw.Width,
            raw.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            raw.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
