using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services.Tasks;

internal static class GrayImageCodec
{
    public static void SaveFrame(GrayImage frame, string path)
    {
        var rgba = frame.RgbaPixels;
        if (rgba is null)
        {
            rgba = new byte[checked(frame.Width * frame.Height * 4)];
            for (var pixel = 0; pixel < frame.Pixels.Length; pixel++)
            {
                rgba[pixel * 4] = frame.Pixels[pixel];
                rgba[pixel * 4 + 1] = frame.Pixels[pixel];
                rgba[pixel * 4 + 2] = frame.Pixels[pixel];
                rgba[pixel * 4 + 3] = 255;
            }
        }
        SaveScreenshot(new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero,
            new AdbRawScreenshot(frame.Width, frame.Height, rgba)), path);
    }

    public static GrayImage? FromScreenshot(AdbScreenshotResult screenshot)
    {
        ArgumentNullException.ThrowIfNull(screenshot);

        if (screenshot.DecodedRaw is { } raw)
        {
            var image = FromRawRgba(raw.Width, raw.Height, raw.RgbaBytes);
            return image is null || screenshot.SourceFrame is null
                ? image
                : image with { SourceFrame = screenshot.SourceFrame };
        }

        var decoded = FromEncodedImage(screenshot.Data);
        return decoded is null || screenshot.SourceFrame is null
            ? decoded
            : decoded with { SourceFrame = screenshot.SourceFrame };
    }

    public static GrayImage? FromDeviceFrame(Umamusume.CoreBridge.DeviceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var image = FromEncodedImage(frame.PngBytes.ToArray());
        return image is null
            ? null
            : image with { SourceFrame = new FrameProvenance(frame.Metadata) };
    }

    public static GrayImage? FromFile(string path) =>
        File.Exists(path) ? LoadTemplate(path) : null;

    internal static GrayImage? LoadTemplate(string path,
        TemplatePreparation preparation = TemplatePreparation.Original) =>
        FromRecognitionImage(TemplateMatcher.LoadTemplateImage(path, preparation));

    internal static GrayImage Resize(GrayImage image, int width, int height, bool meanGray = false)
    {
        var resized = FromRecognitionImage(RecognitionImageDecoder.Transform(
            new RecognitionImage(image.Width, image.Height, image.Pixels.Length == 0 ? null : image.Pixels,
                image.RgbaPixels), new RecognitionImageTransform(TargetWidth: width, TargetHeight: height,
                    MeanGray: meanGray)))
            ?? throw new InvalidDataException("Native image resizing produced no pixels.");
        return resized with
        {
            SourceFrame = image.SourceFrame?.Resize(image.Width, image.Height, width, height),
        };
    }

    internal static GrayImage ResizeNearest(GrayImage image, int width, int height)
    {
        var resized = FromRecognitionImage(RecognitionImageDecoder.Transform(
            new RecognitionImage(image.Width, image.Height, image.Pixels.Length == 0 ? null : image.Pixels,
                image.RgbaPixels), new RecognitionImageTransform(TargetWidth: width, TargetHeight: height, Nearest: true)))
            ?? throw new InvalidDataException("Native nearest-neighbour resizing produced no pixels.");
        return resized with { SourceFrame = image.SourceFrame?.Resize(image.Width, image.Height, width, height) };
    }

    internal static GrayImage CropAndResize(GrayImage image, Int32Rect bounds, int width, int height)
    {
        var resized = FromRecognitionImage(RecognitionImageDecoder.Transform(
            new RecognitionImage(image.Width, image.Height, image.Pixels.Length == 0 ? null : image.Pixels,
                image.RgbaPixels), new RecognitionImageTransform(bounds.X, bounds.Y, bounds.Width,
                    bounds.Height, width, height)))
            ?? throw new InvalidDataException("Native image crop/resize produced no pixels.");
        return resized with
        {
            SourceFrame = image.SourceFrame?.Crop(bounds.X, bounds.Y)
                .Resize(bounds.Width, bounds.Height, width, height),
        };
    }

    internal static GrayImage Binarize(GrayImage image, RecognitionColorRule rule)
    {
        var binary = FromRecognitionImage(RecognitionColorAnalyzer.Binarize(
            new RecognitionImage(image.Width, image.Height, image.Pixels.Length == 0 ? null : image.Pixels,
                image.RgbaPixels), rule))
            ?? throw new InvalidDataException("Native color binarization produced no pixels.");
        return binary with { SourceFrame = image.SourceFrame };
    }

    public static GrayImage? Crop(GrayImage image, Int32Rect region)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Width <= 0
            || image.Height <= 0
            || image.Pixels.Length < checked(image.Width * image.Height))
        {
            return null;
        }

        var x = Math.Clamp(region.X, 0, image.Width);
        var y = Math.Clamp(region.Y, 0, image.Height);
        var right = Math.Clamp(
            (long)region.X + region.Width,
            0,
            image.Width);
        var bottom = Math.Clamp(
            (long)region.Y + region.Height,
            0,
            image.Height);
        var width = (int)Math.Max(0, right - x);
        var height = (int)Math.Max(0, bottom - y);
        if (width == 0 || height == 0)
        {
            return null;
        }

        var count = checked(image.Width * image.Height);
        var gray = image.Pixels.Length == count ? image.Pixels : image.Pixels[..count];
        var rgba = image.RgbaPixels is { } sourceRgba && sourceRgba.Length >= checked(count * 4)
            ? sourceRgba.Length == count * 4 ? sourceRgba : sourceRgba[..(count * 4)] : null;
        var cropped = FromRecognitionImage(RecognitionImageDecoder.Transform(
            new RecognitionImage(image.Width, image.Height, gray, rgba),
            new RecognitionImageTransform(x, y, width, height)))
            ?? throw new InvalidDataException("Native image cropping produced no pixels.");
        return cropped with { SourceFrame = image.SourceFrame?.Crop(x, y) };
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

    private static GrayImage? FromRawRgba(int width, int height, byte[] rgba) =>
        width > 0 && height > 0 && (long)width * height * 4 == rgba.Length
            ? FromRecognitionImage(RecognitionImageDecoder.FromRgba(width, height, rgba))
            : null;

    private static GrayImage? FromEncodedImage(byte[] data) =>
        FromRecognitionImage(RecognitionImageDecoder.DecodeBytes(data));

    private static GrayImage? FromRecognitionImage(RecognitionImage? image) =>
        image?.GrayPixels is { } gray
            ? new GrayImage(image.Width, image.Height, gray, image.RgbaPixels) { Preparation = image.Preparation }
            : null;

}
