using System.IO;
using System.Reflection;
using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSkillLearningFlowTests
{
    [Fact]
    public void Cost_threshold_adds_one_known_lower_tier()
    {
        var catalog = IndependentTrainingCatalog.Load(FindRoot());
        var byName = catalog.Skills
            .GroupBy(skill => skill.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(240, CareerSkillCostResolver.Estimate(
            byName["Right-Handed Demon"], byName));
        Assert.Equal(280, CareerSkillCostResolver.Estimate(
            byName["Concentration"], byName));
        Assert.Equal(200, CareerSkillCostResolver.Estimate(
            byName["Right-Handed ◎"], byName));
        Assert.Equal(90, CareerSkillCostResolver.Estimate(
            byName["Right-Handed ○"], byName));
    }

    [Theory]
    [InlineData("Firm Conditions ○", "Firm Conditions O", true)]
    [InlineData("Firm Conditions ◎", "Firm Conditions O", false)]
    [InlineData("Firm Conditions ○", "Firm Conditions ◎", false)]
    [InlineData("Right-Handed ×", "Right-Handed O", false)]
    [InlineData("Right-Handed ×", "Right-Handed X", true)]
    [InlineData("Presents from X", "Presents from X", true)]
    public void Ocr_name_matching_keeps_skill_tiers_distinct(
        string expected, string observed, bool shouldMatch)
    {
        Assert.Equal(shouldMatch,
            CareerSkillLearningFlow.NameSimilarity(expected, observed) >= 0.84);
    }

    [Fact]
    public async Task Unaffordable_first_target_saves_points_and_opens_race_list()
    {
        var catalog = IndependentTrainingCatalog.Load(FindRoot());
        var expensive = catalog.Skills.Single(skill => skill.SkillName == "Right-Handed Demon").SkillId;
        var cheap = catalog.Skills.Single(skill => skill.SkillName == "Right-Handed ×").SkillId;
        var actions = new RecordingActions();
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", new UraCareerSessionState(),
            new CareerObservation("race_day", 1), null, CancellationToken.None,
            NormalSkillIds: [expensive, cheap]);

        var result = await new CareerRaceFlow(PointsRuntime.Create(74), actions)
            .HandleAsync(context);

        Assert.Null(result);
        Assert.Equal(["open_list"], actions.ActionIds);
    }

    [Fact]
    public async Task Unreadable_skill_points_stop_before_opening_the_race_list()
    {
        var skillId = IndependentTrainingCatalog.Load(FindRoot()).Skills
            .Single(skill => skill.SkillName == "Professor of Curvature").SkillId;
        var actions = new RecordingActions();
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", new UraCareerSessionState(),
            new CareerObservation("race_day", 1), null, CancellationToken.None,
            NormalSkillIds: [skillId]);

        var result = await new CareerRaceFlow(PointsRuntime.Create(-1), actions)
            .HandleAsync(context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Empty(actions.ActionIds);
    }

    [Fact]
    public async Task Cached_learned_skill_skips_points_ocr_and_skills_page()
    {
        var skillId = IndependentTrainingCatalog.Load(FindRoot()).Skills
            .Single(skill => skill.SkillName == "Professor of Curvature").SkillId;
        var actions = new RecordingActions();
        var state = new UraCareerSessionState();
        state.NormalLearnedSkillIds.Add(skillId);
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", state,
            new CareerObservation("race_day", 1), null, CancellationToken.None,
            NormalSkillIds: [skillId]);

        var result = await new CareerRaceFlow(PointsRuntime.Create(-1), actions)
            .HandleAsync(context);

        Assert.Null(result);
        Assert.Equal(["open_list"], actions.ActionIds);
    }

    [Theory]
    [InlineData("career_skill_race_day_662_sample.png", true)]
    [InlineData("career_skill_obtained_sample.png", false)]
    public async Task Existing_career_observer_distinguishes_race_day_from_skills(
        string capture, bool expectedRaceDay)
    {
        var frame = GrayImageCodec.FromFile(Path.Combine(ScreensDirectory(),
            "captures", capture));
        Assert.NotNull(frame);
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindRoot(), "resource", "hachimi", "ura", "manifest.json"));
        var state = new UraCareerSessionState { CareerStarted = true };
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var observation = await new CareerScreenObserver(FrameRuntime.Create(frame!))
            .ObserveAsync(connection, pack, state, false, CancellationToken.None);

        Assert.Equal(expectedRaceDay, observation?.ScreenId == "race_day");
    }

    [Fact]
    public void Skills_back_button_is_absent_on_race_day()
    {
        var directory = ScreensDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(directory,
            "captures", "career_skill_race_day_662_sample.png"));
        var back = GrayImageCodec.FromFile(Path.Combine(directory,
            "templates", "career_skill_back.png"));
        Assert.NotNull(frame);
        Assert.NotNull(back);

        var match = TemplateMatcher.FindColor(frame!, back!,
            [0, 1480, 185, 110], 0.8, 900, 1600);
        Assert.False(match.Found, $"Skills Back falsely matched Race Day "
            + $"at ({match.CenterX},{match.CenterY}) with score {match.Score:0.000}");
    }

    [Theory]
    [InlineData("career_skill_learn_sample.png", 685, 650, 95, 80, 72)]
    [InlineData("career_skill_race_day_sample.png", 750, 1140, 115, 75, 74)]
    [InlineData("career_skill_race_day_662_sample.png", 750, 1140, 115, 75, 662)]
    public async Task Captured_skill_points_and_prices_are_readable(
        string capture, int x, int y, int width, int height, int expected)
    {
        var result = await OcrCropAsync(capture, 0, 0, 900, 1600);
        Assert.True(CareerSkillLearningFlow.ParseNumberInRegion(
                result, [x, y, width, height]) == expected,
            $"Expected {expected}; OCR: " + string.Join(" | ",
                result.Detections.Select(d => $"{d.Text}@{d.Bounds}")));
    }

    [Fact]
    public async Task Captured_list_title_and_confirmation_name_are_readable()
    {
        var list = await OcrCropAsync("career_skill_learn_sample.png",
            155, 590, 475, 690);
        Assert.Contains(list.Detections, detection =>
            CareerSkillLearningFlow.NameSimilarity("Firm Conditions ○", detection.Text) >= 0.84);

        var confirmation = await OcrCropAsync("career_skill_confirmation_sample.png",
            185, 150, 660, 130);
        Assert.Contains(confirmation.Detections, detection =>
            CareerSkillLearningFlow.NameSimilarity(
                "Where There's a Will, There's a Way", detection.Text) >= 0.84);
    }

    [Fact]
    public async Task Captured_obtained_marker_is_readable_in_its_row()
    {
        var status = await OcrCropAsync("career_skill_obtained_sample.png",
            625, 845, 220, 155);
        Assert.Contains(status.Detections, detection =>
            detection.Text.Contains("Obtained", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("career_skill_learn_sample.png", "career_skill_plus.png", 775, 610, 85, 150)]
    [InlineData("career_skill_learn_sample.png", "career_skill_back.png", 0, 1480, 185, 110)]
    [InlineData("career_skill_selected_sample.png", "career_skill_confirm.png", 345, 1310, 210, 80)]
    [InlineData("career_skill_confirmation_sample.png", "career_skill_confirmation_title.png", 290, 25, 320, 105)]
    [InlineData("career_skill_confirmation_sample.png", "career_skill_confirmation_learn.png", 455, 1410, 385, 130)]
    [InlineData("career_skill_confirmation_sample.png", "career_skill_confirmation_cancel.png", 60, 1410, 385, 130)]
    [InlineData("career_skill_learned_sample.png", "career_skill_learned_title.png", 290, 455, 320, 110)]
    [InlineData("career_skill_learned_sample.png", "career_skill_learned_close.png", 250, 975, 390, 130)]
    [InlineData("career_skill_exit_confirmation_sample.png", "career_skill_exit_title.png", 245, 730, 420, 100)]
    [InlineData("career_skill_exit_confirmation_sample.png", "career_skill_exit_ok.png", 455, 970, 385, 145)]
    public void Captured_button_templates_match_their_pages(
        string capture, string template, int x, int y, int width, int height)
    {
        var directory = ScreensDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(directory, "captures", capture));
        var button = GrayImageCodec.FromFile(Path.Combine(directory, "templates", template));
        Assert.NotNull(frame);
        Assert.NotNull(button);

        var match = TemplateMatcher.FindColor(frame!, button!,
            [x, y, width, height], 0.82, 900, 1600);
        Assert.True(match.Found, $"{template} score {match.Score:0.000}");
        var expectedCenter = template switch
        {
            "career_skill_plus.png" => (812, 685),
            "career_skill_back.png" => (95, 1541),
            "career_skill_confirm.png" => (450, 1350),
            "career_skill_confirmation_title.png" => (450, 78),
            "career_skill_confirmation_cancel.png" => (250, 1477),
            "career_skill_confirmation_learn.png" => (645, 1477),
            "career_skill_learned_title.png" => (450, 515),
            "career_skill_learned_close.png" => (450, 1043),
            "career_skill_exit_title.png" => (450, 773),
            "career_skill_exit_ok.png" => (645, 1044),
            _ => throw new InvalidOperationException(template),
        };
        Assert.InRange(Math.Abs(match.CenterX - expectedCenter.Item1), 0, 25);
        Assert.InRange(Math.Abs(match.CenterY - expectedCenter.Item2), 0, 25);
    }

    [Theory]
    [InlineData("career_skill_race_day_sample.png")]
    [InlineData("career_skill_race_day_662_sample.png")]
    [InlineData("career_skill_race_day_animation_sample.png")]
    public void Race_day_skills_text_matches_without_button_background(string capture)
    {
        var directory = ScreensDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(directory, "captures", capture));
        var textTemplate = GrayImageCodec.FromFile(Path.Combine(directory,
            "templates", "race_day_skills_open.png"));
        Assert.NotNull(frame);
        Assert.NotNull(textTemplate);
        var match = TemplateMatcher.FindColor(frame!, textTemplate!,
            [80, 1250, 350, 180], 0.78, 900, 1600);
        Assert.True(match.Found, $"{capture} score {match.Score:0.000}");
        Assert.InRange(match.CenterX, 210, 320);
        Assert.InRange(match.CenterY, 1350, 1410);
    }

    [Theory]
    [InlineData("career_skill_learn_sample.png", "career_skill_confirm.png", 345, 1310, 210, 80)]
    [InlineData("career_skill_selected_sample.png", "career_skill_plus.png", 775, 1120, 85, 155)]
    public void Text_templates_do_not_match_when_action_is_unavailable(
        string capture, string template, int x, int y, int width, int height)
    {
        var directory = ScreensDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(directory, "captures", capture));
        var textTemplate = GrayImageCodec.FromFile(Path.Combine(directory, "templates", template));
        Assert.NotNull(frame);
        Assert.NotNull(textTemplate);
        var match = TemplateMatcher.FindColor(frame!, textTemplate!,
            [x, y, width, height], 0.82, 900, 1600);
        Assert.False(match.Found, $"{template} incorrectly matched at "
            + $"({match.CenterX},{match.CenterY}) with score {match.Score:0.000}");
    }

    [Fact]
    public void Race_day_marker_matches_after_skill_purchase()
    {
        var directory = ScreensDirectory();
        var frame = GrayImageCodec.FromFile(Path.Combine(directory,
            "captures", "career_skill_race_day_sample.png"));
        var marker = GrayImageCodec.FromFile(Path.Combine(directory,
            "templates", "runtime_frames", "race_day_race_button_text.png"));
        Assert.NotNull(frame);
        Assert.NotNull(marker);
        var match = TemplateMatcher.FindColor(frame!, marker!,
            [550, 1340, 190, 105], 0.78, 900, 1600);
        Assert.True(match.Found, $"Race Day marker score {match.Score:0.000}");
    }

    private static async Task<ScreenTextRecognitionResult> OcrCropAsync(
        string capture, int x, int y, int width, int height)
    {
        using var image = Image.Load<Rgba32>(Path.Combine(ScreensDirectory(),
            "captures", capture));
        image.Mutate(context => context.Crop(new Rectangle(x, y, width, height))
            .Resize(width * (width > 400 ? 1 : 3),
                height * (width > 400 ? 1 : 3), KnownResamplers.Lanczos3));
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(image.Width, image.Height, bytes), "en-US");
    }

    private static string ScreensDirectory() => Path.Combine(FindRoot(),
        "resource", "hachimi", "ura", "screens");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "uma", "database", "global", "skills.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> ActionIds { get; } = [];

        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context,
            string screenId, string actionId, HachimiPipelineRunOptions? options = null)
        {
            ActionIds.Add(actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class PointsRuntime : DispatchProxy
    {
        private int _points;

        public static IVisualPipelineRuntime Create(int points)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, PointsRuntime>();
            ((PointsRuntime)(object)runtime)._points = points;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "DetectTextAsync" => Task.FromResult<ScreenTextRecognitionResult?>(
                    _points < 0 ? null : new ScreenTextRecognitionResult(
                        [new ScreenTextDetection(_points.ToString(CultureInfo.InvariantCulture),
                            new ScreenTextRect(780, 1160, 55, 30))],
                        "en-US", 900, 1600)),
                _ => throw new InvalidOperationException(
                    $"Unexpected visual operation: {targetMethod?.Name}"),
            };
    }

    public class FrameRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, FrameRuntime>();
            ((FrameRuntime)(object)runtime)._frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(_frame),
                "LoadTemplateAsync" => Task.FromResult(GrayImageCodec.FromFile(
                    Path.Combine((string)args![1]!, (string)args[0]!))),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(
                    $"Unexpected visual operation: {targetMethod?.Name}"),
            };
    }
}
