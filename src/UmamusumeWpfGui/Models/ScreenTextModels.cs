using System.Globalization;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Pixel-space rectangle returned by the screen OCR engine.  Coordinates are
/// always in the actual ADB screenshot coordinate system, so callers can tap
/// the bounds without inventing a game-specific coordinate.
/// </summary>
public sealed record ScreenTextRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Math.Max(0, Width);

    public int Bottom => Y + Math.Max(0, Height);

    public int CenterX => X + Math.Max(0, Width) / 2;

    public int CenterY => Y + Math.Max(0, Height) / 2;

    public ScreenTextRect Expand(int[]? expansion)
    {
        if (expansion is not { Length: >= 4 })
            return this;

        var left = Math.Max(0, X - Math.Max(0, expansion[0]));
        var top = Math.Max(0, Y - Math.Max(0, expansion[1]));
        var right = Math.Max(left, Right + Math.Max(0, expansion[2]));
        var bottom = Math.Max(top, Bottom + Math.Max(0, expansion[3]));
        return new ScreenTextRect(left, top, right - left, bottom - top);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"({X},{Y},{Width},{Height})");
}

public sealed record ScreenTextDetection(
    string Text,
    ScreenTextRect Bounds,
    double Confidence = 1d);

public sealed record ScreenTextRecognitionResult(
    IReadOnlyList<ScreenTextDetection> Detections,
    string Language,
    int Width = 0,
    int Height = 0);

public sealed record ScreenTextCandidate(
    string Text,
    ScreenTextRect Bounds,
    double Confidence,
    double Similarity);

public sealed record ScreenTextQueryResult(
    bool Found,
    string TargetText,
    IReadOnlyList<ScreenTextCandidate> Candidates,
    ScreenTextCandidate? Match,
    bool Ambiguous,
    string? Error = null)
{
    public double Similarity => Match?.Similarity ?? 0d;

    public string RecognizedSummary => string.Join(
        "; ",
        Candidates.Select(candidate =>
            $"{candidate.Text}@{candidate.Bounds}"
            + $" sim={candidate.Similarity.ToString("0.000", CultureInfo.InvariantCulture)}"
            + $" conf={candidate.Confidence.ToString("0.000", CultureInfo.InvariantCulture)}"));
}
