using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerMainSharedOcrFrameTests
{
    [Fact]
    public async Task Main_page_uses_one_additional_frame_for_all_four_ocr_regions()
    {
        var template = CreateFrame();
        var ocrFrame = CreateFrame();
        var runtime = RecordingVisualRuntime.Create(template, ocrFrame, out var recorder);
        var observer = new CareerScreenObserver(runtime);

        var observation = await observer.ObserveAsync(
            CreateConnection(), CreatePack(), new UraCareerSessionState { CareerStarted = true },
            careerStartTransitionExpected: false, CancellationToken.None);

        Assert.Equal("career_main", observation?.ScreenId);
        Assert.Equal(3, recorder.CaptureCount);
        Assert.Equal(4, recorder.OcrCalls.Count);
        Assert.All(recorder.OcrCalls, call => Assert.Same(ocrFrame, call.Frame));
        Assert.Equal(
            ["career_main.turn_position", "career_main.objective.turns_left",
             "career_main.objective.title", "career_main.mood.current"],
            recorder.OcrCalls.Select(call => call.TaskName));
        Assert.Equal(
            [0, 2, 4, 6],
            recorder.OcrCalls.Select(call => call.Roi![0]));
        Assert.Equal("Junior Year Early Jan", observation?.TurnPositionText);
        Assert.Equal(12, observation?.TurnsToGoal);
        Assert.Equal("Earn 3000 fans Progress 1200 fans to go", observation?.GoalText);
        Assert.Equal("Good", observation?.MoodText);
    }

    [Fact]
    public async Task Missing_shared_frame_does_not_trigger_per_region_recaptures()
    {
        var template = CreateFrame();
        var runtime = RecordingVisualRuntime.Create(template, null, out var recorder);
        var observer = new CareerScreenObserver(runtime);

        var observation = await observer.ObserveAsync(
            CreateConnection(), CreatePack(), new UraCareerSessionState { CareerStarted = true },
            careerStartTransitionExpected: false, CancellationToken.None);

        Assert.Equal("career_main", observation?.ScreenId);
        Assert.Equal(3, recorder.CaptureCount);
        Assert.Empty(recorder.OcrCalls);
        Assert.Null(observation?.TurnPositionText);
        Assert.Null(observation?.TurnsToGoal);
        Assert.Null(observation?.GoalText);
        Assert.Null(observation?.MoodText);
    }

    [Theory]
    [InlineData("race_day", "race.objective")]
    [InlineData("race_list", "objective.title")]
    public async Task Race_goal_ocr_uses_the_same_frame_path(
        string screenId,
        string regionId)
    {
        var template = CreateFrame();
        var ocrFrame = CreateFrame();
        var runtime = RecordingVisualRuntime.Create(template, ocrFrame, out var recorder);
        var observer = new CareerScreenObserver(runtime);

        var observation = await observer.ObserveAsync(
            CreateConnection(), CreatePack(screenId, regionId),
            new UraCareerSessionState { CareerStarted = true },
            careerStartTransitionExpected: false, CancellationToken.None);

        Assert.Equal(screenId, observation?.ScreenId);
        Assert.Equal("Place within top 3", observation?.GoalText);
        Assert.Equal(3, recorder.CaptureCount);
        Assert.Same(ocrFrame, Assert.Single(recorder.OcrCalls).Frame);
    }

    [Fact]
    public async Task Frame_ocr_keeps_cropping_and_restores_screen_coordinates_without_adb()
    {
        var pixels = Enumerable.Repeat((byte)100, 100 * 100).ToArray();
        var rgba = new byte[100 * 100 * 4];
        for (var index = 0; index < pixels.Length; index++)
        {
            rgba[index * 4] = 100;
            rgba[index * 4 + 1] = 100;
            rgba[index * 4 + 2] = 100;
            rgba[index * 4 + 3] = 255;
        }
        var frame = new GrayImage(100, 100, pixels, rgba);
        var recognizer = new RecordingTextRecognizer();
        var runtime = new AdbVisualPipelineRuntime(
            UnexpectedAdbCallProxy.Create(), new AsyncDelay(), recognizer);

        var result = await runtime.DetectTextAsync(
            frame, [10, 10, 20, 20], 50, 50,
            "en-US", "career_main.objective.title");

        Assert.Equal(1, recognizer.CallCount);
        Assert.Equal((56, 56), recognizer.LastSize);
        Assert.Equal((100, 100), (result?.Width, result?.Height));
        Assert.Equal(new ScreenTextRect(22, 22, 2, 2),
            Assert.Single(result!.Detections).Bounds);
    }

    private static LastVerifiedConnection CreateConnection() =>
        new("adb", "serial", "android", "version", 8, 8, 8, 8,
            DateTimeOffset.UnixEpoch);

    private static GrayImage CreateFrame() =>
        new(8, 8,
            [0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0]);

    private static UraScenarioPack CreatePack(
        string screenId = "career_main",
        string? raceRegionId = null)
    {
        var profile = new UraScreenProfile
        {
            ReferenceWidth = 8,
            ReferenceHeight = 8,
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = screenId,
                    Recognition = new UraScreenRecognition
                    {
                        Template = screenId == "career_main" ? "main.png" : "race.png",
                        Stable = true,
                    },
                    OcrRegions = screenId == "career_main"
                        ?
                        [
                            Region("scenario.phase", 0),
                            Region("objective.turns_left", 2),
                            Region("objective.title", 4),
                            Region("mood.current", 6),
                        ]
                        : [Region(raceRegionId!, 0)],
                },
            ],
        };
        return new UraScenarioPack(
            string.Empty, Path.GetTempPath(), string.Empty, string.Empty,
            null!, null!, null!, null!, null!, profile,
            new HachimiPipelineDefinition());
    }

    private static UraScreenTextRegion Region(string semanticId, int x) =>
        new()
        {
            SemanticId = semanticId,
            Bounds = new UraScreenTextBounds { X = x, Y = 0, Width = 2, Height = 2 },
        };

    public class RecordingVisualRuntime : DispatchProxy
    {
        private GrayImage _template = null!;
        private GrayImage? _ocrFrame;

        public int CaptureCount { get; private set; }
        public List<(GrayImage Frame, string TaskName, int[]? Roi)> OcrCalls { get; } = [];

        public static IVisualPipelineRuntime Create(
            GrayImage template,
            GrayImage? ocrFrame,
            out RecordingVisualRuntime recorder)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, RecordingVisualRuntime>();
            recorder = (RecordingVisualRuntime)(object)runtime;
            recorder._template = template;
            recorder._ocrFrame = ocrFrame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    CaptureCount++;
                    return Task.FromResult<GrayImage?>(
                        CaptureCount <= 2 ? _template : _ocrFrame);
                case "LoadTemplateAsync":
                    return Task.FromResult<GrayImage?>(
                        args?[0] is string path
                            && (path.EndsWith("main.png", StringComparison.Ordinal)
                                || path.EndsWith("race.png", StringComparison.Ordinal))
                            ? _template : null);
                case "DetectTextAsync" when args is not null && args[0] is GrayImage frame:
                    var taskName = (string)args[5]!;
                    OcrCalls.Add((frame, taskName, (int[]?)args[1]));
                    var text = taskName switch
                    {
                        "career_main.turn_position" => "Junior Year Early Jan",
                        "career_main.objective.turns_left" => "12 turns left",
                        "career_main.objective.title" =>
                            "Earn 3000 fans Progress 1200 fans to go",
                        "career_main.mood.current" => "Good",
                        "race_day.objective.title" or "race_list.objective.title" =>
                            "Place within top 3",
                        _ => throw new InvalidOperationException(taskName),
                    };
                    return Task.FromResult<ScreenTextRecognitionResult?>(
                        new ScreenTextRecognitionResult(
                            [new ScreenTextDetection(text, new ScreenTextRect(0, 0, 1, 1))],
                            "en-US", frame.Width, frame.Height));
                case "DelayAsync":
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected visual runtime call: {targetMethod?.Name ?? "unknown"}.");
            }
        }
    }

    private sealed class RecordingTextRecognizer : IScreenTextRecognizer
    {
        public int CallCount { get; private set; }
        public (int Width, int Height) LastSize { get; private set; }

        public Task<ScreenTextRecognitionResult> RecognizeAsync(
            AdbRawScreenshot screenshot,
            string? language,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSize = (screenshot.Width, screenshot.Height);
            return Task.FromResult(new ScreenTextRecognitionResult(
                [
                    new ScreenTextDetection("inside", new ScreenTextRect(10, 10, 2, 2)),
                    new ScreenTextDetection("outside", new ScreenTextRect(0, 0, 2, 2)),
                ],
                language ?? string.Empty,
                screenshot.Width,
                screenshot.Height));
        }
    }

    public class UnexpectedAdbCallProxy : DispatchProxy
    {
        public static IAdbRuntime Create() =>
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbCallProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected ADB call: {targetMethod?.Name ?? "unknown"}.");
    }
}
