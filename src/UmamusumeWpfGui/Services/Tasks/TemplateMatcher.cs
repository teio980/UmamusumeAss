using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;







internal static class TemplateMatcher
{
    public static TemplateMatchResult FindScaled(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        IReadOnlyList<double> scaleCandidates,
        int candidateStep = 4,
        int sampleWidth = 16,
        int sampleHeight = 16)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scaleCandidates);

        if (scaleCandidates.Count == 0)
        {
            return Find(
                screen,
                template,
                roi,
                threshold,
                referenceWidth,
                referenceHeight);
        }

        var bounds = ScaleRoi(
            roi,
            screen.Width,
            screen.Height,
            referenceWidth,
            referenceHeight);
        var referenceScale = Math.Min(
            screen.Width / (double)Math.Max(1, referenceWidth),
            screen.Height / (double)Math.Max(1, referenceHeight));
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;
        var bestWidth = template.Width;
        var bestHeight = template.Height;

        foreach (var scale in scaleCandidates)
        {
            if (!double.IsFinite(scale) || scale <= 0)
                continue;

            var targetWidth = Math.Max(
                1,
                (int)Math.Round(template.Width * referenceScale * scale));
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(template.Height * referenceScale * scale));
            if (targetWidth > screen.Width || targetHeight > screen.Height)
                continue;

            var match = FindAtSize(
                screen,
                template,
                bounds,
                targetWidth,
                targetHeight,
                candidateStep,
                sampleWidth,
                sampleHeight);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestX = match.X;
                bestY = match.Y;
                bestWidth = targetWidth;
                bestHeight = targetHeight;
            }
        }

        if (bestScore == double.MinValue)
            return new TemplateMatchResult(false, 0, 0, 0, template.Width, template.Height);

        return new TemplateMatchResult(
            bestScore >= Math.Clamp(threshold, 0, 1),
            Math.Max(0, bestScore),
            bestX,
            bestY,
            bestWidth,
            bestHeight);
    }

    public static TemplateMatchResult FindScaledMasked(
        GrayImage screen,
        string transparentTemplatePath,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        IReadOnlyList<double> scaleCandidates)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(transparentTemplatePath);
        ArgumentNullException.ThrowIfNull(scaleCandidates);

        var template = LoadMaskedHeadTemplate(transparentTemplatePath);
        if (template is null)
        {
            return new TemplateMatchResult(
                false,
                0,
                0,
                0,
                0,
                0);
        }

        var referenceScale = Math.Min(
            screen.Width / (double)Math.Max(1, referenceWidth),
            screen.Height / (double)Math.Max(1, referenceHeight));
        var bounds = ScaleRoi(
            roi,
            screen.Width,
            screen.Height,
            referenceWidth,
            referenceHeight);
        var bestScore = double.MinValue;
        var bestX = 0;
        var bestY = 0;
        var bestWidth = template.Width;
        var bestHeight = template.Height;
        foreach (var scale in scaleCandidates)
        {
            if (!double.IsFinite(scale) || scale <= 0)
                continue;

            var targetWidth = Math.Max(
                1,
                (int)Math.Round(template.Width * referenceScale * scale));
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(template.Height * referenceScale * scale));
            if (targetWidth > screen.Width || targetHeight > screen.Height)
                continue;

            var match = FindMaskedAtSize(
                screen,
                template,
                bounds,
                targetWidth,
                targetHeight);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestX = match.X;
                bestY = match.Y;
                bestWidth = targetWidth;
                bestHeight = targetHeight;
            }
        }

        if (bestScore == double.MinValue)
        {
            return new TemplateMatchResult(
                false,
                0,
                0,
                0,
                template.Width,
                template.Height);
        }

        return new TemplateMatchResult(
            bestScore >= Math.Clamp(threshold, 0, 1),
            Math.Max(0, bestScore),
            bestX,
            bestY,
            bestWidth,
            bestHeight);
    }

    private static TemplateMatchResult FindMaskedAtSize(
        GrayImage screen,
        MaskedTemplate template,
        RoiBounds bounds,
        int targetWidth,
        int targetHeight)
    {
        var maxX = Math.Min(
            screen.Width - targetWidth,
            bounds.X + bounds.Width - targetWidth);
        var maxY = Math.Min(
            screen.Height - targetHeight,
            bounds.Y + bounds.Height - targetHeight);
        if (bounds.X > maxX || bounds.Y > maxY)
        {
            return new TemplateMatchResult(
                false,
                double.MinValue,
                0,
                0,
                targetWidth,
                targetHeight);
        }

        const int candidateStep = 4;
        const int sampleWidth = 24;
        const int sampleHeight = 24;
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;
        for (var y = bounds.Y; y <= maxY; y += candidateStep)
        {
            for (var x = bounds.X; x <= maxX; x += candidateStep)
            {
                var score = CompareMaskedSamples(
                    screen,
                    template,
                    x,
                    y,
                    targetWidth,
                    targetHeight,
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

        var refineMinX = Math.Max(bounds.X, bestX - candidateStep + 1);
        var refineMaxX = Math.Min(maxX, bestX + candidateStep - 1);
        var refineMinY = Math.Max(bounds.Y, bestY - candidateStep + 1);
        var refineMaxY = Math.Min(maxY, bestY + candidateStep - 1);
        for (var y = refineMinY; y <= refineMaxY; y++)
        {
            for (var x = refineMinX; x <= refineMaxX; x++)
            {
                var score = CompareMaskedSamples(
                    screen,
                    template,
                    x,
                    y,
                    targetWidth,
                    targetHeight,
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

        return new TemplateMatchResult(
            false,
            bestScore,
            bestX,
            bestY,
            targetWidth,
            targetHeight);
    }

    private static double CompareMaskedSamples(
        GrayImage screen,
        MaskedTemplate template,
        int screenX,
        int screenY,
        int targetWidth,
        int targetHeight,
        int sampleWidth,
        int sampleHeight)
    {
        Span<double> templateValues = stackalloc double[sampleWidth * sampleHeight];
        Span<double> screenValues = stackalloc double[sampleWidth * sampleHeight];
        var count = 0;
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var templateY = Math.Min(
                template.Height - 1,
                sampleY * template.Height / sampleHeight);
            var screenYAt = screenY + Math.Min(
                targetHeight - 1,
                sampleY * targetHeight / sampleHeight);
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                // The roster rank badge usually covers the upper-right part
                // of a portrait. The left and centre head shape remain stable.
                if (sampleX >= sampleWidth * 3 / 4)
                    continue;

                var templateX = Math.Min(
                    template.Width - 1,
                    sampleX * template.Width / sampleWidth);
                var templateIndex = templateY * template.Width + templateX;
                if (template.Mask[templateIndex] < 48)
                    continue;

                var screenXAt = screenX + Math.Min(
                    targetWidth - 1,
                    sampleX * targetWidth / sampleWidth);
                templateValues[count] = template.Pixels[templateIndex];
                screenValues[count] = screen.Pixels[screenYAt * screen.Width + screenXAt];
                count++;
            }
        }

        if (count < 32)
            return 0;

        return PearsonCorrelation(
            templateValues[..count],
            screenValues[..count]);
    }

    private static MaskedTemplate? LoadMaskedHeadTemplate(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var image = Image.Load<Rgba32>(path);
            var minX = image.Width;
            var minY = image.Height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    if (image[x, y].A < 24)
                        continue;

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
                return null;

            // The upper part contains the hat, ears and hair silhouette;
            // lower costume pixels are intentionally excluded because roster
            // cards show a portrait rather than the full-body source art.
            var headBottom = Math.Min(
                maxY,
                minY + Math.Max(1, (int)Math.Round((maxY - minY + 1) * 0.44)));
            var headMinX = image.Width;
            var headMaxX = -1;
            for (var y = minY; y <= headBottom; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (image[x, y].A < 24)
                        continue;

                    headMinX = Math.Min(headMinX, x);
                    headMaxX = Math.Max(headMaxX, x);
                }
            }

            if (headMaxX < headMinX)
                return null;

            using var crop = image.Clone(context => context.Crop(new Rectangle(
                headMinX,
                minY,
                headMaxX - headMinX + 1,
                headBottom - minY + 1)));
            crop.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(128, 128),
                Mode = ResizeMode.Stretch,
            }));

            var rgba = new byte[checked(crop.Width * crop.Height * 4)];
            crop.CopyPixelDataTo(rgba);
            var pixels = new byte[crop.Width * crop.Height];
            var mask = new byte[pixels.Length];
            for (var index = 0; index < pixels.Length; index++)
            {
                var offset = index * 4;
                pixels[index] = (byte)((rgba[offset] * 299
                    + rgba[offset + 1] * 587
                    + rgba[offset + 2] * 114) / 1000);
                mask[index] = rgba[offset + 3];
            }

            return new MaskedTemplate(crop.Width, crop.Height, pixels, mask);
        }
        catch (Exception) when (File.Exists(path))
        {
            return null;
        }
    }

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

    private static TemplateMatchResult FindAtSize(
        GrayImage screen,
        GrayImage template,
        RoiBounds bounds,
        int targetWidth,
        int targetHeight,
        int candidateStep,
        int sampleWidth,
        int sampleHeight)
    {
        var maxX = Math.Min(
            screen.Width - targetWidth,
            bounds.X + bounds.Width - targetWidth);
        var maxY = Math.Min(
            screen.Height - targetHeight,
            bounds.Y + bounds.Height - targetHeight);
        if (bounds.X > maxX || bounds.Y > maxY)
            return new TemplateMatchResult(false, double.MinValue, 0, 0, targetWidth, targetHeight);

        // Do not sample more points than the rendered target contains. When
        // a small target (for example 4x4) was compared with a larger
        // template, several samples landed on the same screen pixel while
        // still advancing through different template pixels. That made an
        // exact nearest-neighbour match correlate as 0.000.
        sampleWidth = Math.Clamp(
            sampleWidth,
            1,
            Math.Min(32, Math.Min(template.Width, targetWidth)));
        sampleHeight = Math.Clamp(
            sampleHeight,
            1,
            Math.Min(32, Math.Min(template.Height, targetHeight)));
        candidateStep = Math.Clamp(candidateStep, 1, 32);
        // Small scaled references are sensitive to a one-pixel placement
        // error. Use a denser coarse pass for them so the refinement window
        // cannot skip the actual match entirely.
        var smallestTargetDimension = Math.Min(targetWidth, targetHeight);
        candidateStep = Math.Min(
            candidateStep,
            Math.Max(1, smallestTargetDimension / 2));
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;

        for (var y = bounds.Y; y <= maxY; y += candidateStep)
        {
            for (var x = bounds.X; x <= maxX; x += candidateStep)
            {
                var score = CompareSamplesAtSize(
                    screen,
                    template,
                    x,
                    y,
                    targetWidth,
                    targetHeight,
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

        // Refine around the coarse winner so a small portrait is not rejected
        // just because its true top-left is between coarse scan positions.
        var refineMinX = Math.Max(bounds.X, bestX - candidateStep + 1);
        var refineMaxX = Math.Min(maxX, bestX + candidateStep - 1);
        var refineMinY = Math.Max(bounds.Y, bestY - candidateStep + 1);
        var refineMaxY = Math.Min(maxY, bestY + candidateStep - 1);
        for (var y = refineMinY; y <= refineMaxY; y++)
        {
            for (var x = refineMinX; x <= refineMaxX; x++)
            {
                var score = CompareSamplesAtSize(
                    screen,
                    template,
                    x,
                    y,
                    targetWidth,
                    targetHeight,
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

        return new TemplateMatchResult(
            false,
            bestScore,
            bestX,
            bestY,
            targetWidth,
            targetHeight);
    }

    private static double CompareSamplesAtSize(
        GrayImage screen,
        GrayImage template,
        int screenX,
        int screenY,
        int targetWidth,
        int targetHeight,
        int sampleWidth,
        int sampleHeight)
    {
        // Keep the complete reference crop as the primary signal. A tight
        // face-only crop can accidentally prefer a different runner with a
        // similar expression, especially when the card badge overlaps it.
        var fullScore = CompareSamplesAtRegion(
            screen,
            template,
            screenX,
            screenY,
            targetWidth,
            targetHeight,
            sampleWidth,
            sampleHeight,
            0d,
            1d,
            0d,
            1d);
        var faceScore = CompareSamplesAtRegion(
            screen,
            template,
            screenX,
            screenY,
            targetWidth,
            targetHeight,
            sampleWidth,
            sampleHeight,
            0.10d,
            0.90d,
            0.08d,
            0.92d);
        return Math.Clamp(
            fullScore * 0.85d + faceScore * 0.15d,
            -1d,
            1d);
    }

    private static double CompareSamplesAtRegion(
        GrayImage screen,
        GrayImage template,
        int screenX,
        int screenY,
        int targetWidth,
        int targetHeight,
        int sampleWidth,
        int sampleHeight,
        double left,
        double right,
        double top,
        double bottom)
    {
        var sampleCount = sampleWidth * sampleHeight;
        Span<double> templateSamples = stackalloc double[sampleCount];
        Span<double> screenSamples = stackalloc double[sampleCount];
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var relativeY = top + sampleY * (bottom - top) / sampleHeight;
            var templateY = Math.Min(
                template.Height - 1,
                Math.Max(0, (int)Math.Round(relativeY * template.Height)));
            var screenYAt = screenY + Math.Min(
                targetHeight - 1,
                Math.Max(0, (int)Math.Round(relativeY * targetHeight)));
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var relativeX = left + sampleX * (right - left) / sampleWidth;
                var templateX = Math.Min(
                    template.Width - 1,
                    Math.Max(0, (int)Math.Round(relativeX * template.Width)));
                var screenXAt = screenX + Math.Min(
                    targetWidth - 1,
                    Math.Max(0, (int)Math.Round(relativeX * targetWidth)));
                var sampleIndex = sampleY * sampleWidth + sampleX;
                templateSamples[sampleIndex] =
                    template.Pixels[templateY * template.Width + templateX];
                screenSamples[sampleIndex] =
                    screen.Pixels[screenYAt * screen.Width + screenXAt];
            }
        }

        var intensityScore = PearsonCorrelation(templateSamples, screenSamples);
        if (sampleWidth < 3 || sampleHeight < 3)
            return intensityScore;

        var gradientCount = (sampleWidth - 2) * (sampleHeight - 2);
        Span<double> templateGradientX = stackalloc double[gradientCount];
        Span<double> templateGradientY = stackalloc double[gradientCount];
        Span<double> screenGradientX = stackalloc double[gradientCount];
        Span<double> screenGradientY = stackalloc double[gradientCount];
        var gradientIndex = 0;
        for (var sampleY = 1; sampleY < sampleHeight - 1; sampleY++)
        {
            for (var sampleX = 1; sampleX < sampleWidth - 1; sampleX++)
            {
                var center = sampleY * sampleWidth + sampleX;
                templateGradientX[gradientIndex] =
                    templateSamples[center + 1] - templateSamples[center - 1];
                templateGradientY[gradientIndex] =
                    templateSamples[center + sampleWidth] - templateSamples[center - sampleWidth];
                screenGradientX[gradientIndex] =
                    screenSamples[center + 1] - screenSamples[center - 1];
                screenGradientY[gradientIndex] =
                    screenSamples[center + sampleWidth] - screenSamples[center - sampleWidth];
                gradientIndex++;
            }
        }

        var edgeScore = (
            PearsonCorrelation(templateGradientX, screenGradientX)
            + PearsonCorrelation(templateGradientY, screenGradientY)) / 2d;
        return Math.Clamp(
            intensityScore * 0.75d + edgeScore * 0.25d,
            -1d,
            1d);
    }

    private static double PearsonCorrelation(
        ReadOnlySpan<double> first,
        ReadOnlySpan<double> second)
    {
        if (first.Length == 0 || first.Length != second.Length)
            return 0;

        var firstTotal = 0d;
        var secondTotal = 0d;
        for (var index = 0; index < first.Length; index++)
        {
            firstTotal += first[index];
            secondTotal += second[index];
        }

        var firstMean = firstTotal / first.Length;
        var secondMean = secondTotal / second.Length;
        var numerator = 0d;
        var firstVariance = 0d;
        var secondVariance = 0d;
        for (var index = 0; index < first.Length; index++)
        {
            var firstDelta = first[index] - firstMean;
            var secondDelta = second[index] - secondMean;
            numerator += firstDelta * secondDelta;
            firstVariance += firstDelta * firstDelta;
            secondVariance += secondDelta * secondDelta;
        }

        if (firstVariance < 1 || secondVariance < 1)
            return 0;

        return Math.Clamp(
            numerator / Math.Sqrt(firstVariance * secondVariance),
            -1d,
            1d);
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

    private sealed record MaskedTemplate(
        int Width,
        int Height,
        byte[] Pixels,
        byte[] Mask);

    private readonly record struct RoiBounds(int X, int Y, int Width, int Height);
}
