using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerOcrReuseTests
{
    [Theory]
    [InlineData("+17", 17)]
    [InlineData("Failure O%", 0)]
    [InlineData("+Il", 11)]
    [InlineData("662", 662)]
    [InlineData("+17 +17", 17)]
    [InlineData("+17 +10", null)]
    [InlineData("", null)]
    [InlineData("10000", null)]
    public void Skill_and_shared_numeric_parsers_preserve_the_same_results(string text, int? expected)
    {
        var result = Result(text);
        Assert.Equal(expected, CareerOcrNumberParser.ParseSingleNumber(result));
        Assert.Equal(expected, CareerSkillLearningFlow.ParseSingleNumber(result));
    }

    [Theory]
    [InlineData("+999", 999, 999)]
    [InlineData("+1000", 999, null)]
    [InlineData("100%", 100, 100)]
    [InlineData("101%", 100, null)]
    [InlineData("0% 6%", 100, null)]
    [InlineData("010", 100, null)]
    [InlineData("+fi]9", 999, null)]
    [InlineData("712", 999, null)]
    [InlineData("-5", 999, null)]
    public void Training_adds_range_and_ambiguity_checks(string text, int maximum, int? expected) =>
        Assert.Equal(expected, CareerOcrNumberParser.ParseTrainingNumber([text], maximum));

    [Fact]
    public async Task Training_reuses_roi_crop_and_restores_coordinates_without_a_new_capture()
    {
        var recognizer = new RecordingRecognizer();
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, NeverCallProxy>(), new AsyncDelay(), recognizer);
        var frame = new GrayImage(900, 1600, new byte[900 * 1600], new byte[900 * 1600 * 4]);
        var result = await runtime.DetectTextAsync(frame, [200, 300, 100, 80], 900, 1600,
            "en-US", "training_selection.failure_rate");

        Assert.Equal((116, 96), recognizer.Size);
        Assert.Equal(new ScreenTextRect(212, 312, 20, 20), Assert.Single(result!.Detections).Bounds);
        Assert.Equal(900, result.Width);
    }

    [Fact]
    public void Full_image_numeric_crop_keeps_the_last_padding_row_and_column()
    {
        var frame = new GrayImage(3, 2, [1, 2, 3, 4, 5, 6]);
        var crop = CareerNumericOcrReader.Crop(frame, [0, 0, 3, 2], 3, 2);
        Assert.NotNull(crop);
        Assert.Equal((3, 2), (crop.Width, crop.Height));
        Assert.Equal(frame.Pixels, crop.Pixels);
    }

    [Fact]
    public async Task Existing_countdown_uses_shared_image_code_with_its_original_parser()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "training_selection_ura.png"));
        Assert.NotNull(frame);
        var roi = pack.ScreenProfile.Find("career_main")!.FindOcrRegion("objective.turns_left")!.ToRoi();
        Assert.Equal(11, await CareerCountdownOcrReader.TryReadAsync([frame], roi, 900, 1600,
            CancellationToken.None));
        Assert.Equal(11, await CareerCountdownOcrReader.TryReadAsync([frame], roi, 900, 1600,
            CancellationToken.None));
    }

    [Fact]
    public async Task Missing_gain_uses_shared_fallback_and_auxiliary_fields_are_not_read()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, CoreRuntime>();
        var fake = (CoreRuntime)(object)runtime;
        fake.SpeedText = null;
        var frame = new GrayImage(900, 1600, new byte[900 * 1600]);
        var calls = 0;
        var reader = new UraSmartTrainingCandidateReader(runtime, (actualFrame, _, _, _, maximum, _, _) =>
        {
            Assert.Equal((152, 65), (actualFrame.Width, actualFrame.Height));
            Assert.Equal(999, maximum);
            calls++;
            return Task.FromResult<int?>(17);
        });

        var candidate = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None);

        Assert.True(candidate.IsSafe);
        Assert.Equal(1, calls);
        Assert.Equal(7, fake.Calls.Count);
        Assert.Null(candidate.TrainingLevel);
        Assert.Null(candidate.UnbondedSupportCount);
        Assert.Null(candidate.HintCount);
        Assert.Equal("tesseract-fallback", reader.LastReadings["speed"].Source);
        Assert.Equal(1, candidate.FailureRateConfidence);
        Assert.Equal(0.82, candidate.CoreConfidence);
    }

    [Fact]
    public async Task Expired_candidate_budget_keeps_missing_failure_unknown()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, CoreRuntime>();
        ((CoreRuntime)(object)runtime).FailureText = "0% 6%";
        using var budget = new CancellationTokenSource();
        budget.Cancel();
        var reader = new UraSmartTrainingCandidateReader(runtime,
            (_, _, _, _, _, _, _) => throw new InvalidOperationException("Expired fallback must not run."));

        var candidate = await reader.ReadAsync(pack,
            new GrayImage(900, 1600, new byte[900 * 1600]), "speed", CancellationToken.None, budget.Token);

        Assert.Null(candidate.FailureRatePercent);
        Assert.False(candidate.IsSafe);
        Assert.Equal(1, candidate.CoreConfidence);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_from_numeric_fallback()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, CoreRuntime>();
        ((CoreRuntime)(object)runtime).SpeedText = null;
        using var cancellation = new CancellationTokenSource();
        var reader = new UraSmartTrainingCandidateReader(runtime, (_, _, _, _, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<int?>(null);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(pack,
            new GrayImage(900, 1600, new byte[900 * 1600]), "speed", cancellation.Token));
    }

    [Fact]
    public async Task Candidate_budget_expires_across_fallback_calls_and_rejects_late_values()
    {
        var budget = new CareerNumericOcrBudget(TimeSpan.FromMilliseconds(80));
        Assert.Equal(17, await budget.RunAsync(_ => Task.FromResult<int?>(17),
            CancellationToken.None, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.RunAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 10;
        }, CancellationToken.None, CancellationToken.None));
        Assert.Null(await budget.RunAsync(_ => throw new InvalidOperationException("Budget exhausted."),
            CancellationToken.None, CancellationToken.None));
    }

    [Theory]
    [InlineData("training_selection_ura.png", false)]
    [InlineData("training_selection_turn15_ura.png", true)]
    public async Task Old_preview_templates_reject_training_result_with_the_same_header(
        string capture, bool screenChanged)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));
        Assert.NotNull(frame);
        var runtime = CareerSkillLearningFlowTests.FrameRuntime.Create(frame);
        var connection = new LastVerifiedConnection("adb", "serial", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var result = await new UraTrainingSelectionHeightDetector(runtime)
            .DetectAsync(connection, pack, CancellationToken.None);
        Assert.Equal(screenChanged, result.ScreenChanged);
        Assert.Equal(!screenChanged, result.Succeeded);
    }

    private static ScreenTextRecognitionResult Result(string text) =>
        new([new(text, new ScreenTextRect(20, 20, 20, 20))], "en-US", 900, 1600);

    public class NeverCallProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected device call: {targetMethod?.Name}");
    }

    public class CoreRuntime : DispatchProxy
    {
        public string? SpeedText { get; set; } = "+17";
        public string? FailureText { get; set; } = "0%";
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("DetectTextAsync", targetMethod?.Name);
            var field = args!.OfType<string>().Last().Split('.').Last();
            Calls.Add(field);
            var text = field switch
            {
                "speed" => SpeedText,
                "failure_rate" => FailureText,
                "stamina" or "power" or "guts" or "wit" or "skill_points" => "+0",
                _ => throw new InvalidOperationException($"Auxiliary field requested: {field}"),
            };
            return Task.FromResult<ScreenTextRecognitionResult?>(text is null ? null : Result(text));
        }
    }

    private sealed class RecordingRecognizer : IScreenTextRecognizer
    {
        public (int Width, int Height) Size { get; private set; }
        public Task<ScreenTextRecognitionResult> RecognizeAsync(AdbRawScreenshot screenshot,
            string? language, CancellationToken cancellationToken = default)
        {
            Size = (screenshot.Width, screenshot.Height);
            return Task.FromResult(Result("5%"));
        }
    }
}
