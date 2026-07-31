using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

internal static class GrayImageCodec
{
    public static GrayImage? FromScreenshot(AdbScreenshotResult screenshot)
    {
        ArgumentNullException.ThrowIfNull(screenshot);

        if (screenshot.DecodedRaw is { } raw)
            return FromRawRgba(raw.Width, raw.Height, raw.RgbaBytes);

        return FromEncodedImage(screenshot.Data);
    }

    public static GrayImage? FromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return FromEncodedImage(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (COMException)
        {



            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    public static void SaveScreenshot(AdbScreenshotResult screenshot, string path)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (screenshot.DecodedRaw is not { } raw)
        {
            File.WriteAllBytes(path, screenshot.Data);
            return;
        }

        var bgra = new byte[checked(raw.Width * raw.Height * 4)];
        var pixelCount = Math.Min(raw.Width * raw.Height, raw.RgbaBytes.Length / 4);
        for (var index = 0; index < pixelCount; index++)
        {
            var source = index * 4;
            var target = source;
            bgra[target] = raw.RgbaBytes[source + 2];
            bgra[target + 1] = raw.RgbaBytes[source + 1];
            bgra[target + 2] = raw.RgbaBytes[source];
            bgra[target + 3] = raw.RgbaBytes[source + 3];
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

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static GrayImage FromRawRgba(int width, int height, byte[] rgba)
    {
        var pixels = new byte[checked(width * height)];
        var pixelCount = Math.Min(pixels.Length, rgba.Length / 4);
        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            pixels[index] = ToGray(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
        }

        return new GrayImage(width, height, pixels);
    }

    private static GrayImage? FromEncodedImage(byte[] data)
    {
        if (data.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(
                frame,
                PixelFormats.Gray8,
                null,
                0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var pixels = new byte[checked(width * height)];
            converted.CopyPixels(pixels, width, 0);
            return new GrayImage(width, height, pixels);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    private static byte ToGray(byte red, byte green, byte blue) =>
        (byte)((red * 299 + green * 587 + blue * 114) / 1000);
}
