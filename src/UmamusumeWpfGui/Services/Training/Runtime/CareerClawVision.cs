using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Locates the claw control and result button in actual screenshot pixels.</summary>
internal static class CareerClawVision
{
    private readonly record struct Blob(int Area, Rectangle Bounds);

    // Search the entire image at reduced size, then verify the original pixels.
    public static TemplateMatchResult MatchMarker(GrayImage frame, GrayImage template,
        UraScreenProfile profile, UraScreenRecognition recognition)
    {
        if (frame.RgbaPixels is null || template.RgbaPixels is null)
            return TemplateMatcher.Find(frame, template, null, recognition.TemplateThreshold,
                profile.ReferenceWidth, profile.ReferenceHeight);
        var settings = profile.ClawMachine;
        var scaledTemplate = Resize(template,
            Math.Max(1, template.Width * frame.Width / profile.ReferenceWidth),
            Math.Max(1, template.Height * frame.Height / profile.ReferenceHeight));
        var scale = Math.Clamp(settings.MarkerSearchScale, 0.1, 1);
        var reducedFrame = Resize(frame, Math.Max(1, (int)(frame.Width * scale)),
            Math.Max(1, (int)(frame.Height * scale)));
        var reducedTemplate = Resize(scaledTemplate,
            Math.Max(1, (int)(scaledTemplate.Width * scale)),
            Math.Max(1, (int)(scaledTemplate.Height * scale)));
        var candidate = TemplateMatcher.FindColor(reducedFrame, reducedTemplate,
            null, settings.MarkerCandidateThreshold, reducedFrame.Width, reducedFrame.Height);
        if (!candidate.Found)
            return candidate with { Found = false };
        var padding = (int)Math.Ceiling(2 / scale);
        var left = Math.Max(0, (int)(candidate.X / scale) - padding);
        var top = Math.Max(0, (int)(candidate.Y / scale) - padding);
        var right = Math.Min(frame.Width,
            (int)((candidate.X + candidate.Width) / scale) + padding);
        var bottom = Math.Min(frame.Height,
            (int)((candidate.Y + candidate.Height) / scale) + padding);
        return TemplateMatcher.FindColor(frame, scaledTemplate,
            [left, top, right - left, bottom - top], recognition.TemplateThreshold,
            frame.Width, frame.Height, requireTextContrast: recognition.MatchColorText);
    }

    public static bool TryFindControl(GrayImage frame, TemplateMatchResult marker,
        CareerClawMachineSettings settings, out Rectangle bounds)
    {
        bounds = default;
        if (!marker.Found) return false;
        var top = marker.Y + marker.Height;
        var control = FindBlobs(frame,
                new Rectangle(0, top, frame.Width, frame.Height - top), settings.WhiteControlColor.Accepts)
            .Where(blob => Fits(frame, blob, settings.ControlShape)
                && HasColorAround(frame, blob.Bounds, settings.PinkControlColor,
                    settings.ControlPinkPixelRatio, settings.ControlBorderPaddingRatio))
            .OrderByDescending(blob => blob.Bounds.Bottom).FirstOrDefault();
        if (control.Area == 0 || control.Bounds.Top <= top) return false;
        bounds = control.Bounds;
        return true;
    }

    public static IReadOnlyList<Rectangle> FindResultButtons(GrayImage frame,
        TemplateMatchResult marker, CareerClawMachineSettings settings)
    {
        var top = marker.Y + marker.Height;
        return FindBlobs(frame, new Rectangle(0, top, frame.Width, frame.Height - top), settings.ResultButtonColor.Accepts)
            .Where(blob => Fits(frame, blob, settings.ResultButtonShape)).Select(blob => blob.Bounds).ToArray();
    }

    internal static GrayImage Resize(GrayImage frame, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(frame.RgbaPixels!, frame.Width, frame.Height);
        image.Mutate(operation => operation.Resize(width, height));
        var rgba = new byte[width * height * 4]; image.CopyPixelDataTo(rgba);
        var pixels = new byte[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var offset = index * 4;
            pixels[index] = (byte)((rgba[offset] + rgba[offset + 1] + rgba[offset + 2]) / 3);
        }
        return new GrayImage(width, height, pixels, rgba);
    }

