using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerCompleteRecognitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Both_completion_buttons_are_recognized_and_clickable(bool close, bool resume)
    {
        var pack = await LoadPackAsync();
        var frame = LoadCompletionFrame(close);
        var expectedScreenId = close ? "career_complete_close" : "career_complete";
        var observer = new CareerScreenObserver(CapturedFrameRuntime.Create(frame));
        var state = new UraCareerSessionState
        {
            CareerStarted = !resume,
            LastScreenId = resume ? "unknown" : "follow_trainer_limit",
        };

        var observation = await observer.ObserveAsync(
            Connection(), pack, state, false, CancellationToken.None, careerOnly: resume);

        Assert.Equal(expectedScreenId, observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Settlement, observation?.Kind);
        var screen = pack.ScreenProfile.Find(expectedScreenId)!;
        var task = pack.ExecutionDefinition.GetTask(screen.FindAction(close ? "close" : "to_home")!.Task);
        var template = LoadResource(task.Template!);
        var match = TemplateMatcher.FindColor(frame, template, task.Roi,
            task.TemplateThreshold, 900, 1600,
            requireTextContrast: task.Algorithm == "MatchTemplateColorText");
        output.WriteLine($"{expectedScreenId}: title={observation!.Score:0.000}, button={match.Score:0.000}, click=({match.CenterX},{match.CenterY}), size={template.Width}x{template.Height}");
        Assert.True(match.Found);
        Assert.InRange(match.CenterX, 230, 275);
        Assert.InRange(match.CenterY, 1020, 1065);
        if (close)
        {
            Assert.InRange(template.Width, 80, 110);
            Assert.InRange(template.Height, 25, 45);
        }

        state.CareerStarted = true;
        state.LastScreenId = expectedScreenId;
        Assert.True(CareerScreenObserver.IsReturningHome(state));
        Assert.Equal(expectedScreenId, (await observer.ObserveAsync(
            Connection(), pack, state, false, CancellationToken.None))?.ScreenId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Completion_requires_both_its_title_and_exit_button(bool close, bool removeTitle)
    {
        var pack = await LoadPackAsync();
        pack = pack with
        {
            ScreenProfile = new UraScreenProfile
            {
                Screens = pack.ScreenProfile.Screens
                    .Where(screen => screen.ScreenId is "career_complete" or "career_complete_close")
                    .ToList(),
            },
        };
        var frame = LoadCompletionFrame(close);
        Clear(frame, removeTitle ? [285, 475, 330, 85] : [155, 1010, 190, 75]);
        var observer = new CareerScreenObserver(CapturedFrameRuntime.Create(frame));

        Assert.Null(await observer.ObserveAsync(Connection(), pack,
            new UraCareerSessionState { CareerStarted = true, LastScreenId = "follow_trainer_limit" },
            false, CancellationToken.None));
    }

    [Theory]
    [InlineData("career_complete", "to_home")]
    [InlineData("career_complete_close", "close")]
    [InlineData("career_story_unlocked", "close")]
    [InlineData("career_story_unlocked_compact", "close")]
    public async Task Settlement_dispatches_the_matching_exit_action(string screenId, string actionId)
    {
        var actions = new RecordingActions();
        var context = new CareerFlowContext(null!, null!, true, null!, null!, string.Empty,
            new UraCareerSessionState(), new CareerObservation(screenId, 1),
            null, CancellationToken.None);

        Assert.Null(await new CareerSettlementFlow(actions).HandleAsync(context));
        Assert.Equal((screenId, actionId), actions.LastAction);
    }

    [Theory]
    [InlineData("career_complete", false, false)]
    [InlineData("career_complete_close", false, false)]
    [InlineData("unknown", true, false)]
    [InlineData("career_story_unlocked_compact", false, false)]
    [InlineData("career_complete", false, true)]
    [InlineData("career_complete_close", false, true)]
    [InlineData("unknown", true, true)]
    [InlineData("career_story_unlocked", false, true)]
    public async Task Unlocked_story_is_dismissed_on_the_way_home_and_on_resume(
        string previousScreen, bool resume, bool compact)
    {
        var pack = await LoadPackAsync();
        var expectedScreenId = compact ? "career_story_unlocked_compact" : "career_story_unlocked";
        var frame = Load(Path.Combine(WorkspaceRoot(), "testdata", "hachimi", "ura", "captures", expectedScreenId + ".png"));
        var state = new UraCareerSessionState { CareerStarted = !resume, LastScreenId = previousScreen };
        var observer = new CareerScreenObserver(CapturedFrameRuntime.Create(frame));

        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None, careerOnly: resume);

        Assert.Equal(expectedScreenId, observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Settlement, observation?.Kind);
        var screen = pack.ScreenProfile.Find(observation!.ScreenId)!;
        var task = pack.ExecutionDefinition.GetTask(screen.FindAction("close")!.Task);
        var button = LoadResource(task.Template!);
        var match = TemplateMatcher.FindColor(frame, button, task.Roi,
            task.TemplateThreshold, 900, 1600, requireTextContrast: true);
        output.WriteLine($"{expectedScreenId}: title={observation.Score:0.000}, button={match.Score:0.000}, click=({match.CenterX},{match.CenterY}), size={button.Width}x{button.Height}");
        Assert.True(match.Found);
        Assert.InRange(match.CenterX, 435, 465);
        Assert.InRange(match.CenterY, compact ? 1020 : 1460, compact ? 1065 : 1495);
        if (compact)
        {
            var header = LoadResource(screen.Recognition.Template!);
            Assert.InRange(header.Width, 235, 255);
            Assert.InRange(header.Height, 30, 45);
            Assert.InRange(button.Width, 80, 110);
            Assert.InRange(button.Height, 25, 45);
            foreach (var crop in new[] { header, button })
            {
                Assert.True(Enumerable.Range(0, crop.Width * crop.Height)
                    .All(pixel => crop.RgbaPixels![pixel * 4 + 3] == 255));
            }
        }
        state.CareerStarted = true;
        state.LastScreenId = observation.ScreenId;
        Assert.True(CareerScreenObserver.IsReturningHome(state));

        // A generic Close label alone must not identify this optional overlay.
        pack = pack with { ScreenProfile = new UraScreenProfile { Screens = [screen] } };
        foreach (var roi in new[] { screen.Recognition.Roi!, screen.Recognition.RequiredTemplateRoi! })
        {
            var incomplete = frame with
            {
                Pixels = (byte[])frame.Pixels.Clone(),
                RgbaPixels = (byte[])frame.RgbaPixels!.Clone(),
            };
            Clear(incomplete, roi);
            Assert.Null(await new CareerScreenObserver(CapturedFrameRuntime.Create(incomplete))
                .ObserveAsync(Connection(), pack, state, false, CancellationToken.None));
        }

        // The compact story popup shares completion-dialog geometry. Its title
        // must distinguish it from Career Complete and the tall story layout.
        foreach (var unrelated in new[]
        {
            LoadCompletionFrame(false),
            LoadCompletionFrame(true),
            Load(Path.Combine(WorkspaceRoot(), "testdata", "hachimi", "ura", "captures",
                (compact ? "career_story_unlocked" : "career_story_unlocked_compact") + ".png")),
        })
        {
            Assert.Null(await new CareerScreenObserver(CapturedFrameRuntime.Create(unrelated))
                .ObserveAsync(Connection(), pack, state, false, CancellationToken.None));
        }
    }

    private static void Clear(GrayImage frame, int[] roi)
    {
        for (var y = roi[1]; y < roi[1] + roi[3]; y++)
        {
            Array.Fill(frame.Pixels, (byte)255, y * frame.Width + roi[0], roi[2]);
            Array.Fill(frame.RgbaPixels!, (byte)255, (y * frame.Width + roi[0]) * 4, roi[2] * 4);
        }
    }

    private static GrayImage LoadCompletionFrame(bool close) => close
        ? Load(Path.Combine(WorkspaceRoot(), "testdata", "hachimi", "ura", "captures", "career_complete_close.png"))
        : LoadResource("templates/runtime_frames/ura_rewards_support_next.png");

    private static GrayImage LoadResource(string relativePath) => Load(Path.Combine(
        ScreensDirectory(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static GrayImage Load(string path) => GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing completion image.", path);

    private static LastVerifiedConnection Connection() => new(
        "adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    private static Task<UraScenarioPack> LoadPackAsync() => UraScenarioPackLoader.LoadAsync(
        Path.Combine(ScreensDirectory(), "..", "manifest.json"));

    private static string ScreensDirectory() => Path.Combine(
        WorkspaceRoot(), "resource", "hachimi", "ura", "screens");

    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public (string ScreenId, string ActionId)? LastAction { get; private set; }

        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context,
            string screenId, string actionId, HachimiPipelineRunOptions? options = null)
        {
            LastAction = (screenId, actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class CapturedFrameRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = Create<IVisualPipelineRuntime, CapturedFrameRuntime>();
            ((CapturedFrameRuntime)(object)runtime)._frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(_frame),
                "LoadTemplateAsync" => Task.FromResult(GrayImageCodec.FromFile((string)args![0]!)),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}"),
            };
    }
}
