using System.IO;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class StartGameTemplateMatcherTests
{
    [Theory]
    [InlineData("debug/cold_8.png", "templates/start_game/logo_skip.png", 56, 612, 794, 249, 0.86)]
    [InlineData("debug/cold_7.png", "templates/start_game/startnotice_skip.png", 68, 444, 764, 369, 0.84)]
    [InlineData("debug/user_repro_current.png", "templates/start_game/tap_to_start.png", 0, 0, 500, 120, 0.84)]
    public void Startup_template_matches_saved_screen(
        string screenName,
        string templateName,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        double threshold)
    {
        var root = FindSolutionRoot();
        var screen = GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi", screenName));
        var template = GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi", templateName));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen!,
            template!,
            [roiX, roiY, roiWidth, roiHeight],
            threshold,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.True(result.Found, $"Template score was {result.Score:0.000}.");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
