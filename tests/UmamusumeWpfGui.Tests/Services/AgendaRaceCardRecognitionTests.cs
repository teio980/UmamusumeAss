using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class AgendaRaceCardRecognitionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    public void Saudi_card_matches_without_lowering_threshold_or_changing_template(int shiftX, int shiftY)
    {
        var (screen, template) = LoadFixture(shiftX, shiftY);
        var match = TemplateMatcher.FindScaled(screen, template, [20, 260, 300, 950],
            0.62d, 900, 1600, [0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56]);

        Assert.True(match.Found, $"Saudi card score was {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 155 + shiftX, 175 + shiftX);
        Assert.InRange(match.CenterY, 385 + shiftY, 410 + shiftY);
    }

    [Fact]
    public void Missing_Saudi_card_does_not_match_other_visible_races()
    {
        var (screen, template) = LoadFixture(0, 0);
        for (var y = 300; y < 490; y++)
            Array.Fill(screen.Pixels, (byte)255, y * screen.Width + 20, 300);
        var match = TemplateMatcher.FindScaled(screen, template, [20, 260, 300, 950],
            0.62d, 900, 1600, [0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56]);

        Assert.False(match.Found, $"An unrelated card scored {match.Score:0.000}.");
    }

    private static (GrayImage Screen, GrayImage Template) LoadFixture(int shiftX, int shiftY)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakePresets.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var crop = GrayImageCodec.FromFile(Path.Combine(root!.FullName, "tests", "UmamusumeWpfGui.Tests",
            "Fixtures", "Agenda", "junior-early-oct-race-cards.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(root.FullName,
            "resource", "hachimi", "ura", "screens", "templates", "independent", "race_cards", "3057.png"));
        Assert.NotNull(crop);
        Assert.NotNull(template);
        var pixels = new byte[900 * 1600];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < crop.Height; y++)
            Array.Copy(crop.Pixels, y * crop.Width, pixels, (y + 260 + shiftY) * 900 + 20 + shiftX, crop.Width);
        return (new GrayImage(900, 1600, pixels), template);
    }
}
