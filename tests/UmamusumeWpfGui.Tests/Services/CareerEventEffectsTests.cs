using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerEventEffectsTests
{
    [Theory]
    [InlineData("support_event_choice_ura.png", true)]
    [InlineData("trainee_event_choice_ura.png", false)]
    [InlineData("training_event_ura.png", false)]
    [InlineData("rest_event_ura.png", false)]
    public void Effects_button_identifies_a_ready_event_choice(
        string imageName,
        bool expected)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var framePath = imageName == "training_event_ura.png"
            ? CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
                "templates/runtime_frames/training_event_ura.png")
            : CareerTestResourceResolver.FindUraCapture(root, imageName);
        var frame = GrayImageCodec.FromFile(framePath)!;
        var effects = GrayImageCodec.FromFile(
            CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
                "templates/event_effects_button.png"))!;

        Assert.Equal(expected,
            TemplateMatcher.Find(
                frame,
                effects,
                [640, 1200, 230, 100],
                threshold: 0.82,
                referenceWidth: 900,
                referenceHeight: 1600).Found);
    }
}
