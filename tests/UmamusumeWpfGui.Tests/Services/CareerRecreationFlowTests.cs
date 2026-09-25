using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRecreationFlowTests
{
    [Theory]
    [InlineData("AWFUL", CareerMood.Awful)]
    [InlineData("BAD", CareerMood.Bad)]
    [InlineData("NORMAL", CareerMood.Normal)]
    [InlineData("GOOD", CareerMood.Good)]
    [InlineData("(4R GREAT)", CareerMood.Great)]
    public void Reads_all_five_mood_labels(string text, CareerMood expected) =>
        Assert.Equal(expected, CareerMoodParser.Parse(text));

    [Theory]
    [InlineData(CareerMood.Awful, UraPlannedAction.Recreation)]
    [InlineData(CareerMood.Bad, UraPlannedAction.Recreation)]
    [InlineData(CareerMood.Normal, UraPlannedAction.Recreation)]
    [InlineData(CareerMood.Good, UraPlannedAction.Training)]
    [InlineData(CareerMood.Great, UraPlannedAction.Training)]
    public async Task Recreation_is_selected_at_normal_or_lower(
        CareerMood mood,
        UraPlannedAction expected)
    {
        var pack = await LoadPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();
        state.Energy = UraObservedValueFactory.FromObservation(75, 0.98);
        state.Mood = UraObservedValueFactory.FromObservation(mood, 0.98);

        Assert.Equal(expected,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);
    }

    [Fact]
    public async Task Recreation_has_a_three_step_action_flow()
    {
        var pack = await LoadPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();
        scenario.ObserveScreen(state, "career_main", 0.98,
            energyPercent: 75, energyConfidence: 0.98,
            moodText: "BAD");
        Assert.Equal(CareerMood.Bad, state.Mood.Value);
        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);

        foreach (var (screen, action) in new[]
                 {
                     ("career_main", "recreation"),
                     ("recreation_selection", "trainee"),
                     ("recreation_confirmation", "confirm"),
                 })
        {
            var context = new CareerFlowContext(null!, pack, true, scenario,
                new UraDefaultStrategy(), string.Empty, state,
                new CareerObservation(screen, 0.98, EnergyPercent: 75),
                null, CancellationToken.None);
            Assert.Null(await flow.HandleAsync(context));
            Assert.Equal((screen, action), actions.LastAction);
        }

        Assert.Equal(UraPlannedAction.Recreation, state.LastAction);
        Assert.True(state.AwaitingRecreationConfirmationGone);
    }

    [Fact]
    public async Task Windows_ocr_reads_the_captured_mood_badge()
    {
        using var image = Image.Load<Rgba32>(Path.Combine(
            ScreensDirectory(), "captures", "recreation_main.png"));
        image.Mutate(context => context.Crop(new Rectangle(654, 169, 170, 68)));
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        var recognized = await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(image.Width, image.Height, bytes), "en-US");

        Assert.Contains(recognized.Detections,
            item => CareerMoodParser.Parse(item.Text) == CareerMood.Great);
    }

    [Theory]
    [InlineData("career_main_action_recreation", "recreation_main.png", true)]
    [InlineData("career_main_action_recreation", "recreation_selection.png", false)]
    [InlineData("career_main_action_recreation", "summer_rest_current.png", false)]
    [InlineData("recreation_selection_trainee", "recreation_selection.png", true)]
    [InlineData("recreation_selection_trainee", "recreation_confirmation.png", false)]
    [InlineData("recreation_confirmation_ok", "recreation_confirmation.png", true)]
    [InlineData("recreation_confirmation_ok", "recreation_selection.png", false)]
    public async Task Transparent_text_template_matches_its_live_step(
        string taskName,
        string captureName,
        bool expected)
    {
        var pack = await LoadPackAsync();
        Assert.True(pack.ExecutionDefinition.TryGetTask(taskName, out var task));
        Assert.NotNull(task);
        var screens = ScreensDirectory();
        var template = Load(Path.Combine(screens,
            task.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var frame = Load(Path.Combine(screens, "captures", captureName));

        Assert.Contains(template.RgbaPixels!.Chunk(4), pixel => pixel[3] == 0);
        var match = TemplateMatcher.FindColor(
            frame,
            template,
            task.Roi,
            task.TemplateThreshold,
            900,
            1600,
            requireTextContrast: true);
        Assert.True(match.Found == expected,
            $"{taskName} on {captureName}: expected={expected}, score={match.Score:0.000}, "
            + $"position=({match.X},{match.Y}).");
    }

    [Theory]
    [InlineData("recreation_selection", "recreation_selection.png", true)]
    [InlineData("recreation_selection", "recreation_confirmation.png", false)]
    [InlineData("recreation_confirmation", "recreation_confirmation.png", true)]
    [InlineData("recreation_confirmation", "recreation_selection.png", false)]
    public async Task Recreation_dialogs_are_distinct(
        string screenId,
        string captureName,
        bool expected)
    {
        var pack = await LoadPackAsync();
        var screen = pack.ScreenProfile.Find(screenId);
        Assert.NotNull(screen);
        var screens = ScreensDirectory();
        var template = Load(Path.Combine(screens,
            screen.Recognition.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var frame = Load(Path.Combine(screens, "captures", captureName));

        var match = TemplateMatcher.FindColor(frame, template,
            screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
            900, 1600, requireTextContrast: true);
        Assert.True(match.Found == expected,
            $"{screenId} on {captureName}: expected={expected}, score={match.Score:0.000}, "
            + $"position=({match.X},{match.Y}).");
    }

    [Fact]
    public async Task Existing_event_choice_screen_handles_the_recreation_result()
    {
        var pack = await LoadPackAsync();
        var screen = pack.ScreenProfile.Find("event_choice");
        Assert.NotNull(screen);
        var screens = ScreensDirectory();
        var template = Load(Path.Combine(screens,
            screen.Recognition.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var frame = Load(Path.Combine(screens,
            "captures", "recreation_event_choice.png"));

        var match = TemplateMatcher.Find(frame, template,
            screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
            900, 1600);
        Assert.True(match.Found, $"Event choice score={match.Score:0.000}.");
    }

    private static GrayImage Load(string path) =>
        GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing test image", path);

    private static async Task<UraScenarioPack> LoadPackAsync() =>
        await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));

    private static string ScreensDirectory() => Path.Combine(
        FindWorkspaceRoot(), "resource", "hachimi", "ura", "screens");

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

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public (string ScreenId, string ActionId)? LastAction { get; private set; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            LastAction = (screenId, actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }
}
