using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>One OCR request for isolated numeric rows, with spatial field assignment.</summary>
internal static class UraSmartTrainingOcrBatch
{
    public static async Task<IReadOnlyDictionary<string, ScreenTextRecognitionResult?>> ReadAsync(
        IVisualPipelineRuntime runtime, KeyValuePair<string, GrayImage>[] fields,
        CancellationToken cancellationToken)
    {
        const int margin = 24;
        var images = fields.Select(field => CareerNumericOcrReader.Upscale(field.Value, 2)).ToArray();
        var rowHeight = images.Max(image => image.Height) + margin * 2;
        var width = images.Max(image => image.Width) + margin * 2;
        var height = rowHeight * fields.Length;
        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, (byte)255);
        for (var row = 0; row < images.Length; row++)
        {
            var image = images[row];
            for (var y = 0; y < image.Height; y++)
                image.Pixels.AsSpan(y * image.Width, image.Width).CopyTo(
                    pixels.AsSpan((row * rowHeight + margin + y) * width + margin, image.Width));
        }
        var rgba = new byte[checked(pixels.Length * 4)];
        for (var pixel = 0; pixel < pixels.Length; pixel++)
        {
            rgba[pixel * 4] = rgba[pixel * 4 + 1] = rgba[pixel * 4 + 2] = pixels[pixel];
            rgba[pixel * 4 + 3] = 255;
        }
        ScreenTextRecognitionResult? result = null;
        try
        {
            result = await runtime.DetectTextAsync(new GrayImage(width, height, pixels, rgba),
                [0, 0, width, height], width, height, "en-US", "training_selection.smart_batch",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            // Reuse the existing per-field numeric fallback when batch OCR is unavailable.
        }
        var mapped = new Dictionary<string, ScreenTextRecognitionResult?>();
        for (var row = 0; row < fields.Length; row++)
        {
            // A detection spanning two rows is ambiguous and cannot be assigned by order.
            var top = row * rowHeight;
            var bottom = top + rowHeight;
            mapped[fields[row].Key] = result is null ? null : result with
            {
                Detections = result.Detections.Where(item => item.Bounds.Height > 0
                    && item.Bounds.Y >= top && item.Bounds.Bottom <= bottom).ToArray(),
            };
        }
        return mapped;
    }
}
