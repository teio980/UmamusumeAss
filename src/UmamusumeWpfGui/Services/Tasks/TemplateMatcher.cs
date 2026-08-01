using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;







internal static class TemplateMatcher
{
    public static TemplateMatchResult Find(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);

        if (template.Width > screen.Width || template.Height > screen.Height)
            return new TemplateMatchResult(false, 0, 0, 0, template.Width, template.Height);

        var bounds = ScaleRoi(
            roi,
            screen.Width,
            screen.Height,
            referenceWidth,
            referenceHeight);
        var maxX = Math.Min(
            screen.Width - template.Width,
            bounds.X + bounds.Width - template.Width);
        var maxY = Math.Min(
            screen.Height - template.Height,
            bounds.Y + bounds.Height - template.Height);
        if (bounds.X > maxX || bounds.Y > maxY)
            return new TemplateMatchResult(false, 0, 0, 0, template.Width, template.Height);

        // MAA's default MatchTemplate path is Ccoeff (TM_CCOEFF_NORMED).
        // Keep the managed matcher bounded, but use enough samples to preserve
        // button text and borders instead of comparing a whole page snapshot.
        var sampleWidth = Math.Min(32, template.Width);
        var sampleHeight = Math.Min(32, template.Height);
        // Small button crops are cheap enough to scan at pixel precision;
        // larger state markers use a two-pixel stride to keep polling bounded.
        var candidateStep = template.Width <= 160 ? 1 : 2;
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;

        for (var y = bounds.Y; y <= maxY; y += candidateStep)
        {
            for (var x = bounds.X; x <= maxX; x += candidateStep)
            {
                var score = CompareSamples(
                    screen,
                    template,
                    x,
                    y,
                    sampleWidth,
                    sampleHeight);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        var found = bestScore >= Math.Clamp(threshold, 0, 1);
        return new TemplateMatchResult(
            found,
            Math.Max(0, bestScore),
            bestX,
            bestY,
            template.Width,
            template.Height);
    }

    private static double CompareSamples(
        GrayImage screen,
        GrayImage template,
        int screenX,
        int screenY,
        int sampleWidth,
        int sampleHeight)
    {
        var templateValues = new double[sampleWidth * sampleHeight];
        var screenValues = new double[templateValues.Length];
        var samples = 0;
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var templateY = sampleY * template.Height / sampleHeight;
            var screenRow = (screenY + templateY) * screen.Width;
            var templateRow = templateY * template.Width;
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var templateX = sampleX * template.Width / sampleWidth;
                templateValues[samples] = template.Pixels[templateRow + templateX];
                screenValues[samples] = screen.Pixels[screenRow + screenX + templateX];
                samples++;
            }
        }

        if (samples == 0)
            return 0;

        var templateMean = templateValues.Take(samples).Average();
        var screenMean = screenValues.Take(samples).Average();
        var numerator = 0d;
        var templateVariance = 0d;
        var screenVariance = 0d;
        for (var index = 0; index < samples; index++)
        {
            var templateDelta = templateValues[index] - templateMean;
            var screenDelta = screenValues[index] - screenMean;
            numerator += templateDelta * screenDelta;
            templateVariance += templateDelta * templateDelta;
            screenVariance += screenDelta * screenDelta;
        }

        // Solid-color button edges have almost no variance. Fall back to the
        // absolute grayscale comparison for those tiny templates.
        if (templateVariance < 1 || screenVariance < 1)
        {
            var error = 0d;
            for (var index = 0; index < samples; index++)
                error += Math.Abs(screenValues[index] - templateValues[index]);
            return 1d - error / (samples * 255d);
        }

        return Math.Clamp(
            numerator / Math.Sqrt(templateVariance * screenVariance),
            -1d,
            1d);
    }

    private static RoiBounds ScaleRoi(
        int[]? roi,
        int width,
        int height,
        int referenceWidth,
        int referenceHeight)
    {
        if (roi is not { Length: >= 4 })
            return new RoiBounds(0, 0, width, height);

        var x = ScaleCoordinate(roi[0], width, referenceWidth);
        var y = ScaleCoordinate(roi[1], height, referenceHeight);
        var roiWidth = Math.Max(1, ScaleCoordinate(roi[2], width, referenceWidth));
        var roiHeight = Math.Max(1, ScaleCoordinate(roi[3], height, referenceHeight));
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        roiWidth = Math.Min(roiWidth, width - x);
        roiHeight = Math.Min(roiHeight, height - y);
        return new RoiBounds(x, y, roiWidth, roiHeight);
    }

    private static int ScaleCoordinate(int value, int actual, int reference) =>
        (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));

    private readonly record struct RoiBounds(int X, int Y, int Width, int Height);
}
