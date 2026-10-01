using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerGoalResumeTests
{
    [Theory]
    [InlineData("goal3_update.png", "goal_update_goal_update_next")]
    [InlineData("current_mid_year1.png", "goal_complete_goal_next")]
    [InlineData("ura_finale_entry.png", "goal_complete_goal_next")]
    public async Task Resuming_on_a_goal_page_clicks_its_next(
        string captureName,
        string expectedTask)
    {
        var root = FindWorkspaceRoot();
        var frame = LoadGoalFrame(root, captureName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var visual = ResumeVisualRuntime.Create(frame, cancellation, expectedTask);
        var runtime = (ResumeVisualRuntime)(object)visual;

        await RunEngineAsync(root, visual, cancellation.Token);

        Assert.Equal([expectedTask], runtime.TappedTasks);
        Assert.Equal(2, runtime.Captures);
    }

    [Theory]
    [InlineData("current_mid_year1.png", "goal_complete_goal_next", false, 0)]
    [InlineData("goal3_update.png", "goal_update_goal_update_next", false, 0)]
    [InlineData("ura_finale_entry.png", "goal_complete_goal_next", false, 0)]
    [InlineData("current_mid_year1.png", "goal_complete_goal_next", true, 0)]
    [InlineData("current_mid_year1.png", "goal_complete_goal_next", false, 4)]
    public async Task Home_resume_hands_the_goal_page_to_the_turn_engine(
        string captureName,
        string expectedTask,
        bool mainBeforeGoal,
        int missingCaptures)
    {
        var root = FindWorkspaceRoot();
        var frame = LoadGoalFrame(root, captureName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var visual = ResumeVisualRuntime.Create(frame, cancellation, expectedTask);
        var runtime = (ResumeVisualRuntime)(object)visual;
        runtime.EntryFrame = GrayImageCodec.FromFile(Path.Combine(root,
            "testdata", "hachimi", "ura", "captures", "home.jpg"));
        Assert.NotNull(runtime.EntryFrame);
        runtime.ContinueFrame = CreateContinueFrame(root);
        runtime.MissingCapturesRemaining = missingCaptures;
        if (mainBeforeGoal)
        {
            runtime.MainFrame = GrayImageCodec.FromFile(Path.Combine(root,
                "testdata", "hachimi", "ura", "captures", "goal3_turn8_main.png"));
            Assert.NotNull(runtime.MainFrame);
            runtime.MainCapturesRemaining = 2;
        }

        await RunEngineAsync(root, visual, cancellation.Token);

        Assert.Equal(
            ["home", "home_home_career", "career_continue_resume", expectedTask],
            runtime.TappedTasks);
    }

    [Fact]
    public async Task Home_resume_pauses_without_clicking_when_no_runtime_screen_is_recognized()
    {
        var root = FindWorkspaceRoot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var visual = ResumeVisualRuntime.Create(
            LoadGoalFrame(root, "current_mid_year1.png"), cancellation, "goal_complete_goal_next");
        var runtime = (ResumeVisualRuntime)(object)visual;
        runtime.EntryFrame = GrayImageCodec.FromFile(Path.Combine(root,
            "testdata", "hachimi", "ura", "captures", "home.jpg"));
        Assert.NotNull(runtime.EntryFrame);
        runtime.ContinueFrame = CreateContinueFrame(root);
        runtime.MissingCapturesRemaining = 100;

        var result = await RunEngineAsync(root, visual, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("career_resume_transition", result.LastScreenId);
        Assert.Equal("Could not recognize a stable Career entry screen.", result.Message);
        Assert.Equal(["home", "home_home_career", "career_continue_resume"], runtime.TappedTasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Daily_reset_after_learning_gourmand_keeps_the_skill_and_resumes_the_same_career_from_home(bool raceDay)
    {
        var root = FindWorkspaceRoot();
        var expectedTask = raceDay ? "race_day_race_open_list" : "goal_update_goal_update_next";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var frame = raceDay
            ? GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi", "ura", "screens", "captures", "career_skill_race_day_662_sample.png"))!
            : LoadGoalFrame(root, "goal3_update.png");
        var visual = ResumeVisualRuntime.Create(frame, cancellation, expectedTask);
        var runtime = (ResumeVisualRuntime)(object)visual;
        runtime.EntryFrame = GrayImageCodec.FromFile(Path.Combine(root, "testdata", "hachimi", "ura", "captures", "home.jpg"));
        runtime.ContinueFrame = CreateContinueFrame(root);
        var fixture = new DateChangedDialogRecoveryTests.Fixture();
        var connection = new LastVerifiedConnection("adb", "date-changed-test", Guid.NewGuid().ToString("N"), "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "date-changed-skills-" + Guid.NewGuid().ToString("N"));
        var cache = new NormalCareerSkillCache(connection, 100602, cacheDirectory);
        runtime.BeforeNextCapture = async () =>
        {
            // The original run started with Restart selected and learned this
            // skill before Skills Back was covered by the Date Changed modal.
            await cache.SaveAsync([201351]);
            throw new DateChangedInterruptionException();
        };
        try
        {
            var result = await RunEngineAsync(root, visual, cancellation.Token, fixture.Recovery,
                continueExistingCareer: false, connection, cacheDirectory, normalSkillIds: [201351]);
            Assert.False(result.Succeeded); // The fixture stops after the next goal action.
            Assert.Equal("canceled", result.LastScreenId);
            Assert.Equal(["ok", "launch", "startup"], fixture.Events);
            Assert.Equal(["home", "home_home_career", "career_continue_resume", expectedTask], runtime.TappedTasks);
            Assert.Equal([201351], await cache.LoadAsync());
            Assert.Null(GameAutomationScope.Current);
        }
        finally
        {
            await cache.ClearAsync();
            if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory);
        }
    }

    private static GrayImage LoadGoalFrame(string root, string captureName)
    {
        var path = captureName == "ura_finale_entry.png"
            ? Path.Combine(root, "resource", "hachimi", "ura", "screens",
                "templates", "runtime_frames", captureName)
            : Path.Combine(root, "testdata", "hachimi", "ura", "captures", captureName);
        return GrayImageCodec.FromFile(path)
            ?? throw new InvalidDataException($"Could not load {path}.");
    }

    private static GrayImage CreateContinueFrame(string root)
    {
        var header = GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi",
            "ura", "screens", "templates", "career_continue_header.png"));
        Assert.NotNull(header);
        var pixels = new byte[900 * 1600];
        for (var row = 0; row < header.Height; row++)
            Buffer.BlockCopy(header.Pixels, row * header.Width,
                pixels, (360 + row) * 900, header.Width);
        return new GrayImage(900, 1600, pixels);
    }

    private static async Task<CareerTrainingResult> RunEngineAsync(
        string root,
        IVisualPipelineRuntime visual,
        CancellationToken cancellationToken,
        DateChangedDialogRecovery? recovery = null,
        bool continueExistingCareer = true,
        LastVerifiedConnection? connection = null,
        string? cacheDirectory = null,
        IReadOnlyList<int>? normalSkillIds = null)
    {
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"), cancellationToken);
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(),
            visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"career-goal-resume-{Guid.NewGuid():N}.json")));
        var navigator = new CareerEntryNavigator(
            visual,
            database,
            new UraTraineeSelector(visual, database),
            new UraLegacySelector(visual, runner),
            new CareerJsonActionExecutor(runner));
        var engine = new CareerTrainingEngine(visual, database, navigator, runner, recovery,
            (device, traineeId) => new NormalCareerSkillCache(device, traineeId, cacheDirectory));
        var settings = new CareerTrainingSettings(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            100602,
            ContinueExistingCareer: continueExistingCareer,
            SupportCardIds: [],
            SupportDeckMode: "auto",
            SupportDeckPreset: "",
            FriendSupportCardId: null,
            StrategyId: "default-speed-medium",
            PauseOnUnknownOutcome: true,
            AllowOptionalRaces: false,
            LegacySelectionMode: "",
            UseLegacyGuest: false,
            UseCachedLegacy: false,
            LegacyAttributeSparks: [],
            LegacyAptitudeSparks: [],
            NormalSkillIds: normalSkillIds);
        connection ??= new LastVerifiedConnection("adb", "serial", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

        return await engine.RunAsync(connection, settings, logSink: null,
            cancellationToken: cancellationToken);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "hachimi", "ura", "manifest.json"))
                && File.Exists(Path.Combine(directory.FullName,
                    "testdata", "hachimi", "ura", "captures", "goal3_update.png")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }

    public class ResumeVisualRuntime : DispatchProxy
    {
        public GrayImage Frame { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; set; } = null!;
        public int Captures { get; private set; }
        public List<string> TappedTasks { get; } = [];
        public string ExpectedTask { get; set; } = null!;
        public GrayImage? EntryFrame { get; set; }
        public GrayImage? ContinueFrame { get; set; }
        public GrayImage? MainFrame { get; set; }
        public int MainCapturesRemaining { get; set; }
        public int MissingCapturesRemaining { get; set; }
        public Func<Task<GrayImage?>>? BeforeNextCapture { get; set; }

        public static IVisualPipelineRuntime Create(
            GrayImage frame,
            CancellationTokenSource cancellation,
            string expectedTask)
        {
            var visual = DispatchProxy.Create<IVisualPipelineRuntime, ResumeVisualRuntime>();
            var runtime = (ResumeVisualRuntime)(object)visual;
            runtime.Frame = frame;
            runtime.Cancellation = cancellation;
            runtime.ExpectedTask = expectedTask;
            return visual;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    Captures++;
                    if (BeforeNextCapture is { } before)
                    {
                        BeforeNextCapture = null;
                        return before();
                    }
                    if (EntryFrame is not null)
                        return Task.FromResult<GrayImage?>(EntryFrame);
                    if (MissingCapturesRemaining > 0)
                    {
                        MissingCapturesRemaining--;
                        return Task.FromResult<GrayImage?>(null);
                    }
                    if (MainCapturesRemaining > 0)
                    {
                        MainCapturesRemaining--;
                        return Task.FromResult(MainFrame);
                    }
                    return Task.FromResult<GrayImage?>(Frame);
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(
                        Path.Combine((string)args![1]!, (string)args[0]!)));
                case "DelayAsync":
                    return Task.CompletedTask;
                case "DetectTextAsync":
                    return Task.FromResult<ScreenTextRecognitionResult?>(null);
                case "WaitForMatchAsync":
                case "WaitForColorMatchAsync":
                    Assert.Contains((string)args![8]!,
                        new[] { "home", "home_home_career", "career_continue_resume", ExpectedTask });
                    return Task.FromResult<TemplateMatchResult?>(
                        new TemplateMatchResult(true, 1, 300, 1350, 300, 90));
                case "TapMatchAsync":
                    var task = (string)args![2]!;
                    TappedTasks.Add(task);
                    if (task == "home")
                        return Task.CompletedTask;
                    if (task == "home_home_career")
                    {
                        EntryFrame = ContinueFrame;
                        return Task.CompletedTask;
                    }
                    if (task == "career_continue_resume")
                    {
                        EntryFrame = null;
                        return Task.CompletedTask;
                    }
                    Assert.Equal(ExpectedTask, task);
                    Cancellation.Cancel();
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected visual runtime call: {targetMethod?.Name}.");
            }
        }
    }

    public class UnexpectedAdbRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected ADB call: {targetMethod?.Name}.");
    }
}
