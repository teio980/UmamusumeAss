using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

internal sealed record CareerEnergyMeasurement(int Percent, double Confidence);

/// <summary>
/// Reads the career energy gauge from a stable color screenshot.
/// The game does not render a numeric energy value, so this uses the width of
/// the saturated fill area inside the configured bar ROI.
/// </summary>
internal static class CareerEnergyBarReader
{
    public static CareerEnergyMeasurement? TryMeasure(
        GrayImage frame,
        UraEnergyBarObservation definition,
        int referenceWidth,
        int referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);

        if (frame.RgbaPixels is not { Length: > 0 } rgba
            || definition.Roi is not { Length: >= 4 } roi)
        {
            return null;
        }

        var inset = definition.InnerInset is { Length: >= 4 }
            ? definition.InnerInset
            : [0, 0, roi[2], roi[3]];
        var refWidth = Math.Max(1, referenceWidth);
        var refHeight = Math.Max(1, referenceHeight);
        var left = ScaleCoordinate(roi[0] + inset[0], refWidth, frame.Width);
        var top = ScaleCoordinate(roi[1] + inset[1], refHeight, frame.Height);
        var right = ScaleCoordinate(
            roi[0] + inset[0] + Math.Max(1, inset[2]) - 1,
            refWidth,
            frame.Width);
        var bottom = ScaleCoordinate(
            roi[1] + inset[1] + Math.Max(1, inset[3]) - 1,
            refHeight,
            frame.Height);

        left = Math.Clamp(left, 0, Math.Max(0, frame.Width - 1));
        right = Math.Clamp(right, left, Math.Max(left, frame.Width - 1));
        top = Math.Clamp(top, 0, Math.Max(0, frame.Height - 1));
        bottom = Math.Clamp(bottom, top, Math.Max(top, frame.Height - 1));

        var width = right - left + 1;
        var height = bottom - top + 1;
        if (width < 8 || height < 3)
            return null;

        var fillSaturationMin = Math.Clamp(
            double.IsFinite(definition.FillSaturationMin)
                ? definition.FillSaturationMin
                : 0.18,
            0,
            1);
        var fillValueMin = Math.Clamp(
            double.IsFinite(definition.FillValueMin)
                ? definition.FillValueMin
                : 0.18,
            0,
            1);
        var columnMatchRatio = Math.Clamp(
            double.IsFinite(definition.ColumnMatchRatio)
                ? definition.ColumnMatchRatio
                : 0.55,
            0.1,
            1);
        var columnScores = new double[width];
        var neutralScores = new double[width];

        for (var column = 0; column < width; column++)
        {
            var fillMatches = 0;
            var neutralMatches = 0;
            for (var y = top; y <= bottom; y++)
            {
                var pixel = ((long)y * frame.Width + left + column) * 4;
                if (pixel < 0 || pixel + 2 >= rgba.Length)
                    continue;

                var red = rgba[(int)pixel] / 255d;
                var green = rgba[(int)pixel + 1] / 255d;
                var blue = rgba[(int)pixel + 2] / 255d;
                var max = Math.Max(red, Math.Max(green, blue));
                var min = Math.Min(red, Math.Min(green, blue));
                var saturation = max <= 0 ? 0 : (max - min) / max;
                if (saturation >= fillSaturationMin && max >= fillValueMin)
                    fillMatches++;
                if (saturation <= 0.12 && max >= 0.12 && max <= 0.92)
                    neutralMatches++;
            }

            columnScores[column] = fillMatches / (double)height;
            neutralScores[column] = neutralMatches / (double)height;
        }

        var firstFilled = FindFirstRun(columnScores, columnMatchRatio, 3);
        if (firstFilled < 0)
        {
            var neutralColumns = neutralScores.Count(score => score >= 0.55);
            return neutralColumns >= width * 0.80
                ? new CareerEnergyMeasurement(0, 0.75)
                : null;
        }

        var endExclusive = firstFilled;
        var misses = 0;
        var relaxedThreshold = Math.Max(0.35, columnMatchRatio * 0.70);
        for (var column = firstFilled; column < width; column++)
        {
            if (columnScores[column] >= relaxedThreshold)
            {
                endExclusive = column + 1;
                misses = 0;
                continue;
            }

            misses++;
            if (misses >= 3)
                break;
        }

        var filledWidth = Math.Clamp(endExclusive - firstFilled, 0, width);
        var percent = (int)Math.Round(
            filledWidth / (double)width * 100,
            MidpointRounding.AwayFromZero);
        percent = Math.Clamp(percent, 0, 100);

        var fillConfidence = columnScores
            .Skip(firstFilled)
            .Take(Math.Max(1, filledWidth))
            .DefaultIfEmpty()
            .Average();
        var boundaryConfidence = endExclusive >= width
            ? 1d
            : Math.Clamp(
                neutralScores[endExclusive] - columnScores[endExclusive],
                0,
                1);
        var confidence = Math.Clamp(
            fillConfidence * 0.75 + boundaryConfidence * 0.25,
            0,
            1);

        return new CareerEnergyMeasurement(percent, confidence);
    }

    private static int FindFirstRun(
        double[] scores,
        double threshold,
        int runLength)
    {
        var run = 0;
        for (var index = 0; index < scores.Length; index++)
        {
            run = scores[index] >= threshold ? run + 1 : 0;
            if (run >= runLength)
                return index - runLength + 1;
        }

        return -1;
    }

    private static int ScaleCoordinate(int value, int reference, int actual) =>
        (int)Math.Round(
            value * (double)Math.Max(1, actual) / Math.Max(1, reference),
            MidpointRounding.AwayFromZero);
}
