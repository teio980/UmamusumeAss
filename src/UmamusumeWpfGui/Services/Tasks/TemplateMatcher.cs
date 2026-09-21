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
        // The coarse pass only locates the correlation basin. FindAtSize
        // refines the winning area at pixel precision; the cross-scale local
        // pass below also recovers narrow peaks without scanning the full
        // runner grid at pixel precision.
        int candidateStep = 8,
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
        var bestScale = double.NaN;

        void EvaluateScale(double scale, RoiBounds? searchBounds = null, int? searchStep = null)
        {
            if (!double.IsFinite(scale) || scale <= 0)
                return;

            var targetWidth = Math.Max(
                1,
                (int)Math.Round(template.Width * referenceScale * scale));
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(template.Height * referenceScale * scale));
            if (targetWidth > screen.Width || targetHeight > screen.Height)
                return;

            var match = FindAtSize(
                screen,
                template,
                searchBounds ?? bounds,
                targetWidth,
                targetHeight,
                searchStep ?? candidateStep,
                sampleWidth,
                sampleHeight);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestX = match.X;
                bestY = match.Y;
                bestWidth = targetWidth;
                bestHeight = targetHeight;
                bestScale = scale;
            }
        }

        foreach (var scale in scaleCandidates)
            EvaluateScale(scale);

        // The rendered card size usually falls between two integer template
        // scales. Recheck around the coarse winner at 0.01 increments; this
        // avoids losing a genuinely identical crop merely because the
        // supplied scale list landed 2-3 pixels away from its display size.
        if (double.IsFinite(bestScale))
        {
            var coarseScale = bestScale;
            for (var offset = -4; offset <= 4; offset++)
                EvaluateScale(coarseScale + offset * 0.01d);
        }

        // A text-heavy card can have a narrow correlation peak between the
        // coarse grid points. Repeating the same grid at more scales cannot
        // recover that peak. On a miss, refine only around the best location
        // at pixel precision, keeping the expensive full-ROI scan coarse.
        if (double.IsFinite(bestScale) && bestScore < Math.Clamp(threshold, 0, 1))
        {
            var localScale = bestScale;
            var padding = Math.Clamp(candidateStep, 1, 32);
            var localX = Math.Max(bounds.X, bestX - padding);
            var localY = Math.Max(bounds.Y, bestY - padding);
            var largestWidth = (int)Math.Ceiling(template.Width * referenceScale * (localScale + 0.04d));
            var largestHeight = (int)Math.Ceiling(template.Height * referenceScale * (localScale + 0.04d));
            var localBounds = new RoiBounds(
                localX,
                localY,
                Math.Min(bounds.X + bounds.Width - localX, largestWidth + padding * 2),
                Math.Min(bounds.Y + bounds.Height - localY, largestHeight + padding * 2));
            for (var offset = -4; offset <= 4; offset++)
                EvaluateScale(localScale + offset * 0.01d, localBounds, searchStep: 1);
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
        int referenceHeight,
        int? candidateStepOverride = null)
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
        // Small button/text templates must be compared in full.  Sampling
        // only the top-left 32x32 pixels can match a flat button background
        // at a false location, which then makes ClickSelf tap beside the
        // actual label (notably the narrow Race! button template).
        var sampleWidth = template.Width <= 160
            ? template.Width
            : 32;
        var sampleHeight = template.Height <= 80
            ? template.Height
            : 32;
        // Small button crops are cheap enough to scan at pixel precision;
        // larger state markers use a two-pixel stride to keep polling bounded.
        var candidateStep = candidateStepOverride is > 0
            ? candidateStepOverride.Value
            : template.Width <= 160 ? 1 : 2;
        candidateStep = Math.Clamp(candidateStep, 1, 32);
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

    /// <summary>
    /// Color-aware template matching for state buttons whose geometry is the
    /// same across states.  Grayscale correlation is deliberately unsuitable
    /// for the quick-mode Skip control: the green selected button and the
    /// white "Skip Off" button share the same border/text layout.
    /// Transparent template pixels are ignored so the crop can retain its
    /// authored padding.
    /// </summary>
    public static TemplateMatchResult FindColor(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);

        if (screen.RgbaPixels is null || template.RgbaPixels is null)
        {
            return Find(
                screen,
                template,
                roi,
                threshold,
                referenceWidth,
                referenceHeight);
        }

        if (template.Width > screen.Width || template.Height > screen.Height)
        {
            return new TemplateMatchResult(
                false,
                0,
                0,
                0,
                template.Width,
                template.Height);
        }

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
        {
            return new TemplateMatchResult(
                false,
                0,
                0,
                0,
                template.Width,
                template.Height);
        }

        var candidateStep = template.Width <= 240 ? 1 : 2;
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;
        for (var y = bounds.Y; y <= maxY; y += candidateStep)
        {
            for (var x = bounds.X; x <= maxX; x += candidateStep)
            {
                var score = CompareColorSamples(
                    screen,
                    template,
                    x,
                    y,
                    sampleWidth: Math.Min(32, template.Width),
                    sampleHeight: Math.Min(24, template.Height));
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

    private static double CompareColorSamples(
        GrayImage screen,
        GrayImage template,
        int screenX,
        int screenY,
        int sampleWidth,
        int sampleHeight)
    {
        var screenPixels = screen.RgbaPixels!;
        var templatePixels = template.RgbaPixels!;
        double weightedError = 0;
        double totalWeight = 0;
        var visiblePixels = 0;
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var templateY = sampleY * template.Height / sampleHeight;
            var screenRow = (screenY + templateY) * screen.Width;
            var templateRow = templateY * template.Width;
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var templateX = sampleX * template.Width / sampleWidth;
                var templateOffset = (templateRow + templateX) * 4;
                var alpha = templatePixels[templateOffset + 3];
                if (alpha < 24)
                    continue;

                var screenOffset = (screenRow + screenX + templateX) * 4;
                var colorError =
                    Math.Abs(screenPixels[screenOffset] - templatePixels[templateOffset])
                    + Math.Abs(screenPixels[screenOffset + 1] - templatePixels[templateOffset + 1])
                    + Math.Abs(screenPixels[screenOffset + 2] - templatePixels[templateOffset + 2]);
                var weight = alpha / 255d;
                weightedError += colorError * weight;
                totalWeight += 765d * weight;
                visiblePixels++;
            }
        }

        if (visiblePixels == 0 || totalWeight <= 0)
            return 0;

        return Math.Clamp(1d - weightedError / totalWeight, -1d, 1d);
    }

    /// <summary>
    /// Matches a small button using the button's stroke/text edges instead of
    /// allowing a large flat crop background to dominate the correlation.
    ///
    /// Reset is a particularly important caller: its reference crop includes
    /// a grey page background around the rounded button.  Pearson correlation
    /// over that crop can therefore return a stable high score for unrelated
    /// page content.  The edge mask below is authored from the template and
    /// only scores locations where the template actually has a visible
    /// stroke or glyph.
    /// </summary>
    public static TemplateMatchResult FindButton(
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

        // A 32x16 grid is enough to retain the rounded outline and Reset
        // glyph while keeping the matcher cheap enough for polling.
        var sampleWidth = Math.Min(32, template.Width);
        var sampleHeight = Math.Min(16, template.Height);
        var features = BuildButtonFeatures(template, sampleWidth, sampleHeight);
        if (features.Length < 8)
            return Find(screen, template, roi, threshold, referenceWidth, referenceHeight);

        // The cropped Reset reference is brown text on a light button.  A
        // white decoration (such as the slash at the end of Prioritized
        // Skills) can share a few edge directions with the glyphs, but it
        // cannot provide the dark ink samples. Keep this as a structural
        // gate, rather than trying to fix the false hit by moving a score
        // threshold.
        var darkFeatureCount = features.Count(static feature => feature.IsDark);
        var minimumDarkFeatures = Math.Max(8, (int)Math.Ceiling(darkFeatureCount * 0.20d));
        var backgroundSamples = BuildButtonBackgroundSamples(template);
        var minimumBrightBackgroundSamples = Math.Max(
            8,
            (int)Math.Ceiling(backgroundSamples.Length * 0.70d));
        var inkSamples = BuildButtonInkSamples(template);

        const int candidateStep = 2;
        var bestScore = double.MinValue;
        var bestX = bounds.X;
        var bestY = bounds.Y;
        for (var y = bounds.Y; y <= maxY; y += candidateStep)
        {
            for (var x = bounds.X; x <= maxX; x += candidateStep)
            {
                var score = CompareButtonFeatures(
                    screen,
                    x,
                    y,
                    features,
                    minimumDarkFeatures,
                    backgroundSamples,
                    minimumBrightBackgroundSamples,
                    inkSamples);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return new TemplateMatchResult(
            bestScore >= Math.Clamp(threshold, 0, 1),
            Math.Max(0, bestScore),
            bestX,
            bestY,
            template.Width,
            template.Height);
    }

    /// <summary>
    /// Returns whether a click anchored at <paramref name="match">match</paramref>
    /// produced a meaningful visual change in the surrounding skill row.
    /// This is a safety stop for a stale/false button match: a successful ADB
    /// tap alone does not prove that the Reset control actually responded.
    /// </summary>
    public static bool HasMeaningfulChange(
        GrayImage before,
        GrayImage after,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (before.Width != after.Width || before.Height != after.Height)
            return true;
        if (before.Pixels.Length < before.Width * before.Height
            || after.Pixels.Length < after.Width * after.Height)
        {
            return false;
        }

        // The selected skill row is immediately above Reset. Include a little
        // below it for button pressed-state transitions, but avoid the rest of
        // the page where unrelated animations can create noise.
        var paddingX = Math.Max(1, match.Width * 2);
        var paddingTop = Math.Max(1, match.Height * 3);
        var paddingBottom = Math.Max(1, match.Height * 3);
        var left = Math.Clamp(match.X - paddingX, 0, before.Width - 1);
        var top = Math.Clamp(match.Y - paddingTop, 0, before.Height - 1);
        var right = Math.Clamp(
            match.X + match.Width + paddingX,
            left + 1,
            before.Width);
        var bottom = Math.Clamp(
            match.Y + match.Height + paddingBottom,
            top + 1,
            before.Height);

        long difference = 0;
        var changedPixels = 0;
        var sampledPixels = 0;
        // Downsampling keeps the post-click guard inexpensive while still
        // covering the full row and Reset button.
        for (var y = top; y < bottom; y += 2)
        {
            var row = y * before.Width;
            for (var x = left; x < right; x += 2)
            {
                var delta = Math.Abs(before.Pixels[row + x] - after.Pixels[row + x]);
                difference += delta;
                if (delta >= 8)
                    changedPixels++;
                sampledPixels++;
            }
        }

        if (sampledPixels == 0)
            return false;

        var meanDifference = difference / (double)sampledPixels;
        var changedRatio = changedPixels / (double)sampledPixels;
        return meanDifference >= 3.0d || changedRatio >= 0.015d;
    }

    /// <summary>
    /// Requires a change to remain visible in two settled frames. This
    /// prevents the short pressed-button animation from being mistaken for a
    /// successful Reset and then causing another tap on the same stale match.
    /// </summary>
    public static bool HasPersistentChange(
        GrayImage before,
        GrayImage firstAfter,
        GrayImage settledAfter,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(firstAfter);
        ArgumentNullException.ThrowIfNull(settledAfter);

        return HasMeaningfulChange(before, firstAfter, match)
            && HasMeaningfulChange(before, settledAfter, match)
            && !HasMeaningfulChange(firstAfter, settledAfter, match);
    }

    private static ButtonFeature[] BuildButtonFeatures(
        GrayImage template,
        int sampleWidth,
        int sampleHeight)
    {
        var features = new List<ButtonFeature>(sampleWidth * sampleHeight);
        for (var sampleY = 1; sampleY < sampleHeight - 1; sampleY++)
        {
            var y = Math.Clamp(
                sampleY * template.Height / sampleHeight,
                1,
                template.Height - 2);
            for (var sampleX = 1; sampleX < sampleWidth - 1; sampleX++)
            {
                var x = Math.Clamp(
                    sampleX * template.Width / sampleWidth,
                    1,
                    template.Width - 2);
                var center = template.Pixels[y * template.Width + x];
                var dx = template.Pixels[y * template.Width + x + 1]
                    - template.Pixels[y * template.Width + x - 1];
                var dy = template.Pixels[(y + 1) * template.Width + x]
                    - template.Pixels[(y - 1) * template.Width + x];
                var magnitude = Math.Abs(dx) + Math.Abs(dy);
                // Ignore the flat grey margin and the nearly flat button fill;
                // keep strokes, rounded borders, and the Reset glyph.
                if (magnitude < 16)
                    continue;

                features.Add(new ButtonFeature(
                    x,
                    y,
                    center,
                    dx,
                    dy,
                    center <= 130));
            }
        }

        return features.ToArray();
    }

    private static double CompareButtonFeatures(
        GrayImage screen,
        int screenX,
        int screenY,
        IReadOnlyList<ButtonFeature> features,
        int minimumDarkFeatures,
        IReadOnlyList<ButtonBackgroundSample> backgroundSamples,
        int minimumBrightBackgroundSamples,
        IReadOnlyList<ButtonInkSample> inkSamples)
    {
        var edgeError = 0d;
        var intensityError = 0d;
        var darkFeatureCount = 0;
        foreach (var feature in features)
        {
            var x = screenX + feature.X;
            var y = screenY + feature.Y;
            var center = screen.Pixels[y * screen.Width + x];
            var dx = screen.Pixels[y * screen.Width + x + 1]
                - screen.Pixels[y * screen.Width + x - 1];
            var dy = screen.Pixels[(y + 1) * screen.Width + x]
                - screen.Pixels[(y - 1) * screen.Width + x];
            edgeError += Math.Min(
                510,
                Math.Abs(dx - feature.Dx) + Math.Abs(dy - feature.Dy));
            intensityError += Math.Abs(center - feature.Center);
            if (feature.IsDark && center <= 130)
                darkFeatureCount++;
        }

        if (darkFeatureCount < minimumDarkFeatures)
            return double.MinValue;

        // The text-only crop deliberately omits the outer button. Recover
        // that missing semantic context from the light bands immediately
        // above and below the glyphs. This rejects dark card text and the
        // green header's white slash even when their edge directions happen
        // to correlate with a few letters.
        var brightBackgroundCount = 0;
        var backgroundError = 0d;
        foreach (var sample in backgroundSamples)
        {
            var value = screen.Pixels[(screenY + sample.Y) * screen.Width + screenX + sample.X];
            if (value >= 200)
                brightBackgroundCount++;
            backgroundError += Math.Abs(value - sample.Center);
        }

        if (brightBackgroundCount < minimumBrightBackgroundSamples)
            return double.MinValue;

        var matchedInkPixels = 0;
        var unexpectedInkPixels = 0;
        var templateInkPixels = 0;
        foreach (var sample in inkSamples)
        {
            var value = screen.Pixels[(screenY + sample.Y) * screen.Width + screenX + sample.X];
            var isDark = value <= 130;
            if (sample.ExpectedDark)
            {
                templateInkPixels++;
                if (isDark)
                    matchedInkPixels++;
            }
            else if (isDark)
            {
                unexpectedInkPixels++;
            }
        }

        var inkF1 = 2d * matchedInkPixels
            / Math.Max(
                1,
                2 * matchedInkPixels
                    + unexpectedInkPixels
                    + templateInkPixels - matchedInkPixels);
        // A whole-word mask is what distinguishes Reset from another brown
        // word in the instructional paragraph. Edge correlation alone gave
        // that unrelated word a score of ~0.802 on the no-Reset fixture.
        if (inkF1 < 0.72d)
            return double.MinValue;

        var edgeScore = 1d - edgeError / (features.Count * 510d);
        var intensityScore = 1d - intensityError / (features.Count * 255d);
        // Keep the ink gate explicit, while retaining edge/intensity scoring
        // for the final placement of the complete word.
        var darkCoverageScore = Math.Clamp(
            darkFeatureCount / (double)Math.Max(1, features.Count(static feature => feature.IsDark)),
            0d,
            1d);
        var backgroundScore = 1d - backgroundError
            / (Math.Max(1, backgroundSamples.Count) * 255d);
        return Math.Clamp(
            edgeScore * 0.50d
                + intensityScore * 0.15d
                + darkCoverageScore * 0.20d
                + backgroundScore * 0.10d
                + inkF1 * 0.05d,
            -1d,
            1d);
    }

    private static ButtonBackgroundSample[] BuildButtonBackgroundSamples(
        GrayImage template)
    {
        var samples = new List<ButtonBackgroundSample>();
        var topBandEnd = Math.Min(6, template.Height);
        var bottomBandStart = Math.Max(0, template.Height - 6);
        for (var y = 1; y < topBandEnd; y++)
        {
            for (var x = 3; x < template.Width - 3; x += 3)
            {
                var center = template.Pixels[y * template.Width + x];
                if (center >= 200)
                    samples.Add(new ButtonBackgroundSample(x, y, center));
            }
        }

        for (var y = bottomBandStart; y < template.Height - 1; y++)
        {
            for (var x = 3; x < template.Width - 3; x += 3)
            {
                var center = template.Pixels[y * template.Width + x];
                if (center >= 200)
                    samples.Add(new ButtonBackgroundSample(x, y, center));
            }
        }

        return samples.ToArray();
    }

    private static ButtonInkSample[] BuildButtonInkSamples(GrayImage template)
    {
        var samples = new List<ButtonInkSample>(template.Width * template.Height);
        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                var expectedDark = template.Pixels[y * template.Width + x] <= 130;
                // Keep every dark glyph pixel for recall. Sample the light
                // area at 2px spacing so extra words are penalized without
                // making every polling pass needlessly expensive.
                if (expectedDark || ((x & 1) == 0 && (y & 1) == 0))
                    samples.Add(new ButtonInkSample(x, y, expectedDark));
            }
        }

        return samples.ToArray();
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

    private readonly record struct ButtonFeature(
        int X,
        int Y,
        byte Center,
        int Dx,
        int Dy,
        bool IsDark);

    private readonly record struct ButtonBackgroundSample(
        int X,
        int Y,
        byte Center);

    private readonly record struct ButtonInkSample(
        int X,
        int Y,
        bool ExpectedDark);

    private readonly record struct RoiBounds(int X, int Y, int Width, int Height);
}
