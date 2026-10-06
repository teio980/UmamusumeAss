using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingPerformanceTests
{
    private static readonly LastVerifiedConnection Connection = new("adb", "offline", "android", "version",
        900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
    private static readonly int[] PreviewCenters = [200, 350, 500, 650];

    [Fact]
    public async Task Five_previews_need_ten_frames_and_four_reversible_taps()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, PreviewRuntime>();
        var fake = (PreviewRuntime)(object)runtime;
        var sampler = CreateSampler(runtime, fake);
        var current = await sampler.CaptureAsync(Connection, null!, CancellationToken.None);
        foreach (var type in UraTrainingTypeCatalog.SupportedTypes)
        {
            current = await sampler.SelectAsync(Connection, null!, type, current, CancellationToken.None);
            Assert.Equal(type, current.Selection.RaisedType);
            // This same frame is the first numeric sample; only the second needs capture.
            current = await sampler.CaptureAsync(Connection, null!, CancellationToken.None);
        }
        Assert.Equal(10, sampler.CaptureCount);
        Assert.Equal(4, sampler.PreviewTapCount);
        Assert.Equal(10, fake.Detections);
        Assert.Equal(4, fake.Taps.Count);
        Assert.Equal(PreviewCenters, fake.Taps.Select(match => match.CenterX));
        Assert.All(fake.Taps, match => Assert.Equal(1310, match.CenterY));
    }

    [Fact]
    public async Task Delayed_animation_is_polled_without_a_second_tap()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, PreviewRuntime>();
        var fake = (PreviewRuntime)(object)runtime;
        var sampler = CreateSampler(runtime, fake);
        var initial = await sampler.CaptureAsync(Connection, null!, CancellationToken.None);
        fake.AnimationFrames = 1;
        var selected = await sampler.SelectAsync(Connection, null!, "wit", initial, CancellationToken.None);
        Assert.Equal("wit", selected.Selection.RaisedType);
        Assert.Equal(3, sampler.CaptureCount);
        Assert.Single(fake.Taps);
    }

    [Fact]
    public async Task Failed_switch_has_a_bounded_wait_and_no_training_confirmation()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, PreviewRuntime>();
        var fake = (PreviewRuntime)(object)runtime;
        var sampler = CreateSampler(runtime, fake);
        var initial = await sampler.CaptureAsync(Connection, null!, CancellationToken.None);
        fake.AnimationFrames = 100;
        var selected = await sampler.SelectAsync(Connection, null!, "power", initial, CancellationToken.None);
        Assert.Equal("speed", selected.Selection.RaisedType);
        Assert.Equal(4, sampler.CaptureCount);
        Assert.Single(fake.Taps);
    }

    [Fact]
    public async Task Leaving_the_picker_stops_switch_polling()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, PreviewRuntime>();
        var fake = (PreviewRuntime)(object)runtime;
        var sampler = CreateSampler(runtime, fake);
        var initial = await sampler.CaptureAsync(Connection, null!, CancellationToken.None);
        fake.ScreenChanged = true;
        var selected = await sampler.SelectAsync(Connection, null!, "stamina", initial, CancellationToken.None);
        Assert.True(selected.Selection.ScreenChanged);
        Assert.Equal(2, sampler.CaptureCount);
        Assert.Single(fake.Taps);
    }

    [Fact]
    public async Task Batch_assigns_by_position_and_rejects_cross_row_text()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, BatchRuntime>();
        var fake = (BatchRuntime)(object)runtime;
        fake.Result = new([
            new("28%", new(24, 115, 40, 20)), // second row, deliberately returned first
            new("+17", new(24, 25, 40, 20)),
            new("99", new(24, 80, 40, 40)), // spans the boundary: cannot be assigned
        ], "en-US");
        var image = new GrayImage(20, 20, new byte[400]); // 2x20 + 48 = 88px per row
        var result = await UraSmartTrainingOcrBatch.ReadAsync(runtime,
            [new("speed", image), new("failure_rate", image)], CancellationToken.None);
        Assert.Equal("+17", Assert.Single(result["speed"]!.Detections).Text);
        Assert.Equal("28%", Assert.Single(result["failure_rate"]!.Detections).Text);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Changed_numeric_pixels_are_read_again_and_disagreement_is_excluded()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "training_selection_ura.png"))!;
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, BatchRuntime>();
        var fake = (BatchRuntime)(object)runtime;
        fake.AutoRows = true;
        var reader = new UraSmartTrainingCandidateReader(runtime, (_, _, _, _, _, _, _) => Task.FromResult<int?>(null));
        var first = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None, optimize: true);
        Assert.True(first.IsSafe);
        var changed = frame with { RgbaPixels = (byte[])frame.RgbaPixels!.Clone() };
        for (var y = 980; y < 1002; y++)
            for (var x = 50; x < 65; x++)
            {
                var offset = (y * changed.Width + x) * 4;
                changed.RgbaPixels[offset] = 255;
                changed.RgbaPixels[offset + 1] = 150;
                changed.RgbaPixels[offset + 2] = 0;
            }
        fake.SpeedText = "+18";
        var second = await reader.ReadAsync(pack, changed, "speed", CancellationToken.None,
            optimize: true, reusePreviousRead: true);
        Assert.Equal(1, reader.LastWindowsOcrCalls);
        Assert.Equal(6, reader.LastReusedFields);
        Assert.Equal(18, second.SpeedGain);
        Assert.Null(UraSmartTrainingCandidateReader.MergeStable(first, second).SpeedGain);
        Assert.False(UraSmartTrainingCandidateReader.MergeStable(first, second).IsSafe);
    }

    [Fact]
    public async Task Unknown_failure_is_not_cached_as_zero_or_reused()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "training_selection_ura.png"))!;
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, BatchRuntime>();
        var fake = (BatchRuntime)(object)runtime;
        fake.AutoRows = true;
        fake.FailureText = "0%6%";
        var reader = new UraSmartTrainingCandidateReader(runtime, (_, _, _, _, _, _, _) => Task.FromResult<int?>(null));
        var first = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None, optimize: true);
        Assert.Null(first.FailureRatePercent);
        fake.FailureText = "0%";
        fake.OnlyFailure = true;
        var second = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None,
            optimize: true, reusePreviousRead: true);
        Assert.Equal(1, reader.LastWindowsOcrCalls);
        Assert.Equal(6, reader.LastReusedFields);
        Assert.Equal(0, second.FailureRatePercent);
        Assert.False(UraSmartTrainingCandidateReader.MergeStable(first, second).IsSafe);
    }

    [Fact]
    public async Task A_new_candidate_first_frame_does_not_reuse_previous_numeric_values()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "training_selection_ura.png"))!;
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, BatchRuntime>();
        var fake = (BatchRuntime)(object)runtime;
        fake.AutoRows = fake.FullPreview = true;
        var reader = new UraSmartTrainingCandidateReader(runtime, (_, _, _, _, _, _, _) => Task.FromResult<int?>(null));
        var first = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None, optimize: true);
        Assert.Equal(17, first.SpeedGain);
        fake.SpeedText = "+19";
        var next = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None, optimize: true);
        Assert.Equal(19, next.SpeedGain);
        Assert.Equal(0, reader.LastReusedFields);
        Assert.Equal(1, reader.LastWindowsOcrCalls);
    }

    [Fact]
    public async Task Batch_caller_cancellation_propagates()
    {
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, BatchRuntime>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UraSmartTrainingOcrBatch.ReadAsync(
            runtime, [new("failure_rate", new(20, 20, new byte[400]))], cancellation.Token));
    }

    private static UraSmartTrainingPreviewSampler CreateSampler(IVisualPipelineRuntime runtime, PreviewRuntime fake) =>
        new(runtime, (_, _, _) =>
        {
            fake.Detections++;
            if (fake.ScreenChanged)
                return Task.FromResult(new UraTrainingSelectionHeightResult(null, [], null, true));
            var matches = UraTrainingTypeCatalog.SupportedTypes.Select((type, index) =>
                new UraTrainingLogoMatch(type, new(true, 0.98, 30 + index * 150,
                    type == fake.Selected ? 1210 : 1290, 40, 40))).ToArray();
            return Task.FromResult(UraTrainingSelectionHeightDetector.SelectHighest(matches));
        });

    public class PreviewRuntime : DispatchProxy
    {
        public string Selected { get; set; } = "speed";
        public string? Pending { get; set; }
        public int AnimationFrames { get; set; }
        public int Detections { get; set; }
        public bool ScreenChanged { get; set; }
        public List<TemplateMatchResult> Taps { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
            switch (targetMethod!.Name)
            {
                case "CaptureGrayAsync":
                    if (Pending is not null && AnimationFrames-- <= 0)
                    {
                        Selected = Pending;
                        Pending = null;
                    }
                    return Task.FromResult<GrayImage?>(new(1, 1, [0]));
                case "TapMatchAsync":
                    Taps.Add((TemplateMatchResult)args[1]!);
                    var task = (string)args[2]!;
                    Pending = task.Split('_')[2];
                    return Task.CompletedTask;
                case "DelayAsync": return Task.CompletedTask;
                default: throw new InvalidOperationException($"Unexpected input: {targetMethod.Name}");
            }
        }
    }

    public class BatchRuntime : DispatchProxy
    {
        public ScreenTextRecognitionResult? Result { get; set; }
        public int Calls { get; set; }
        public bool AutoRows { get; set; }
        public bool OnlyFailure { get; set; }
        public bool FullPreview { get; set; }
        public string SpeedText { get; set; } = "+17";
        public string FailureText { get; set; } = "0%";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("DetectTextAsync", targetMethod!.Name);
            ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
            Calls++;
            if (!AutoRows)
                return Task.FromResult(Result);
            var image = (GrayImage)args[0]!;
            var texts = Calls == 1 || FullPreview ? new[] { SpeedText, "+10", "+6", FailureText }
                : new[] { OnlyFailure ? FailureText : SpeedText };
            var rowHeight = image.Height / texts.Length;
            return Task.FromResult<ScreenTextRecognitionResult?>(new(texts.Select((text, row) =>
                new ScreenTextDetection(text, new(24, row * rowHeight + 24, 40, 20))).ToArray(), "en-US"));
        }
    }
}
