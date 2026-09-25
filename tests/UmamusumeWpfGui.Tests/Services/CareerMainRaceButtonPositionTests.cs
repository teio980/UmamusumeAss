using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerMainRaceButtonPositionTests
{
    [Theory]
    [InlineData("infirmary_current.png", "career_main_action_races", 600, 650)]
    [InlineData("infirmary_current.png", "career_main_action_finale_races", 600, 650)]
    [InlineData("race_shifted_current.png", "career_main_action_races", 480, 530)]
    [InlineData("race_shifted_current.png", "career_main_action_finale_races", 480, 530)]
    public async Task Career_main_race_button_matches_both_positions(
        string frameName,
        string taskName,
        int minimumX,
        int maximumX)
    {
        var root = FindWorkspaceRoot();
        var screens = Path.Combine(root, "resource", "hachimi", "ura", "screens");
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        Assert.True(pack.ExecutionDefinition.TryGetTask(taskName, out var task));
        Assert.NotNull(task);

        var frame = GrayImageCodec.FromFile(Path.Combine(screens, "captures", frameName));
        var template = GrayImageCodec.FromFile(Path.Combine(screens,
            task.Template!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(frame!, template!, task.Roi,
            task.TemplateThreshold, 900, 1600);
        Assert.True(match.Found,
            $"{taskName} on {frameName}: score={match.Score:0.000}, x={match.X}, y={match.Y}.");
        Assert.InRange(match.X, minimumX, maximumX);
        Assert.InRange(match.Y, 1410, 1440);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
}
