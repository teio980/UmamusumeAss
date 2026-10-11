using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

internal static class ScreenshotBitmapCodec
{
    public static BitmapSource? ToBitmapSource(GrayImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0
            || frame.RgbaPixels is not { } rgba
            || rgba.Length < checked(frame.Width * frame.Height * 4))
            return null;

        var bgra = new byte[checked(frame.Width * frame.Height * 4)];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = rgba[offset + 2];
            bgra[offset + 1] = rgba[offset + 1];
            bgra[offset + 2] = rgba[offset];
            bgra[offset + 3] = rgba[offset + 3];
        }

        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
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

}
