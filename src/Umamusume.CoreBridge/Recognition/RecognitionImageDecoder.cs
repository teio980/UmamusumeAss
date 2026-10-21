namespace Umamusume.CoreBridge;

/// <summary>
/// Decodes image files and encoded image bytes with the native WIC decoder.
/// Successful results own managed pixel arrays and have no native lifetime.
/// </summary>
public static class RecognitionImageDecoder
{
    private const int MaxDimension = 10_000;
    private const long MaxPixels = 100_000_000;

    /// <summary>
    /// Decodes the first image frame from a file. Missing, corrupt, or
    /// unsupported files return null.
    /// </summary>
    public static RecognitionImage? DecodeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path)) return null;
        int result = UmaCoreBridgeNative.DecodeImageFile(path, out SafeDecodedImageHandle? image);
        return CopyToManaged(result, image);
    }

    /// <summary>
    /// Decodes the first image frame from encoded bytes. Empty, corrupt, or
    /// unsupported data returns null.
    /// </summary>
    public static RecognitionImage? DecodeBytes(ReadOnlySpan<byte> encodedBytes)
    {
        if (encodedBytes.IsEmpty) return null;
        int result = UmaCoreBridgeNative.DecodeImageBytes(encodedBytes, out SafeDecodedImageHandle? image);
        return CopyToManaged(result, image);
    }

    /// <summary>
    /// Builds a recognition image from tightly packed straight RGBA8 pixels.
    /// </summary>
    public static RecognitionImage FromRgba(int width, int height, ReadOnlySpan<byte> rgbaPixels)
    {
        ValidateDimensions(width, height);
        int expectedLength = checked(width * height * 4);
        if (rgbaPixels.Length != expectedLength)
            throw new ArgumentException("RGBA byte length must match the image dimensions.", nameof(rgbaPixels));

        int result = UmaCoreBridgeNative.CreateImageFromRgba(
            width, height, rgbaPixels, out SafeDecodedImageHandle? image);
        return CopyToManaged(result, image)
            ?? throw new ManagedBridgeException(
                $"UmaCreateImageFromRgba failed with native error code {result}.");
    }

    public static RecognitionImage Transform(RecognitionImage source, RecognitionImageTransform options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        int result = UmaCoreBridgeNative.TransformImage(source, options, out SafeDecodedImageHandle? image);
        return CopyToManaged(result, image)
            ?? throw new ManagedBridgeException($"UmaTransformImage failed with native error code {result}.");
    }

    internal static RecognitionImage? CopyToManaged(int result, SafeDecodedImageHandle? image)
    {
        using (image)
        {
            if (result != (int)ConnectionErrorCode.Success || image is null || image.IsInvalid || image.IsClosed)
                return null;

            int infoResult = UmaCoreBridgeNative.GetDecodedImageInfo(image, out var info);
            if (infoResult != (int)ConnectionErrorCode.Success
                || info.Width <= 0 || info.Height <= 0
                || info.Width > MaxDimension || info.Height > MaxDimension)
                return null;

            long pixelCount = (long)info.Width * info.Height;
            if (pixelCount > MaxPixels
                || info.GrayLength != (ulong)pixelCount
                || (info.RgbaLength != 0 && info.RgbaLength != (ulong)pixelCount * 4)
                || info.GrayLength > int.MaxValue
                || info.RgbaLength > int.MaxValue)
                return null;

            var gray = new byte[(int)info.GrayLength];
            var rgba = new byte[(int)info.RgbaLength];
            int copyResult = UmaCoreBridgeNative.CopyDecodedImage(image, gray, rgba);
            return copyResult == (int)ConnectionErrorCode.Success
                ? new RecognitionImage(info.Width, info.Height, gray, rgba.Length == 0 ? null : rgba,
                    (TemplatePreparation)info.Preparation)
                : null;
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || width > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Image width is outside supported limits.");
        if (height <= 0 || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(height), "Image height is outside supported limits.");
        if ((long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Image pixel count exceeds supported limits.");
    }
}
