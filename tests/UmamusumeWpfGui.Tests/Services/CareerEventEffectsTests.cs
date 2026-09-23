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
        var templates = FindTemplateDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            templates, "runtime_frames", imageName))!;
        var effects = GrayImageCodec.FromFile(Path.Combine(
            templates, "event_effects_button.png"))!;

        Assert.Equal(expected,
            TemplateMatcher.Find(
                frame,
                effects,
                [640, 1200, 230, 100],
                threshold: 0.82,
                referenceWidth: 900,
                referenceHeight: 1600).Found);
    }

    private static string FindTemplateDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var templates = Path.Combine(directory.FullName,
                "resource", "hachimi", "ura", "screens", "templates");
            if (Directory.Exists(templates))
                return templates;
        }

        throw new DirectoryNotFoundException("Could not locate URA templates.");
    }
}
