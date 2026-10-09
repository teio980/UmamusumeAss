using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Isolate displayed training glyphs before using the existing numeric OCR.</summary>
internal static class UraTrainingNumberImagePreprocessor
{
    private sealed record Component(int Left, int Top, int Right, int Bottom, List<int> Pixels);

    public static GrayImage? Prepare(GrayImage crop, bool gain, Func<byte, byte, byte, bool> isGainSeed)
    {
        if (crop.RgbaPixels is not { } rgba)
            return crop;
        // Zero-failure cards have a blue body. Restrict its white glyphs to that
        // body so hair, clothes and the Duel badge below cannot become the
        // "last number line". Preserve the existing path for other card colors.
        var blueRows = new Dictionary<int, (int Left, int Right)>();
        if (!gain)
            for (var y = 0; y < crop.Height; y++)
            {
                var left = crop.Width;
                var right = -1;
                var count = 0;
                for (var x = 0; x < crop.Width; x++)
                {
                    var at = (y * crop.Width + x) * 4;
                    if (rgba[at + 2] <= rgba[at] + 60 || rgba[at + 2] <= rgba[at + 1] + 15
                        || rgba[at + 1] < 60)
                        continue;
                    left = Math.Min(left, x);
                    right = x;
                    count++;
                }
                if (count >= crop.Width * .35)
                    blueRows[y] = (left, right);
            }
        var hasBlueBody = blueRows.Count >= crop.Height * .25;
        var foreground = new bool[crop.Width * crop.Height];
        for (var pixel = 0; pixel < foreground.Length; pixel++)
        {
            var offset = pixel * 4;
            var r = rgba[offset];
            var g = rgba[offset + 1];
            var b = rgba[offset + 2];
            var insideBody = !hasBlueBody
                || (blueRows.TryGetValue(pixel / crop.Width, out var body)
                    && pixel % crop.Width >= body.Left && pixel % crop.Width <= body.Right);
            foreground[pixel] = gain
                ? r >= 150 && g is >= 30 and <= 245 && b <= 230 && r >= g + 15 && g >= b + 10
                : insideBody && ((r >= 220 && g >= 220 && b >= 220)
                    || (r >= 220 && g >= 180 && b <= 100));
        }
        var components = new List<Component>();
        for (var pixel = 0; pixel < foreground.Length; pixel++)
        {
            if (!foreground[pixel])
                continue;
            var pixels = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(pixel);
            foreground[pixel] = false;
            var left = crop.Width;
            var top = crop.Height;
            var right = 0;
            var bottom = 0;
            var seeds = 0;
            while (queue.TryDequeue(out var index))
            {
                pixels.Add(index);
                var x = index % crop.Width;
                var y = index / crop.Width;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                if (isGainSeed(rgba[index * 4], rgba[index * 4 + 1], rgba[index * 4 + 2]))
                    seeds++;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }
            // Broad orange background components reach the crop edge. Glyphs contain
            // the authored bright orange seed and have comparable digit/plus heights.
            if (!gain || (seeds >= Math.Max(3, pixels.Count / 50)
                && left > 0 && top > 0 && right < crop.Width && bottom < crop.Height
                && bottom - top >= crop.Height * 0.3))
                components.Add(new(left, top, right, bottom, pixels));

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= crop.Width || y >= crop.Height)
                    return;
                var index = y * crop.Width + x;
                if (!foreground[index])
                    return;
                foreground[index] = false;
                queue.Enqueue(index);
            }
        }
        if (gain && components.Count > 0)
        {
            // The plus is square and shorter than the digits. Discard arrow decoration
            // on its left rather than allowing it to introduce a second number.
            var plus = components.Where(component =>
                (component.Right - component.Left) / (double)(component.Bottom - component.Top) is >= 0.9 and <= 1.2
                && component.Bottom - component.Top < crop.Height * 0.6
                && HasCrossStrokes(component, crop.Width))
                .OrderBy(component => component.Left).FirstOrDefault();
            if (plus is null)
                return null;
            components.RemoveAll(component => component.Left < plus.Left);
        }
        else if (!gain && components.Count > 0)
        {
            // Keep the existing whole failure-card ROI. The number line can move
            // vertically, so find its last substantial white/yellow glyphs rather than
            // using a fixed slice that clips the percentage on older captures.
            components.RemoveAll(component => component.Left <= 0 || component.Top <= 0
                || component.Right >= crop.Width || component.Bottom >= crop.Height
                || (!hasBlueBody && ((component.Left + component.Right) / 2d < crop.Width * 0.25
                    || (component.Left + component.Right) / 2d > crop.Width * 0.75)));
            var anchors = components.Where(component => component.Bottom - component.Top >= crop.Height * 0.15
                && component.Right - component.Left >= crop.Width * 0.025).ToArray();
            if (anchors.Length == 0)
                return null;
            var bottom = anchors.Max(component => component.Bottom);
            components.RemoveAll(component => component.Bottom < bottom - crop.Height * 0.2
                || component.Top > bottom);
        }
        // Empty images retain their original geometry for the separate visual blank rule.
        var minX = components.Count == 0 ? 0 : components.Min(component => component.Left);
        var minY = components.Count == 0 ? 0 : components.Min(component => component.Top);
        var maxX = components.Count == 0 ? crop.Width : components.Max(component => component.Right);
        var maxY = components.Count == 0 ? crop.Height : components.Max(component => component.Bottom);
        var padding = Math.Max(6, crop.Height / 6);
        var width = maxX - minX + padding * 2;
        var height = maxY - minY + padding * 2;
        var gray = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (var component in components)
            foreach (var index in component.Pixels)
                gray[(index / crop.Width - minY + padding) * width
                    + index % crop.Width - minX + padding] = 0;
        if (gain)
            RestoreBrightGlyphInteriors(gray, width, height, crop, minX, minY, padding);
        var prepared = new byte[gray.Length * 4];
        for (var pixel = 0; pixel < gray.Length; pixel++)
        {
            prepared[pixel * 4] = prepared[pixel * 4 + 1] = prepared[pixel * 4 + 2] = gray[pixel];
            prepared[pixel * 4 + 3] = 255;
        }
        return new(width, height, gray, prepared);
    }

    private static bool HasCrossStrokes(Component component, int cropWidth)
    {
        // A capped-stat chevron is also nearly square. A plus has both a
        // horizontal and a vertical stroke spanning almost its full bounds;
        // the chevron's vertical projection is much shorter.
        var rows = new int[component.Bottom - component.Top];
        var columns = new int[component.Right - component.Left];
        foreach (var pixel in component.Pixels)
        {
            rows[pixel / cropWidth - component.Top]++;
            columns[pixel % cropWidth - component.Left]++;
        }
        return rows.Max() >= columns.Length * 0.8 && columns.Max() >= rows.Length * 0.8;
    }

    private static void RestoreBrightGlyphInteriors(byte[] gray, int width, int height,
        GrayImage source, int minX, int minY, int padding)
    {
        // The capped-growth font has pale yellow interiors disconnected from its
        // orange outline. Restore only enclosed pixels whose original color is bright;
        // dark/background counters in 0/6/8/9 stay open.
        var visited = new bool[gray.Length];
        for (var pixel = 0; pixel < gray.Length; pixel++)
        {
            if (visited[pixel] || gray[pixel] != 255)
                continue;
            var queue = new Queue<int>();
            var hole = new List<int>();
            var exterior = false;
            visited[pixel] = true;
            queue.Enqueue(pixel);
            while (queue.TryDequeue(out var index))
            {
                hole.Add(index);
                var x = index % width;
                var y = index / width;
                exterior |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }
            if (exterior)
                continue;
            foreach (var index in hole)
            {
                var x = index % width + minX - padding;
                var y = index / width + minY - padding;
                if (x < 0 || y < 0 || x >= source.Width || y >= source.Height)
                    continue;
                var offset = (y * source.Width + x) * 4;
                if (source.RgbaPixels![offset] >= 235 && source.RgbaPixels[offset + 1] >= 225)
                    gray[index] = 0;
            }

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                    return;
                var index = y * width + x;
                if (visited[index] || gray[index] != 255)
                    return;
                visited[index] = true;
                queue.Enqueue(index);
            }
        }
    }
}
