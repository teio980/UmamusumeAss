using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Reads the movable claw and exposed plush faces from a single crane frame.
/// Coordinates returned here are in the 900x1600 screen-profile space.
/// </summary>
internal static class CareerClawVision
{
    internal readonly record struct Point(int X, int Y);

    private readonly record struct Blob(int Area, int Left, int Top, int Right,
        int Bottom, int CenterX, int CenterY)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    public static bool TryFindTarget(GrayImage frame, Point? previous,
        out Point target)
    {
        target = default;
        var faces = FindBlobs(frame, 280, 780, 870, 1010, IsFacePixel)
            .Where(blob => blob.Area is >= 600 and <= 6500
                && blob.Width is >= 25 and <= 115
                && blob.Height is >= 25 and <= 120
                // A face sunk into the front pile is a toppled/occluded
                // plush, not a reliable standing target.
                && blob.CenterY <= 915
                && HasFaceDetail(frame, blob))
            .ToArray();
        if (faces.Length == 0)
            return false;

        Blob chosen;
        if (previous is { } last)
        {
            chosen = faces
                .OrderBy(blob => Math.Abs(blob.CenterY - last.Y) * 2
                    + Math.Abs(blob.CenterX - last.X))
                .First();
            if (Math.Abs(chosen.CenterY - last.Y) > 85
                || chosen.CenterX > last.X + 90)
                return false;
        }
        else
        {
            // High, exposed faces are easier to secure. On ties, prefer the
            // leftmost reachable plush because it is nearer to the claw.
            chosen = faces.OrderBy(blob => blob.CenterY)
                .ThenBy(blob => blob.CenterX).First();
        }

        target = new Point(chosen.CenterX, chosen.CenterY);
        return true;
    }

    public static bool TryFindClaw(GrayImage frame, Point? previous,
        out Point center)
    {
        center = default;
        var arms = FindBlobs(frame, 100, 390, 860, 490, IsArmPixel)
            .Where(blob => blob.Area is >= 700 and <= 6500
                && blob.Width is >= 65 and <= 190
                && blob.Height is >= 60 and <= 110)
            .OrderBy(blob => blob.CenterX)
            .ToArray();
        var bestScore = int.MaxValue;
        for (var left = 0; left < arms.Length; left++)
        {
            for (var right = left + 1; right < arms.Length; right++)
            {
                var a = arms[left];
                var b = arms[right];
                var separation = b.CenterX - a.CenterX;
                if (separation is < 145 or > 275
                    || Math.Abs(a.CenterY - b.CenterY) > 28
                    || a.Area * 2 < b.Area || b.Area * 2 < a.Area)
                    continue;
                var x = (a.CenterX + b.CenterX) / 2;
                var score = Math.Abs(a.CenterY - b.CenterY) * 3
                    + Math.Abs(a.Area - b.Area) / 40
                    + (previous is { } last
                        ? Math.Abs(x - last.X)
                        : Math.Abs(x - 320));
                if (score >= bestScore)
                    continue;
                bestScore = score;
                center = new Point(x, (a.CenterY + b.CenterY) / 2);
            }
        }

        return bestScore != int.MaxValue;
    }

    private static bool IsFacePixel(byte red, byte green, byte blue) =>
        red >= 205 && green >= 165 && blue >= 125
        && red - green is >= 10 and <= 55
        && green - blue is >= 8 and <= 58;

    private static bool IsArmPixel(byte red, byte green, byte blue) =>
        red is >= 110 and <= 205
        && green is >= 125 and <= 220
        && blue is >= 100 and <= 205
        && green - red is >= 4 and <= 30
        && green - blue is >= 0 and <= 35;

    private static bool HasFaceDetail(GrayImage frame, Blob blob)
    {
        var rgba = frame.RgbaPixels!;
        var left = Scale(blob.Left, frame.Width, 900);
        var right = Scale(blob.Right, frame.Width, 900);
        var top = Scale(blob.Top, frame.Height, 1600);
        var bottom = Scale(blob.Bottom, frame.Height, 1600);
        var dark = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var offset = (y * frame.Width + x) * 4;
                if (rgba[offset] < 140 && rgba[offset + 1] < 140
                    && rgba[offset + 2] < 150)
                    dark++;
            }
        }

        var area = (right - left + 1) * (bottom - top + 1);
        return dark >= Math.Max(30, area * 0.015);
    }

    private static List<Blob> FindBlobs(GrayImage frame,
        int leftReference, int topReference, int rightReference,
        int bottomReference, Func<byte, byte, byte, bool> accepts)
    {
        var blobs = new List<Blob>();
        var pixels = frame.RgbaPixels;
        if (pixels is null || pixels.Length < (long)frame.Width * frame.Height * 4)
            return blobs;
        var left = Scale(leftReference, frame.Width, 900);
        var top = Scale(topReference, frame.Height, 1600);
        var right = Math.Min(frame.Width - 1,
            Scale(rightReference, frame.Width, 900));
        var bottom = Math.Min(frame.Height - 1,
            Scale(bottomReference, frame.Height, 1600));
        if (left > right || top > bottom)
            return blobs;
        var visited = new byte[frame.Width * frame.Height];
        var pending = new int[(right - left + 1) * (bottom - top + 1)];

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var start = y * frame.Width + x;
                if (visited[start] != 0)
                    continue;
                visited[start] = 1;
                if (!Accepts(start))
                    continue;
                var read = 0;
                var count = 1;
                pending[0] = start;
                long sumX = 0;
                long sumY = 0;
                var minX = x;
                var maxX = x;
                var minY = y;
                var maxY = y;
                while (read < count)
                {
                    var index = pending[read++];
                    var pointX = index % frame.Width;
                    var pointY = index / frame.Width;
                    sumX += pointX;
                    sumY += pointY;
                    minX = Math.Min(minX, pointX);
                    maxX = Math.Max(maxX, pointX);
                    minY = Math.Min(minY, pointY);
                    maxY = Math.Max(maxY, pointY);
                    Add(pointX - 1, pointY);
                    Add(pointX + 1, pointY);
                    Add(pointX, pointY - 1);
                    Add(pointX, pointY + 1);
                    Add(pointX - 1, pointY - 1);
                    Add(pointX + 1, pointY - 1);
                    Add(pointX - 1, pointY + 1);
                    Add(pointX + 1, pointY + 1);
                }

                var referenceArea = (int)Math.Round(count
                    * 900.0 / frame.Width * 1600.0 / frame.Height);
                blobs.Add(new Blob(referenceArea,
                    Scale(minX, 900, frame.Width),
                    Scale(minY, 1600, frame.Height),
                    Scale(maxX, 900, frame.Width),
                    Scale(maxY, 1600, frame.Height),
                    Scale((int)(sumX / count), 900, frame.Width),
                    Scale((int)(sumY / count), 1600, frame.Height)));

                void Add(int pointX, int pointY)
                {
                    if (pointX < left || pointX > right
                        || pointY < top || pointY > bottom)
                        return;
                    var index = pointY * frame.Width + pointX;
                    if (visited[index] != 0)
                        return;
                    visited[index] = 1;
                    if (Accepts(index))
                        pending[count++] = index;
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

    private static int Scale(int value, int destination, int source) =>
        Math.Clamp((int)Math.Round(value * (double)destination / source),
            0, Math.Max(0, destination - 1));
}
