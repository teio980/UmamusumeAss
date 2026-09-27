using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceFinalNextPositionTests
{
    [Theory]
    [InlineData("ura_qualifier_reward_centered_next.png", 430, 470)]
    [InlineData("ura_tenno_reward.png", 620, 660)]
    public async Task Final_next_matches_reward_buttons_in_both_positions(
        string frameName,
        int minimumCenterX,
        int maximumCenterX)
    {
        var root = FindWorkspaceRoot();
        var screens = Path.Combine(root, "resource", "hachimi", "ura", "screens");
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var task = pack.ExecutionDefinition.GetTask("race_runner_last_next");
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "ura", "captures", frameName));
        var template = GrayImageCodec.FromFile(Path.Combine(screens,
            task.Template!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(frame!, template!, task.Roi,
            task.TemplateThreshold, 900, 1600);
        Assert.True(match.Found,
            $"{frameName}: score={match.Score:0.000}, x={match.X}, y={match.Y}.");
        Assert.InRange(match.CenterX, minimumCenterX, maximumCenterX);
        Assert.InRange(match.CenterY, 1470, 1540);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
}