    private static bool Fits(GrayImage frame, Blob blob, ClawBlobRule rule)
    {
        var area = blob.Area / ((double)frame.Width * frame.Height);
        var width = blob.Bounds.Width / (double)frame.Width;
        var height = blob.Bounds.Height / (double)frame.Height;
        var aspect = blob.Bounds.Width / (double)blob.Bounds.Height;
        return area >= rule.MinimumAreaRatio && area <= rule.MaximumAreaRatio
            && width >= rule.MinimumWidthRatio && width <= rule.MaximumWidthRatio
            && height >= rule.MinimumHeightRatio && height <= rule.MaximumHeightRatio
            && aspect >= rule.MinimumAspectRatio && aspect <= rule.MaximumAspectRatio;
    }

    private static bool HasColorAround(GrayImage frame, Rectangle bounds, ClawPixelRule rule,
        double minimumRatio, double paddingRatio)
    {
        var padding = Math.Max(1, (int)(bounds.Width * paddingRatio));
        var expanded = Rectangle.Intersect(new Rectangle(0, 0, frame.Width, frame.Height),
            new Rectangle(bounds.X - padding, bounds.Y - padding, bounds.Width + padding * 2, bounds.Height + padding * 2));
        var matching = 0; var sampled = 0; var pixels = frame.RgbaPixels!;
        for (var y = expanded.Top; y < expanded.Bottom; y++)
        {
            for (var x = expanded.Left; x < expanded.Right; x++)
            {
                if (bounds.Contains(x, y)) continue;
                var offset = (y * frame.Width + x) * 4;
                if (rule.Accepts(pixels[offset], pixels[offset + 1], pixels[offset + 2])) matching++;
                sampled++;
            }
        }
        return sampled > 0 && matching >= sampled * minimumRatio;
    }

    private static List<Blob> FindBlobs(GrayImage frame, Rectangle bounds, Func<byte, byte, byte, bool> accepts)
    {
        var blobs = new List<Blob>(); var pixels = frame.RgbaPixels;
        if (pixels is null || pixels.Length < (long)frame.Width * frame.Height * 4) return blobs;
        bounds = Rectangle.Intersect(bounds, new Rectangle(0, 0, frame.Width, frame.Height));
        if (bounds.Width <= 0 || bounds.Height <= 0) return blobs;
        var visited = new byte[frame.Width * frame.Height]; var pending = new int[bounds.Width * bounds.Height];
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var start = y * frame.Width + x;
                if (visited[start] != 0) continue;
                visited[start] = 1;
                if (!Accepts(start)) continue;
                var read = 0; var count = 1; pending[0] = start;
                var minX = x; var maxX = x; var minY = y; var maxY = y;
                while (read < count)
                {
                    var index = pending[read++]; var pointX = index % frame.Width; var pointY = index / frame.Width;
                    minX = Math.Min(minX, pointX); maxX = Math.Max(maxX, pointX);
                    minY = Math.Min(minY, pointY); maxY = Math.Max(maxY, pointY);
                    Add(pointX - 1, pointY); Add(pointX + 1, pointY); Add(pointX, pointY - 1); Add(pointX, pointY + 1);
                    Add(pointX - 1, pointY - 1); Add(pointX + 1, pointY - 1);
                    Add(pointX - 1, pointY + 1); Add(pointX + 1, pointY + 1);
                }
                blobs.Add(new Blob(count, new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1)));

                void Add(int pointX, int pointY)
                {
                    if (!bounds.Contains(pointX, pointY)) return;
                    var index = pointY * frame.Width + pointX;
                    if (visited[index] != 0) return;
                    visited[index] = 1;
                    if (Accepts(index)) pending[count++] = index;
                }
            }
        }
        return blobs;
        bool Accepts(int index)
        {
            var offset = index * 4;
            return accepts(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        }
    }
}
