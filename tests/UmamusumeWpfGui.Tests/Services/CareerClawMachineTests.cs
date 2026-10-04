using System.IO;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerClawMachineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Fixed_holds_finish_the_observed_credits_without_tracking_plushes(int credit)
    {
        var pack = await LoadPackAsync();
        var frame = Capture("claw_machine_ready.png");
        var marker = Match(frame, "claw_how_to_play.png");
        Assert.True(CareerClawVision.TryFindControl(frame, marker, pack.ScreenProfile.ClawMachine, out var button));
        // Remove the entire playfield. Only CREDIT, How to Play and the control remain.
        var pixels = (byte[])frame.RgbaPixels!.Clone();
        var bottom = button.Top - (int)Math.Ceiling(button.Width * pack.ScreenProfile.ClawMachine.ControlBorderPaddingRatio);
        for (var y = marker.Y + marker.Height; y < bottom; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = (y * frame.Width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 100;
            }
        var runtime = SequenceRuntime.Create(new(frame.Width, frame.Height, [], pixels), credit);
        var recorded = (SequenceRuntime)(object)runtime;

        Assert.Null(await new CareerClawMachineFlow(runtime).HandleAsync(Context(pack)));

        Assert.Equal(credit, recorded.DownCount);
        Assert.Equal(credit, recorded.UpCount);
        Assert.Equal(0, recorded.CancelCount);
        Assert.Equal(1, recorded.TapCount);
        Assert.Equal(credit, recorded.HoldDurations.Count);
        Assert.All(recorded.HoldDurations, duration => Assert.Equal(pack.ScreenProfile.ClawMachine.HoldDurationMs, duration));
    }

    [Fact]
    public async Task Credit_is_read_from_the_screenshot_before_pressing()
    {
        var pack = await LoadPackAsync();
        var runtime = SequenceRuntime.Create(Capture("claw_machine_credit2.png"), 2);
        ((SequenceRuntime)(object)runtime).UseActualOcr = true;
        var ready = await new CareerClawMachineFlow(runtime).WaitForNextAsync(Context(pack),
            LoadMarker("claw_how_to_play.png"), LoadMarker("claw_result_cuties.png"), null, TimeSpan.FromSeconds(10));
        Assert.Equal(2, ready?.Ready?.Credit);
        Assert.Equal(0, ((SequenceRuntime)(object)runtime).DownCount);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(2, true)]
    public async Task A_previous_credit_cannot_trigger_an_extra_hold(int previousCredit, bool shouldWait)
    {
        var runtime = SequenceRuntime.Create(Capture("claw_machine_credit2.png"), 2);
        var observed = await new CareerClawMachineFlow(runtime).WaitForNextAsync(Context(await LoadPackAsync()),
            LoadMarker("claw_how_to_play.png"), LoadMarker("claw_result_cuties.png"), previousCredit,
            TimeSpan.FromMilliseconds(shouldWait ? 100 : 5000));
        if (shouldWait) Assert.Null(observed);
        else Assert.Equal(2, observed?.Ready?.Credit);
        Assert.Equal(0, ((SequenceRuntime)(object)runtime).DownCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(12)]
    public void Credit_parser_uses_the_observed_number(int credit) =>
        Assert.Equal(credit, CareerClawMachineFlow.ParseCredit(new(
            [new ScreenTextDetection($"CREDIT {credit}", new(10, 10, 300, 40))], "en-US")));

    [Fact]
    public void Prize_and_ambiguous_numbers_are_not_remaining_credits()
    {
        var label = new ScreenTextDetection("CREDIT", new(10, 10, 100, 40));
        var number = new ScreenTextDetection("7", new(200, 10, 50, 40));
        var prize = new ScreenTextDetection("PRIZES 2", new(10, 100, 300, 40));
        Assert.Equal(7, CareerClawMachineFlow.ParseCredit(new([label, number, prize], "en-US")));
        Assert.Null(CareerClawMachineFlow.ParseCredit(new([prize], "en-US")));
        Assert.Null(CareerClawMachineFlow.ParseCredit(new([label, number, number with { Text = "8" }], "en-US")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_interrupted_hold_always_releases_the_touch(bool cancel)
    {
        using var source = new CancellationTokenSource();
        var pack = await LoadPackAsync();
        var frame = Capture("claw_machine_ready.png");
        Assert.True(CareerClawVision.TryFindControl(frame, Match(frame, "claw_how_to_play.png"),
            pack.ScreenProfile.ClawMachine, out var control));
        var runtime = SequenceRuntime.Create(frame, 3);
        var recorded = (SequenceRuntime)(object)runtime;
        recorded.FailHold = true;
        recorded.CancelSource = cancel ? source : null;
        var ready = new CareerClawMachineFlow.ReadyFrame(3, control, frame.Width, frame.Height);
        var flow = new CareerClawMachineFlow(runtime);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.PressControlAsync(Context(pack, source.Token), ready));
        else
            Assert.False((await flow.PressControlAsync(Context(pack), ready))?.Succeeded);
        Assert.Equal(1, recorded.DownCount);
        Assert.Equal(0, recorded.UpCount);
        Assert.Equal(1, recorded.CancelCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-140, 40)]
    public async Task Control_and_result_buttons_use_their_detected_positions(int dx, int dy)
    {
        var runtime = SequenceRuntime.Create(Shift(Capture("claw_machine_ready.png"), dx, dy), 1);
        var recorded = (SequenceRuntime)(object)runtime;
        recorded.ResultFrame = Shift(Capture("claw_machine_result.png"), dx, dy);
        Assert.Null(await new CareerClawMachineFlow(runtime).HandleAsync(Context(await LoadPackAsync())));
        Assert.InRange(recorded.DownPoint.X, 440 + dx, 470 + dx);
        Assert.InRange(recorded.DownPoint.Y, 1380 + dy, 1420 + dy);
        Assert.InRange(recorded.TapPoint.X, 440 + dx, 460 + dx);
        Assert.InRange(recorded.TapPoint.Y, 1460 + dy, 1490 + dy);
    }

    [Theory]
    [InlineData("claw_machine_ready.png", "claw_machine")]
    [InlineData("claw_machine_result.png", "claw_machine_result")]
    public async Task Career_observer_routes_the_game_and_result(string capture, string expected)
    {
        var visual = CareerGoalCompletionProbeTests.FrameVisualRuntime.Create(Capture(capture));
        var context = Context(await LoadPackAsync());
        var observed = await new CareerScreenObserver(visual).ObserveAsync(context.Connection, context.Pack,
            new UraCareerSessionState { CareerStarted = true, LastScreenId = "recreation_confirmation" },
            false, CancellationToken.None);
        Assert.Equal(expected, observed?.ScreenId);
    }

    [Fact]
    public void Game_and_result_markers_reject_the_recreation_screen()
    {
        var frame = Capture("recreation_main.png");
        Assert.False(Match(frame, "claw_how_to_play.png").Found);
        Assert.False(Match(frame, "claw_result_cuties.png").Found);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_ADB_finishes_the_remaining_credits_and_closes_the_result()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_CLAW_RUN") != "1") return;
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var delay = new AsyncDelay();
        var visual = new AdbVisualPipelineRuntime(new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(15)), delay),
            delay, new WindowsOcrTextRecognizer());
        var connection = new LastVerifiedConnection(
            Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB") ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe",
            Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL") ?? "127.0.0.1:16384",
            "claw validation", "android", 900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        var context = Context(await LoadPackAsync(), source.Token) with { Connection = connection, LogSink = new ConsoleSink() };
        Assert.Null(await new CareerClawMachineFlow(visual).HandleAsync(context));
    }

    private sealed class ConsoleSink : IGrassTaskLogSink
    {
        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {type}: {details}");
    }

    public class SequenceRuntime : DispatchProxy
    {
        private GrayImage _ready = null!;
        private int _initialCredit;
        private int _readCredit;
        private bool _holding;
        private int? _previousCredit;
        public GrayImage ResultFrame { get; set; } = null!;
        public bool UseActualOcr { get; set; }
        public bool FailHold { get; set; }
        public CancellationTokenSource? CancelSource { get; set; }
        public int DownCount { get; private set; }
        public int UpCount { get; private set; }
        public int CancelCount { get; private set; }
        public int TapCount { get; private set; }
        public List<int> HoldDurations { get; } = [];
        public (int X, int Y) DownPoint { get; private set; }
        public (int X, int Y) TapPoint { get; private set; }

        public static IVisualPipelineRuntime Create(GrayImage frame, int credit)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, SequenceRuntime>();
            var recorded = (SequenceRuntime)(object)runtime;
            recorded._ready = frame;
            recorded._initialCredit = credit;
            recorded.ResultFrame = Capture("claw_machine_result.png");
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "PrepareHeldTouchAsync":
                    return Task.CompletedTask;
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(Path.Combine((string)args![1]!, (string)args[0]!)));
                case "CaptureGrayAsync":
                    if (TapCount > 0) return Task.FromResult<GrayImage?>(Capture("recreation_main.png"));
                    if (UpCount >= _initialCredit) return Task.FromResult<GrayImage?>(ResultFrame);
                    _readCredit = _previousCredit ?? _initialCredit - UpCount;
                    _previousCredit = null;
                    return Task.FromResult<GrayImage?>(_ready);
                case "DetectTextAsync":
                    return UseActualOcr ? ReadTextAsync((GrayImage)args![0]!)
                        : Task.FromResult<ScreenTextRecognitionResult?>(new(
                            [new ScreenTextDetection($"CREDIT {_readCredit}", new(10, 10, 300, 40))], "en-US"));
                case "TouchDownAsync":
                    DownCount++;
                    _holding = true;
                    DownPoint = ((int)args![1]!, (int)args[2]!);
                    return Task.CompletedTask;
                case "DelayAsync":
                    if (_holding)
                    {
                        HoldDurations.Add((int)args![0]!);
                        if (FailHold)
                        {
                            if (CancelSource is { } source)
                            {
                                source.Cancel();
                                return Task.FromCanceled(source.Token);
                            }
                            return Task.FromException(new InvalidOperationException("hold interrupted"));
                        }
                    }
                    return Task.CompletedTask;
                case "TouchUpAsync":
                    _previousCredit = _initialCredit - UpCount;
                    UpCount++;
                    _holding = false;
                    return Task.CompletedTask;
                case "TouchCancelAsync":
                    CancelCount++;
                    _holding = false;
                    return Task.CompletedTask;
                case "TapAsync":
                    TapCount++;
                    TapPoint = ((int)args![1]!, (int)args[2]!);
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected visual runtime call: {targetMethod?.Name}.");
            }
        }

        private static async Task<ScreenTextRecognitionResult?> ReadTextAsync(GrayImage frame) =>
            await new WindowsOcrTextRecognizer().RecognizeAsync(new(frame.Width, frame.Height, frame.RgbaPixels!), "en-US");
    }

    private static GrayImage Capture(string name) => GrayImageCodec.FromFile(
        CareerTestResourceResolver.FindUraCapture(WorkspaceRoot(), name))!;
    private static GrayImage LoadMarker(string name) => GrayImageCodec.FromFile(
        CareerTestResourceResolver.ResolveBuiltInUraVisualResource($"templates/career/turn/{name}"))!;
    private static TemplateMatchResult Match(GrayImage frame, string name) => CareerClawVision.MatchMarker(frame,
        LoadMarker(name), new UraScreenProfile(), new UraScreenRecognition { TemplateThreshold = 0.92, MatchColorText = true });
    private static Task<UraScenarioPack> LoadPackAsync() => UraScenarioPackLoader.LoadAsync(Path.Combine(WorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));
    private static CareerFlowContext Context(UraScenarioPack pack, CancellationToken token = default) =>
        new(new("adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch), pack,
            true, null!, null!, string.Empty, new(), new("claw_machine", 1), null, token);
    private static string ScreensDirectory() => Path.Combine(WorkspaceRoot(), "resource", "hachimi", "ura", "screens");
    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "UmamusumeWpfGui.Tests"))
                && File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
    private static GrayImage Shift(GrayImage frame, int dx, int dy)
    {
        var pixels = new byte[frame.Width * frame.Height * 4];
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                if (x + dx >= 0 && x + dx < frame.Width && y + dy >= 0 && y + dy < frame.Height)
                    Array.Copy(frame.RgbaPixels!, (y * frame.Width + x) * 4, pixels,
                        ((y + dy) * frame.Width + x + dx) * 4, 4);
        return new(frame.Width, frame.Height, [], pixels);
    }
}
