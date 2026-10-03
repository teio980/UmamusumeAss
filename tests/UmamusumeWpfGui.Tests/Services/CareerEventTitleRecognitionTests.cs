using System.IO;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerEventTitleRecognitionTests
{
    private const string EventId = "acupuncturist_no_worries";
    private const string Title = "Just an Acupuncturist, No Worries! ☆";
    private static readonly Lazy<Task<UraScenarioPack>> Pack = new(() =>
        UraScenarioPackLoader.LoadAsync(Path.Combine(FindWorkspaceRoot(),
            "resource", "hachimi", "ura", "manifest.json")));

    [Fact]
    public async Task Missing_effects_is_recognized_using_the_same_two_frames()
    {
        var pack = await CreateEventPackAsync();
        var frame = LoadChoiceFrame();
        var screen = pack.ScreenProfile.Find("event_choice")!;
        var effects = GrayImageCodec.FromFile(UraScenarioResourceResolver.Resolve(
            pack, screen.Recognition.Template!))!;
        Assert.False(TemplateMatcher.Find(frame, effects, screen.Recognition.Roi,
            screen.Recognition.TemplateThreshold, 900, 1600).Found);
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(Title, frame), out var recorder);

        var observation = await ObserveAsync(runtime, pack);

        Assert.Equal("event_choice", observation?.ScreenId);
        Assert.Equal(EventId, observation?.EventId);
        Assert.Equal(Title, observation?.EventTitle);
        Assert.Equal(2, recorder.CaptureCount);
        Assert.Equal(2, recorder.OcrFrames.Count);
        Assert.All(recorder.OcrFrames, item => Assert.Same(frame, item));
        Assert.All(recorder.OcrRois, roi => Assert.Equal([140, 295, 620, 65], roi!));
    }

    [Fact]
    public async Task Full_profile_identifies_the_event_before_the_underlying_race_day()
    {
        var pack = await Pack.Value;
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(Title, frame), out _);

        Assert.Equal(EventId, (await ObserveAsync(runtime, pack))?.EventId);
    }

    [Fact]
    public async Task Handler_rechecks_title_and_runs_the_configured_first_choice_once()
    {
        var pack = await CreateEventPackAsync();
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(Title, frame), out var recorder);
        var actions = new RecordingActions();
        var log = new RecordingLog();
        var context = CreateContext(pack, log);

        var result = await new CareerEventHandler(runtime, actions)
            .TryRecognizeAndHandleAsync(context);

        Assert.Null(result);
        Assert.Equal(1, actions.CallCount);
        Assert.Equal(("event_choice", "choice_first"), actions.LastAction);
        Assert.Equal(EventId, actions.Context?.Observation.EventId);
        Assert.Equal(Title, actions.Context?.Observation.EventTitle);
        Assert.Equal(2, recorder.CaptureCount);
        Assert.All(recorder.OcrFrames, item => Assert.Same(frame, item));
        Assert.Contains(log.Messages, message => message.Contains(EventId, StringComparison.Ordinal)
            && message.Contains(Title, StringComparison.Ordinal)
            && message.Contains("OCR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Just an Acupuncturist,\nNo Worries!")]
    [InlineData("just an acupuncturist no worries")]
    [InlineData("Ｊｕｓｔ an Acupuncturist， No Worries！ ☆")]
    public async Task Exact_normalized_title_accepts_layout_and_punctuation_variations(string title)
    {
        var pack = await CreateEventPackAsync();
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(title, frame), out _);

        var match = await new CareerEventTitleRecognizer(runtime).RecognizeAsync(
            frame, pack, "event_choice", CancellationToken.None);

        Assert.Equal(EventId, match?.Event.EventId);
        Assert.Equal(title, match?.Title);
    }

    [Theory]
    [InlineData("Trainee Event")]
    [InlineData("Just an Acupuncturist, No Worry!")]
    [InlineData("Another Event")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unregistered_or_empty_titles_do_not_dispatch_actions(string? title)
    {
        var pack = await CreateEventPackAsync();
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(title, frame), out _);
        Assert.Null(await ObserveAsync(runtime, pack));
        var actions = new RecordingActions();

        Assert.Null(await new CareerEventHandler(runtime, actions)
            .TryRecognizeAndHandleAsync(CreateContext(pack)));
        Assert.Equal(0, actions.CallCount);
    }

    [Fact]
    public async Task Dialogue_without_choice_marker_skips_ocr_and_actions()
    {
        var pack = await CreateEventPackAsync();
        var frame = GrayImageCodec.FromFile(UraScenarioResourceResolver.Resolve(pack,
            "templates/runtime_frames/trainee_event_choice_ura.png"))!;
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(Title, frame), out var recorder);
        Assert.Null(await ObserveAsync(runtime, pack));
        var actions = new RecordingActions();

        await new CareerEventHandler(runtime, actions).TryRecognizeAndHandleAsync(CreateContext(pack));

        Assert.Empty(recorder.OcrFrames);
        Assert.Equal(0, actions.CallCount);
    }

    [Fact]
    public async Task Different_registered_events_in_two_frames_are_not_stable()
    {
        var pack = await CreateEventPackAsync();
        const string otherTitle = "Different Registered Event";
        pack = pack with
        {
            Events = new UraEventDocument
            {
                Events = [pack.Events.Events.Single(item => item.OcrTitle is not null),
                    new UraEventDefinition
                    {
                        EventId = "other", Title = otherTitle,
                        OcrTitle = new UraEventTitleRecognition
                            { ScreenId = "event_choice", ActionId = "choice_first" },
                    }],
            },
        };
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame,
            index => TextResult(index == 0 ? Title : otherTitle, frame), out _);
        Assert.Null(await ObserveAsync(runtime, pack));

        runtime = RecordingRuntime.Create(frame,
            index => TextResult(index == 0 ? Title : index == 1 ? otherTitle : null, frame), out _);
        var actions = new RecordingActions();
        await new CareerEventHandler(runtime, actions).TryRecognizeAndHandleAsync(CreateContext(pack));
        Assert.Equal(0, actions.CallCount);
    }

    [Fact]
    public async Task Ocr_errors_are_misses_but_requested_cancellation_propagates()
    {
        var pack = await CreateEventPackAsync();
        var frame = LoadChoiceFrame();
        var runtime = RecordingRuntime.Create(frame,
            _ => throw new InvalidOperationException("OCR unavailable"), out _);
        Assert.Null(await new CareerEventTitleRecognizer(runtime).RecognizeAsync(
            frame, pack, "event_choice", CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        runtime = RecordingRuntime.Create(frame, _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }, out _);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CareerEventTitleRecognizer(runtime).RecognizeAsync(
                frame, pack, "event_choice", cancellation.Token));
    }

    [Fact]
    public async Task Effects_path_and_legacy_event_data_do_not_require_ocr()
    {
        var pack = await CreateEventPackAsync();
        Assert.All(pack.Events.Events.Where(item => item.EventId != EventId),
            item => Assert.Null(item.OcrTitle));
        var frame = GrayImageCodec.FromFile(UraScenarioResourceResolver.Resolve(pack,
            "templates/runtime_frames/support_event_choice_ura.png"))!;
        var runtime = RecordingRuntime.Create(frame,
            _ => throw new InvalidOperationException("Effects should bypass OCR"), out var recorder);

        var observation = await ObserveAsync(runtime, pack);
        var actions = new RecordingActions();
        await new CareerEventHandler(runtime, actions).TryRecognizeAndHandleAsync(CreateContext(pack));

        Assert.Equal("event_choice", observation?.ScreenId);
        Assert.Null(observation?.EventId);
        Assert.Equal(("event_choice", "choice_first"), actions.LastAction);
        Assert.Empty(recorder.OcrFrames);
    }

    [Theory]
    [InlineData(450, 800)]
    [InlineData(720, 1280)]
    public async Task Choice_readiness_scales_with_screen_resolution(int width, int height)
    {
        var pack = await CreateEventPackAsync();
        using var image = Image.Load<Rgba32>(Path.Combine(FindWorkspaceRoot(),
            "testdata", "hachimi", "ura", "captures", "acupuncturist_no_worries_choice.png"));
        image.Mutate(context => context.Resize(width, height));
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        var frame = GrayImageCodec.FromScreenshot(new AdbScreenshotResult(
            AdbScreenshotMethod.EncodedPng, encoded.ToArray(), TimeSpan.Zero))!;
        var runtime = RecordingRuntime.Create(frame, _ => TextResult(Title, frame), out _);

        Assert.Equal(EventId, (await ObserveAsync(runtime, pack))?.EventId);
        var actions = new RecordingActions();
        await new CareerEventHandler(runtime, actions).TryRecognizeAndHandleAsync(CreateContext(pack));
        Assert.Equal(1, actions.CallCount);
        Assert.Equal([1d], actions.Options?.ScaleCandidatesOverrides?["event_choice_event_choice_first"]);
    }

    private static async Task<UraScenarioPack> CreateEventPackAsync()
    {
        var pack = await Pack.Value;
        return pack with
        {
            ScreenProfile = new UraScreenProfile
            {
                ReferenceWidth = pack.ScreenProfile.ReferenceWidth,
                ReferenceHeight = pack.ScreenProfile.ReferenceHeight,
                Screens = [pack.ScreenProfile.Find("event_choice")!],
            },
        };
    }

    private static GrayImage LoadChoiceFrame() => GrayImageCodec.FromFile(Path.Combine(
        FindWorkspaceRoot(), "testdata", "hachimi", "ura", "captures",
        "acupuncturist_no_worries_choice.png"))!;

    private static ScreenTextRecognitionResult? TextResult(string? title, GrayImage frame) =>
        title is null ? null : new ScreenTextRecognitionResult(
            [new ScreenTextDetection(title, new ScreenTextRect(145, 310, 480, 35))],
            "en-US", frame.Width, frame.Height);

    private static Task<CareerObservation?> ObserveAsync(IVisualPipelineRuntime runtime, UraScenarioPack pack) =>
        new CareerScreenObserver(runtime).ObserveAsync(CreateConnection(), pack,
            new UraCareerSessionState { CareerStarted = true },
            careerStartTransitionExpected: false, CancellationToken.None);

    private static LastVerifiedConnection CreateConnection() =>
        new("adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    private static CareerFlowContext CreateContext(UraScenarioPack pack, IGrassTaskLogSink? log = null) =>
        new(CreateConnection(), pack, true, null!, null!, string.Empty,
            new UraCareerSessionState(), new CareerObservation("event_choice", 1), log, CancellationToken.None);

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "testdata", "hachimi", "ura",
                    "captures", "acupuncturist_no_worries_choice.png")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the event capture fixture.");
    }

    public class RecordingRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;
        private Func<int, ScreenTextRecognitionResult?> _ocr = null!;
        public int CaptureCount { get; private set; }
        public List<GrayImage> OcrFrames { get; } = [];
        public List<int[]?> OcrRois { get; } = [];

        public static IVisualPipelineRuntime Create(GrayImage frame,
            Func<int, ScreenTextRecognitionResult?> ocr, out RecordingRuntime recorder)
        {
            var runtime = Create<IVisualPipelineRuntime, RecordingRuntime>();
            recorder = (RecordingRuntime)(object)runtime;
            recorder._frame = frame;
            recorder._ocr = ocr;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    CaptureCount++;
                    return Task.FromResult<GrayImage?>(_frame);
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile((string)args![0]!));
                case "DetectTextAsync" when args![0] is GrayImage frame:
                    Assert.Equal("en-US", args[4]);
                    Assert.Equal("career_event.event_choice.title", args[5]);
                    OcrFrames.Add(frame);
                    OcrRois.Add((int[]?)args[1]);
                    return Task.FromResult(_ocr(OcrFrames.Count - 1));
                case "DelayAsync":
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected runtime call: {targetMethod?.Name}.");
            }
        }
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public int CallCount { get; private set; }
        public (string, string) LastAction { get; private set; }
        public CareerFlowContext? Context { get; private set; }
        public HachimiPipelineRunOptions? Options { get; private set; }
        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context, string screenId,
            string actionId, HachimiPipelineRunOptions? options = null)
        {
            CallCount++;
            Context = context;
            Options = options;
            LastAction = (screenId, actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    private sealed class RecordingLog : IGrassTaskLogSink
    {
        public List<string> Messages { get; } = [];
        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Messages.Add(details);
    }
}
