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
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var task = pack.ExecutionDefinition.GetTask("race_runner_last_next");
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, frameName));
        var template = GrayImageCodec.FromFile(
            pack.VisualResources!.ResolveTaskTemplate("race_runner_last_next"));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(frame!, template!, task.Roi,
            task.TemplateThreshold, 900, 1600);
        Assert.True(match.Found,
            $"{frameName}: score={match.Score:0.000}, x={match.X}, y={match.Y}.");
        Assert.InRange(match.CenterX, minimumCenterX, maximumCenterX);
        Assert.InRange(match.CenterY, 1470, 1540);
    }

}
