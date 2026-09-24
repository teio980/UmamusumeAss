using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraTrainingSelectionHeightDetectorTests
{
    [Theory]
    [InlineData("turn3_training_selection.png")]
    [InlineData("training_selection_partner_ura.png")]
    [InlineData("year2_nhk_training_select.png")]
    public async Task Raised_speed_is_found_across_training_screen_variants(string captureName)
    {
        var root = FindWorkspaceRoot();
        var screens = Path.Combine(root, "resource", "hachimi", "ura", "screens");
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var screenshot = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "ura", "captures",
            captureName));
        Assert.NotNull(screenshot);

        var templates = UraTrainingTypeCatalog.SupportedTypes.ToDictionary(
            type => type,
            type => GrayImageCodec.FromFile(Path.Combine(
                screens,
                pack.ExecutionDefinition.GetTask($"training_selection_training_{type}")
                    .Template!))!);

        if (captureName == "turn3_training_selection.png")
        {
            var flatSpeedTask = pack.ExecutionDefinition.GetTask(
                "training_selection_training_speed");
            var misleadingFlatMatch = TemplateMatcher.FindColor(
                screenshot!,
                templates["speed"],
                flatSpeedTask.Roi,
                flatSpeedTask.TemplateThreshold,
                pack.ExecutionDefinition.ReferenceWidth,
                pack.ExecutionDefinition.ReferenceHeight);
            Assert.True(misleadingFlatMatch.Found);
        }

        var result = UraTrainingSelectionHeightDetector.CompareHeights(
            screenshot!, pack.ExecutionDefinition, templates);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("speed", result.RaisedType);
        Assert.Equal(5, result.Matches.Count);
        var speedY = result.Matches.Single(item => item.TrainingType == "speed")
            .Match.CenterY;
        Assert.All(
            result.Matches.Where(item => item.TrainingType != "speed"),
            item => Assert.True(item.Match.CenterY > speedY));
    }

    [Fact]
    public void Highest_logo_is_selected_even_when_only_one_pixel_above_the_others()
    {
        var matches = UraTrainingTypeCatalog.SupportedTypes
            .Select((type, index) => new UraTrainingLogoMatch(
                type,
                new TemplateMatchResult(true, 0.98, index * 100,
                    type == "speed" ? 1259 : 1260, 82, 52)))
            .ToArray();

        var result = UraTrainingSelectionHeightDetector.SelectHighest(matches);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("speed", result.RaisedType);
    }

    [Theory]
    [InlineData("turn3_training_selection.png", true)]
    [InlineData("support_event_choice_ura_2.png", false)]
    public async Task Same_frame_header_check_rejects_a_changed_screen(
        string captureName,
        bool expected)
    {
        var root = FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var recognition = pack.ScreenProfile.Find("training_selection")!.Recognition;
        var header = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "ura", "screens", recognition.Template!));
        var screenshot = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "ura", "captures", captureName));

        Assert.NotNull(header);
        Assert.NotNull(screenshot);
        Assert.Equal(expected, UraTrainingSelectionHeightDetector.IsTrainingSelectionScreen(
            screenshot!, header!, recognition, pack.ScreenProfile));
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName, "resource", "hachimi", "ura", "manifest.json"))
                && File.Exists(Path.Combine(
                    directory.FullName, "testdata", "hachimi", "ura", "captures",
                    "turn3_training_selection.png")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find workspace root.");
    }
}
