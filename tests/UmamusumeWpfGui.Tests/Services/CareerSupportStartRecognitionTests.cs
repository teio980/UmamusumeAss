using UmamusumeWpfGui.Services.Tasks;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSupportStartRecognitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("support_select_support_start", "support_autofilled_ura.png", true)]
    [InlineData("support_ready_support_start", "support_autofilled_ura.png", true)]
    [InlineData("support_select_support_start", "support_select_ura.png", false)]
    [InlineData("support_ready_support_start", "support_select_ura.png", false)]
    public async Task Cropped_start_label_matches_only_an_enabled_button(
        string taskName, string captureName, bool enabled)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var task = pack.ExecutionDefinition.GetTask(taskName);
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), captureName));
        var template = GrayImageCodec.FromFile(pack.VisualResources!.ResolveTaskTemplate(taskName));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        // Keep the character and surrounding pavement outside the recognition asset.
        Assert.InRange(template.Width, 190, 220);
        Assert.InRange(template.Height, 30, 55);
        Assert.Equal("MatchTemplateColor", task.Algorithm);
        var match = TemplateMatcher.FindColor(frame, template, task.Roi,
            task.TemplateThreshold, 900, 1600);
        output.WriteLine($"{taskName} on {captureName}: score={match.Score:0.000}, "
            + $"click=({match.CenterX},{match.CenterY}).");
        Assert.Equal(enabled, match.Found);
        if (enabled)
        {
            Assert.InRange(match.CenterX, 470, 520);
            Assert.InRange(match.CenterY, 1320, 1370);
        }
    }
}
