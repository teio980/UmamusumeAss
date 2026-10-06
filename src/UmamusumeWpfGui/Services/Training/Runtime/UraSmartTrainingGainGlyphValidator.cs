using System.Globalization;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Checks isolated gain glyph count before accepting OCR's numeric value.</summary>
internal static class UraSmartTrainingGainGlyphValidator
{
    public static bool Matches(GrayImage image, int value)
    {
        if (value < 0 || image.Width <= 0 || image.Height <= 0
            || image.Pixels.Length != image.Width * image.Height)
            return false;
        var groups = 0;
        var inside = false;
        for (var x = 0; x < image.Width; x++)
        {
            var ink = false;
            for (var y = 0; y < image.Height; y++)
                if (image.Pixels[y * image.Width + x] < 128)
                {
                    ink = true;
                    break;
                }
            if (ink && !inside)
                groups++;
            inside = ink;
        }
        // Prepared gains contain the plus followed by separated decimal glyphs.
        // Ambiguous/touching glyphs remain unknown; never invent or truncate digits.
        return groups == 1 + value.ToString(CultureInfo.InvariantCulture).Length;
    }
}
