using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class QuickModeColorMatcherTests
{
    [Fact]
    public void Color_match_rejects_white_skip_off_for_green_selected_template()
    {
        var template = SolidImage(32, 12, red: 90, green: 200, blue: 0);
        var whiteSkipOff = SolidImage(40, 20, red: 245, green: 245, blue: 245);
        var greenSelected = SolidImage(40, 20, red: 245, green: 245, blue: 245);
        CopyCrop(template, greenSelected, 4, 4);

        var falsePositive = TemplateMatcher.FindColor(
            whiteSkipOff,
            template,
            roi: [4, 4, 32, 12],
            threshold: 0.82,
            referenceWidth: 40,
            referenceHeight: 20);
        var selected = TemplateMatcher.FindColor(
            greenSelected,
            template,
            roi: [4, 4, 32, 12],
            threshold: 0.82,
            referenceWidth: 40,
            referenceHeight: 20);

        Assert.False(falsePositive.Found);
        Assert.True(selected.Found);
        Assert.InRange(selected.Score, 0.99, 1.0);
    }

    private static GrayImage SolidImage(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[width * height];
        var rgba = new byte[pixels.Length * 4];
        var gray = (byte)((red * 299 + green * 587 + blue * 114) / 1000);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = gray;
            var offset = index * 4;
            rgba[offset] = red;
            rgba[offset + 1] = green;
            rgba[offset + 2] = blue;
            rgba[offset + 3] = 255;
        }

        return new GrayImage(width, height, pixels, rgba);
    }

    private static void CopyCrop(GrayImage source, GrayImage destination, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
        {
            for (var column = 0; column < source.Width; column++)
            {
                var sourceIndex = row * source.Width + column;
                var destinationIndex = (y + row) * destination.Width + x + column;
                destination.Pixels[destinationIndex] = source.Pixels[sourceIndex];
                Buffer.BlockCopy(
                    source.RgbaPixels!,
                    sourceIndex * 4,
                    destination.RgbaPixels!,
                    destinationIndex * 4,
                    4);
            }
        }
    }
}
