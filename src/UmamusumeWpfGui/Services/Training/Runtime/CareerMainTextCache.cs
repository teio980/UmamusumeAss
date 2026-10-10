using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Only successful text on exactly unchanged regions can be reused.</summary>
internal sealed class CareerMainTextCache
{
    private readonly Dictionary<string, (GrayImage Image, string Text)> _entries = new();

    public void Clear() => _entries.Clear();
    public void Remember(string field, GrayImage image, string text) => _entries[field] = (image, text);

    public bool TryGet(string field, GrayImage image, out string? text)
    {
        text = null;
        if (!_entries.TryGetValue(field, out var cached) || image.Width != cached.Image.Width
            || image.Height != cached.Image.Height || !image.Pixels.AsSpan().SequenceEqual(cached.Image.Pixels)
            || (image.RgbaPixels is null) != (cached.Image.RgbaPixels is null)
            || (image.RgbaPixels is { } rgba && !rgba.AsSpan().SequenceEqual(cached.Image.RgbaPixels)))
            return false;
        text = cached.Text;
        return true;
    }

    public static GrayImage? Crop(GrayImage frame, int[] bounds, int referenceWidth, int referenceHeight) =>
        GrayImageCodec.Crop(frame, new Int32Rect(
            (int)Math.Round(bounds[0] * frame.Width / (double)Math.Max(1, referenceWidth)),
            (int)Math.Round(bounds[1] * frame.Height / (double)Math.Max(1, referenceHeight)),
            Math.Max(1, (int)Math.Round(bounds[2] * frame.Width / (double)Math.Max(1, referenceWidth))),
            Math.Max(1, (int)Math.Round(bounds[3] * frame.Height / (double)Math.Max(1, referenceHeight)))));
}
