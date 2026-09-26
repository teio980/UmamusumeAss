using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerGoalResumeTests
{
    [Fact]
    public async Task Resuming_on_goal_update_clicks_the_second_next()
    {
        var root = FindWorkspaceRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(root,
            "testdata", "hachimi", "ura", "captures", "goal3_update.png"));
        Assert.NotNull(frame);

        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var visual = ResumeVisualRuntime.Create(frame, cancellation);
        var runtime = (ResumeVisualRuntime)(object)visual;
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
        var engine = new CareerTrainingEngine(visual, database, navigator, runner);
        var settings = new CareerTrainingSettings(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            100602,
            ContinueExistingCareer: true,
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
            LegacyAptitudeSparks: []);
        var connection = new LastVerifiedConnection("adb", "serial", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

        await engine.RunAsync(connection, settings, logSink: null,
            cancellationToken: cancellation.Token);

        Assert.Equal("goal_update_goal_update_next", runtime.TappedTask);
        Assert.Equal(2, runtime.Captures);
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
        public string? TappedTask { get; private set; }

        public static IVisualPipelineRuntime Create(
            GrayImage frame,
            CancellationTokenSource cancellation)
        {
            var visual = DispatchProxy.Create<IVisualPipelineRuntime, ResumeVisualRuntime>();
            var runtime = (ResumeVisualRuntime)(object)visual;
            runtime.Frame = frame;
            runtime.Cancellation = cancellation;
            return visual;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    Captures++;
                    return Task.FromResult<GrayImage?>(Frame);
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(
                        Path.Combine((string)args![1]!, (string)args[0]!)));
                case "DelayAsync":
                    return Task.CompletedTask;
                case "WaitForMatchAsync":
                    Assert.Equal("goal_update_goal_update_next", args![8]);
                    return Task.FromResult<TemplateMatchResult?>(
                        new TemplateMatchResult(true, 1, 300, 1350, 300, 90));
                case "TapMatchAsync":
                    TappedTask = (string)args![2]!;
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
