using Avalonia.Media.Imaging;
using SkiaSharp;
using System.Runtime.InteropServices;
using Umamusume.AppCore.Services;

namespace Umamusume.Avalonia.Services;

/// <summary>Cross-platform image decode/encoding helpers for the developer image gallery.</summary>
public static class DeveloperToolsImageCodec
{
    public static Bitmap LoadBitmap(string path, int? maxDimension = null)
    {
        using var decoded = SKBitmap.Decode(path)
            ?? throw new InvalidDataException($"Could not decode image: {path}");
        using var resized = Resize(decoded, maxDimension);
        using var image = SKImage.FromBitmap(resized ?? decoded);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Skia could not encode the image preview.");
        using var stream = encoded.AsStream();
        return new Bitmap(stream);
    }

    public static CapturedScreenshot LoadAsScreenshot(string path)
    {
        using var bitmap = SKBitmap.Decode(path)
            ?? throw new InvalidDataException($"Could not decode image: {path}");
        return ToScreenshot(bitmap, File.GetLastWriteTime(path));
    }

    public static CapturedScreenshot CropScreenshot(CapturedScreenshot screenshot, int x, int y, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(screenshot.PngBytes)
            ?? throw new InvalidDataException("Could not decode the current screenshot.");
        var bounds = new SKRectI(x, y, x + width, y + height);
        using var subset = new SKBitmap(width, height, bitmap.ColorType, bitmap.AlphaType);
        if (!bitmap.ExtractSubset(subset, bounds))
            throw new InvalidDataException("The selected crop is outside the image bounds.");
        return ToScreenshot(subset, screenshot.CapturedAt);
    }

    public static byte[] EncodeScreenshotCrop(CapturedScreenshot screenshot, int x, int y, int width, int height, string extension)
    {
        var cropped = CropScreenshot(screenshot, x, y, width, height);
        using var bitmap = SKBitmap.Decode(cropped.PngBytes)
            ?? throw new InvalidDataException("Could not decode the cropped screenshot.");
        if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            using var pixmap = bitmap.PeekPixels()
                ?? throw new InvalidDataException("The crop has no pixel data.");
            using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
                ?? throw new InvalidDataException("Skia could not encode the WebP crop.");
            return data.ToArray();
        }

        using var image = SKImage.FromBitmap(bitmap);
        var format = extension.ToLowerInvariant() is ".jpg" or ".jpeg"
            ? SKEncodedImageFormat.Jpeg
            : SKEncodedImageFormat.Png;
        using var encoded = image.Encode(format, 100)
            ?? throw new InvalidDataException("Skia could not encode the PNG crop.");
        return encoded.ToArray();
    }

    private static SKBitmap? Resize(SKBitmap source, int? maxDimension)
    {
        if (maxDimension is not > 0 || (source.Width <= maxDimension && source.Height <= maxDimension))
            return null;
        float scale = (float)maxDimension.Value / Math.Max(source.Width, source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), SKFilterQuality.High)
            ?? throw new InvalidDataException("Skia could not resize the image preview.");
    }

    private static CapturedScreenshot ToScreenshot(SKBitmap bitmap, DateTime capturedAt)
    {
        using var normalized = bitmap.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("Could not normalize image pixels.");
        byte[] rgba = new byte[checked(normalized.Width * normalized.Height * 4)];
        IntPtr pixels = normalized.GetPixels();
        if (pixels != IntPtr.Zero)
        {
            for (int y = 0; y < normalized.Height; y++)
                Marshal.Copy(IntPtr.Add(pixels, y * normalized.RowBytes), rgba, y * normalized.Width * 4, normalized.Width * 4);
        }
        using var image = SKImage.FromBitmap(normalized);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Skia could not encode the image as PNG.");
        return new CapturedScreenshot(png.ToArray(), rgba, normalized.Width, normalized.Height, capturedAt);
    }
}
