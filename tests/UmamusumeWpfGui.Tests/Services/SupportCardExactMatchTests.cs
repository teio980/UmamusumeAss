using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class SupportCardExactMatchTests
{
    private static readonly int[] CardListRoi = [35, 180, 830, 1120];

    [Fact]
    public void Preferred_scale_finds_exact_card_among_other_visible_cards()
    {
        var target = LoadCard(20024);
        var other = LoadCard(20009);
        var screen = NewScreen();
        PlaceCard(screen, other, 55, 215, 144, 130);
        PlaceCard(screen, target, 220, 215, 144, 130);

        var match = TemplateMatcher.FindScaled(
            screen, target, CardListRoi, 0.78, 900, 1600, [1.10],
            fullSearchOnMiss: false);

        Assert.True(match.Found, $"Exact card score {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 285, 300);
        Assert.InRange(match.CenterY, 275, 285);
    }

    [Fact]
    public void Missing_exact_card_is_not_replaced_by_a_different_card()
    {
        var target = LoadCard(20024);
        var other = LoadCard(20009);
        var screen = NewScreen();
        PlaceCard(screen, other, 55, 215, 144, 130);

        var match = TemplateMatcher.FindScaled(
            screen, target, CardListRoi, 0.78, 900, 1600, [1.10],
            fullSearchOnMiss: false);

        Assert.False(match.Found, $"Different card scored {match.Score:0.000}.");
    }

    [Fact]
    public void Wider_scale_fallback_finds_card_on_a_later_list_page()
    {
        var target = LoadCard(20024);
        var other = LoadCard(20009);
        var firstPage = NewScreen();
        PlaceCard(firstPage, other, 55, 215, 144, 130);
        var laterPage = NewScreen();
        PlaceCard(laterPage, target, 220, 610, 117, 105);

        var firstMatch = TemplateMatcher.FindScaled(
            firstPage, target, CardListRoi, 0.78, 900, 1600, [1.10],
            fullSearchOnMiss: false);
        var laterMatch = TemplateMatcher.FindScaled(
            laterPage, target, CardListRoi, 0.78, 900, 1600,
            [0.80, 0.85, 0.90, 0.95, 1.0, 1.05, 1.10],
            fullSearchOnMiss: false);

        Assert.False(firstMatch.Found);
        Assert.True(laterMatch.Found, $"Fallback score {laterMatch.Score:0.000}.");
        Assert.InRange(laterMatch.CenterY, 660, 670);
    }

    private static GrayImage LoadCard(int cardId)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakePresets.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "resource", "uma", "assets", "templates",
            "global", "support_cards", cardId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "card.png");
        var image = GrayImageCodec.FromFile(path);
        Assert.NotNull(image);
        return image;
    }

    private static GrayImage NewScreen()
    {
        var pixels = new byte[900 * 1600];
        Array.Fill(pixels, (byte)127);
        return new GrayImage(900, 1600, pixels);
    }

    private static void PlaceCard(
        GrayImage screen,
        GrayImage card,
        int left,
        int top,
        int width,
        int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                screen.Pixels[(top + y) * screen.Width + left + x] =
                    card.Pixels[(y * card.Height / height) * card.Width + x * card.Width / width];
            }
        }
    }
}
