using System.IO;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class TeamRaceTemplateMatcherTests
{
    [Theory]
    [InlineData("buttons/race_tab.png")]
    [InlineData("buttons/team_trials.png")]
    [InlineData("buttons/team_race.png")]
    [InlineData("buttons/first_uma.png")]
    [InlineData("race_result.png")]
    public void Representative_template_matches_when_present_in_roi(string templateName)
    {
        var root = FindSolutionRoot();
        var template = GrayImageCodec.FromFile(
            Path.Combine(root, "resource", "hachimi", "templates", "team_race", templateName));

        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            template!,
            template!,
            [0, 0, template.Width, template.Height],
            threshold: 0.80,
            referenceWidth: template.Width,
            referenceHeight: template.Height);

        Assert.True(result.Found, $"Template score was {result.Score:0.000}.");
        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
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
