using System.IO;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace UmamusumeWpfGui.Services;

internal static class UmaImageCodec
{
    public static BitmapSource Load(string path, int? maxDimension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var image = Image.Load<Rgba32>(path);
        if (maxDimension is > 0
            && (image.Width > maxDimension.Value || image.Height > maxDimension.Value))
        {
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(maxDimension.Value, maxDimension.Value),
                Mode = ResizeMode.Max,
            }));
        }

        var rgba = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(rgba);
        var bgra = ConvertRgbaToBgra(rgba);
        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            WpfPixelFormats.Bgra32,
            null,
            bgra,
            image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Save(BitmapSource source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".webp":
                SaveWebp(source, path);
                break;
            case ".jpg":
            case ".jpeg":
                SaveJpeg(source, path);
                break;
            default:
                SavePng(source, path);
                break;
        }
    }

    private static void SaveWebp(BitmapSource source, string path)
    {
        var rgba = ConvertBgraToRgba(ReadBgra(source));
        using var image = Image.LoadPixelData<Rgba32>(rgba, source.PixelWidth, source.PixelHeight);
        using var output = File.Create(path);
        image.Save(output, new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossless,
        });
    }

    private static void SaveJpeg(BitmapSource source, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadBgra(BitmapSource source)
    {
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] ConvertRgbaToBgra(byte[] rgba)
    {
        var bgra = (byte[])rgba.Clone();
        for (var index = 0; index < bgra.Length; index += 4)
        {
            (bgra[index], bgra[index + 2]) = (bgra[index + 2], bgra[index]);
        }

        return bgra;
    }

    private static byte[] ConvertBgraToRgba(byte[] bgra) =>
        ConvertRgbaToBgra(bgra);
}
