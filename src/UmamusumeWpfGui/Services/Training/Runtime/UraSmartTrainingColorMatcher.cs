using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Original color samples and exhaustive pixel search with exact early rejection.
/// Used only by smart training.</summary>
internal static class UraSmartTrainingColorMatcher
{
    private readonly struct Sample(int offset, byte r, byte g, byte b, double weight)
    {
        // Fields avoid millions of non-inlined record-property calls in the user's Debug build.
        public readonly int Offset = offset;
        public readonly byte R = r;
        public readonly byte G = g;
        public readonly byte B = b;
        public readonly double Weight = weight;
    }

    public static TemplateMatchResult Find(GrayImage screen, GrayImage template, int[]? roi,
        double threshold, int referenceWidth, int referenceHeight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (screen.RgbaPixels is not { } pixels || template.RgbaPixels is not { } colors)
            return TemplateMatcher.FindColor(screen, template, roi, threshold, referenceWidth, referenceHeight);
        int Scale(int value, int actual, int reference) =>
            (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));
        var x0 = roi is { Length: >= 4 } ? Math.Clamp(Scale(roi[0], screen.Width, referenceWidth), 0, screen.Width - 1) : 0;
        var y0 = roi is { Length: >= 4 } ? Math.Clamp(Scale(roi[1], screen.Height, referenceHeight), 0, screen.Height - 1) : 0;
        var width = roi is { Length: >= 4 } ? Math.Min(Math.Max(1, Scale(roi[2], screen.Width, referenceWidth)), screen.Width - x0) : screen.Width;
        var height = roi is { Length: >= 4 } ? Math.Min(Math.Max(1, Scale(roi[3], screen.Height, referenceHeight)), screen.Height - y0) : screen.Height;
        var maxX = x0 + width - template.Width;
        var maxY = y0 + height - template.Height;
        if (maxX < x0 || maxY < y0)
            return new(false, 0, 0, 0, template.Width, template.Height);
        var samples = new List<Sample>();
        double total = 0;
        var sampleWidth = Math.Min(32, template.Width);
        var sampleHeight = Math.Min(24, template.Height);
        for (var y = 0; y < sampleHeight; y++)
            for (var x = 0; x < sampleWidth; x++)
            {
                var tx = x * template.Width / sampleWidth;
                var ty = y * template.Height / sampleHeight;
                var offset = (ty * template.Width + tx) * 4;
                if (colors[offset + 3] < 24)
                    continue;
                var weight = colors[offset + 3] / 255d;
                samples.Add(new((ty * screen.Width + tx) * 4,
                    colors[offset], colors[offset + 1], colors[offset + 2], weight));
                total += 765d * weight;
            }
        if (total <= 0)
            return new(0 >= Math.Clamp(threshold, 0, 1), 0, x0, y0, template.Width, template.Height);
        var prepared = samples.ToArray();
        var meanR = prepared.Average(sample => (double)sample.R);
        var meanG = prepared.Average(sample => (double)sample.G);
        var meanB = prepared.Average(sample => (double)sample.B);
        // Check opaque, distinctive colors first to reject unrelated positions sooner.
        // Final scores retain the original sample accumulation order and tie behavior.
        var rejectionOrder = prepared.OrderByDescending(sample => sample.Weight
            * (Math.Abs(sample.R - meanR) + Math.Abs(sample.G - meanG)
                + Math.Abs(sample.B - meanB))).ToArray();
        var stride = screen.Width * 4;
        var tolerance = total * 1e-12;
        double Error(int x, int y, double limit)
        {
            var origin = y * stride + x * 4;
            double error = 0;
            for (var index = 0; index < rejectionOrder.Length; index++)
            {
                ref readonly var sample = ref rejectionOrder[index];
                var at = origin + sample.Offset;
                error += (Math.Abs(pixels[at] - sample.R)
                    + Math.Abs(pixels[at + 1] - sample.G)
                    + Math.Abs(pixels[at + 2] - sample.B)) * sample.Weight;
                // Remaining terms cannot reduce error. Retain possible rounded ties.
                if (error > limit + tolerance)
                    return double.PositiveInfinity;
            }
            error = 0;
            for (var index = 0; index < prepared.Length; index++)
            {
                ref readonly var sample = ref prepared[index];
                var at = origin + sample.Offset;
                error += (Math.Abs(pixels[at] - sample.R)
                    + Math.Abs(pixels[at + 1] - sample.G)
                    + Math.Abs(pixels[at + 2] - sample.B)) * sample.Weight;
            }
            return error;
        }
        // Establish a bound, then still search every original candidate location.
        var bound = double.PositiveInfinity;
        var step = template.Width <= 240 ? 1 : 2;
        for (var y = y0; y <= maxY; y += 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = x0; x <= maxX; x += 8)
                bound = Math.Min(bound, Error(x, y, bound));
        }
        var bestScore = double.MinValue;
        var bestX = x0;
        var bestY = y0;
        for (var y = y0; y <= maxY; y += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = x0; x <= maxX; x += step)
            {
                var error = Error(x, y, bound);
                if (!double.IsFinite(error))
                    continue;
                bound = Math.Min(bound, error);
                var score = Math.Clamp(1d - error / total, -1d, 1d);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }
        return new(bestScore >= Math.Clamp(threshold, 0, 1), Math.Max(0, bestScore),
            bestX, bestY, template.Width, template.Height);
    }
}
