using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerMainRecognitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Main_requires_career_header_and_training_button_in_each_same_frame(bool startup)
    {
        var pack = await LoadMainOnlyPackAsync();
        var main = pack.ScreenProfile.Find("career_main")!;
        var complete = LoadFrame("career_main_after_rest.png");
        var headerOnly = WithoutRegion(complete, main.Recognition.RequiredTemplateRoi!);
        var trainingOnly = WithoutRegion(complete, main.Recognition.Roi!);

        Assert.Equal("career_main", (await ObserveAsync(pack, startup, complete, complete))?.ScreenId);
        Assert.Null(await ObserveAsync(pack, startup, headerOnly, headerOnly));
        Assert.Null(await ObserveAsync(pack, startup, trainingOnly, trainingOnly));
        // Separate captures cannot contribute one marker each to an AND match.
        Assert.Null(await ObserveAsync(pack, startup, headerOnly, trainingOnly));
        Assert.Null(await ObserveAsync(pack, startup, complete, headerOnly));
        Assert.Null(await ObserveAsync(pack, startup, trainingOnly, complete));
    }

    [Theory]
    [InlineData("classic_milech_race_day_final.png")]
    [InlineData("year2_nhk_race_day.png")]
    [InlineData("rest_event_ura.png")]
    public async Task Shared_career_title_on_race_day_or_rest_animation_is_not_main(string frameName)
    {
        var pack = await LoadMainOnlyPackAsync();
        var main = pack.ScreenProfile.Find("career_main")!;
        var frame = LoadFrame(frameName);
        var header = GrayImageCodec.FromFile(UraScenarioResourceResolver.Resolve(
            pack, main, main.Templates[0]));
        Assert.NotNull(header);
        Assert.True(TemplateMatcher.Find(frame, header, main.Recognition.Roi,
            main.Recognition.TemplateThreshold, 900, 1600).Found);

        Assert.Null(await ObserveAsync(pack, false, frame, frame));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_training_configuration_or_asset_never_falls_back_to_header_only(bool missingAsset)
    {
        var pack = await LoadMainOnlyPackAsync();
        if (!missingAsset)
            pack.ScreenProfile.Find("career_main")!.Recognition.RequiredTemplate = null;
        var frame = LoadFrame("career_main_after_rest.png");
        var visual = FramesRuntime.Create([frame, frame], out var runtime);
        runtime.MissingTrainingTemplate = missingAsset;

        Assert.Null(await new CareerScreenObserver(visual).ObserveAsync(Connection, pack,
            new UraCareerSessionState { CareerStarted = true }, false, CancellationToken.None,
            includeMainDetails: false));
    }

    [Fact]
    public async Task Race_day_is_observed_as_race_after_rest()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        pack.ScreenProfile.Screens.RemoveAll(screen => screen.ScreenId is not ("career_main" or "race_day"));
        var frame = LoadFrame("classic_milech_race_day_final.png");
        var visual = FramesRuntime.Create([frame, frame], out _);
        var observation = await new CareerScreenObserver(visual).ObserveAsync(Connection, pack,
            new UraCareerSessionState
            {
                CareerStarted = true, LastScreenId = "rest_confirmation", LastAction = UraPlannedAction.Rest,
            }, false, CancellationToken.None, includeMainDetails: false);

        Assert.Equal("race_day", observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Race, observation?.Kind);
    }

    [Theory]
    [InlineData("both", true)]
    [InlineData("header", false)]
    [InlineData("training", false)]
    [InlineData("separate_frames", false)]
    [InlineData("transition", false)]
    public async Task Career_entry_uses_the_same_dual_marker_gate(string evidence, bool expected)
    {
        var pack = await LoadMainOnlyPackAsync();
        var main = pack.ScreenProfile.Find("career_main")!;
        var complete = LoadFrame("career_main_after_rest.png");
        var headerOnly = WithoutRegion(complete, main.Recognition.RequiredTemplateRoi!);
        var trainingOnly = WithoutRegion(complete, main.Recognition.Roi!);
        GrayImage[] frames = evidence switch
        {
            "both" => [complete, complete],
            "header" => [headerOnly, headerOnly],
            "training" => [trainingOnly, trainingOnly],
            "separate_frames" => [headerOnly, trainingOnly],
            "transition" => [complete, headerOnly],
            _ => throw new InvalidOperationException(evidence),
        };
        var visual = FramesRuntime.Create(frames, out _);
        var database = DispatchProxy.Create<IUmaDatabaseService, UnexpectedCalls>();
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedCalls>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), $"main-entry-{Guid.NewGuid():N}.json")));
        var navigator = new CareerEntryNavigator(visual, database,
            new UraTraineeSelector(visual, database), new UraLegacySelector(visual, runner),
            new CareerJsonActionExecutor(runner));

        var result = await navigator.NavigateAsync(Connection, pack,
            DispatchProxy.Create<ICareerEntrySelectionSettings, UnexpectedCalls>(),
            new CareerEntryNavigationState { Step = CareerEntryNavigationStep.Career, RetryCount = 29 }, null);

        Assert.Equal(expected, result.Succeeded);
        Assert.Equal(0, result.ActionsCompleted);
        if (expected)
            Assert.Equal("career_main", result.LastScreenId);
    }

    private static Task<CareerObservation?> ObserveAsync(
        UraScenarioPack pack, bool startup, params GrayImage[] frames) =>
        new CareerScreenObserver(FramesRuntime.Create(frames, out _)).ObserveAsync(Connection, pack,
            new UraCareerSessionState { CareerStarted = !startup }, startup, CancellationToken.None,
            includeMainDetails: false);

    private static async Task<UraScenarioPack> LoadMainOnlyPackAsync()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        pack.ScreenProfile.Screens.RemoveAll(screen => screen.ScreenId != "career_main");
        return pack;
    }

    private static GrayImage LoadFrame(string name) =>
        GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), name)) ?? throw new InvalidDataException(name);

    private static GrayImage WithoutRegion(GrayImage frame, int[] roi)
    {
        var pixels = (byte[])frame.Pixels.Clone();
        var rgba = frame.RgbaPixels is null ? null : (byte[])frame.RgbaPixels.Clone();
        for (var y = roi[1]; y < roi[1] + roi[3]; y++)
        {
            Array.Fill(pixels, (byte)255, y * frame.Width + roi[0], roi[2]);
            if (rgba is not null)
                Array.Fill(rgba, (byte)255, (y * frame.Width + roi[0]) * 4, roi[2] * 4);
        }
        return new GrayImage(frame.Width, frame.Height, pixels, rgba);
    }

    private static LastVerifiedConnection Connection => new(
        "adb", "main-test", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    public class UnexpectedCalls : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}");
    }

    public class FramesRuntime : DispatchProxy
    {
        private GrayImage[] _frames = [];
        private int _captureCount;
        public bool MissingTrainingTemplate { get; set; }

        public static IVisualPipelineRuntime Create(GrayImage[] frames, out FramesRuntime runtime)
        {
            var visual = Create<IVisualPipelineRuntime, FramesRuntime>();
            runtime = (FramesRuntime)(object)visual;
            runtime._frames = frames;
            return visual;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(
                    _frames[Math.Min(_captureCount++, _frames.Length - 1)]),
                "LoadTemplateAsync" => Task.FromResult(
                    MissingTrainingTemplate && Path.GetFileName((string)args![0]!) == "career_main_action_training.png"
                        ? null : GrayImageCodec.FromFile((string)args![0]!)),
                "DetectTextAsync" => Task.FromResult<ScreenTextRecognitionResult?>(null),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}"),
            };
    }
}
