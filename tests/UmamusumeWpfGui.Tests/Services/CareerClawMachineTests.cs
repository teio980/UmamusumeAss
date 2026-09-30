using System.IO;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerClawMachineTests
{
    [Fact]
    public async Task How_to_play_crop_recognizes_only_crane_game()
    {
        var screens = ScreensDirectory();
        var templatePath = Path.Combine(screens, "templates", "career",
            "turn", "claw_how_to_play.png");
        using (var image = Image.Load<Rgb24>(templatePath))
        {
            Assert.InRange(image.Width, 110, 150);
            Assert.InRange(image.Height, 25, 40);
        }
        Assert.Equal((byte)2, File.ReadAllBytes(templatePath)[25]); // PNG RGB, no alpha.

        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));
        var screen = pack.ScreenProfile.Find("claw_machine");
        Assert.NotNull(screen);
        Assert.Single(screen.Templates);
        Assert.Null(screen.Recognition.RequiredTemplate);

        var template = GrayImageCodec.FromFile(templatePath)!;
        foreach (var (capture, expected) in new[]
                 {
                     ("claw_machine_ready.png", true),
                     ("recreation_main.png", false),
                     ("recreation_confirmation.png", false),
                     ("recreation_event_choice.png", false),
                 })
        {
            var frame = GrayImageCodec.FromFile(Path.Combine(screens,
                "captures", capture))!;
            var match = TemplateMatcher.FindColor(frame, template,
                screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
                900, 1600, requireTextContrast: true);
            Assert.Equal(expected, match.Found);
        }
    }

    [Fact]
    public void Vision_finds_an_exposed_face_and_both_claw_arms()
    {
        var frame = GrayImageCodec.FromFile(Path.Combine(ScreensDirectory(),
            "captures", "claw_machine_ready.png"))!;
        Assert.True(CareerClawVision.TryFindTarget(frame, null, out var target));
        Assert.InRange(target.X, 740, 820);
        Assert.InRange(target.Y, 830, 915);
        Assert.True(CareerClawVision.TryFindClaw(frame, null, out var claw));
        Assert.InRange(claw.X, 300, 350);
        Assert.InRange(claw.Y, 405, 455);

        var nextFrame = GrayImageCodec.FromFile(Path.Combine(
            ScreensDirectory(), "captures", "claw_machine_credit2.png"))!;
        Assert.False(CareerClawVision.TryFindTarget(nextFrame, null,
            out _));
    }

    [Fact]
    public void Result_title_is_distinct_from_the_ready_and_recreation_screens()
    {
        var screens = ScreensDirectory();
        var template = GrayImageCodec.FromFile(Path.Combine(screens,
            "templates", "career", "turn", "claw_result_cuties.png"))!;
        foreach (var (capture, expected) in new[]
                 {
                     ("claw_machine_result.png", true),
                     ("claw_machine_ready.png", false),
                     ("recreation_main.png", false),
                 })
        {
            var frame = GrayImageCodec.FromFile(Path.Combine(screens,
                "captures", capture))!;
            var match = TemplateMatcher.FindColor(frame, template,
                [450, 430, 300, 70], 0.92, 900, 1600,
                requireTextContrast: true);
            Assert.Equal(expected, match.Found);
        }
    }

    [Theory]
    [InlineData("claw_machine_ready.png", "claw_machine")]
    [InlineData("claw_machine_result.png", "claw_machine_result")]
    public async Task Career_observer_routes_both_crane_screens(
        string capture, string expectedScreen)
    {
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));
        var frame = GrayImageCodec.FromFile(Path.Combine(ScreensDirectory(),
            "captures", capture))!;
        var visual = CareerGoalCompletionProbeTests.FrameVisualRuntime.Create(frame);
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            LastScreenId = "recreation_confirmation",
        };
        var connection = new LastVerifiedConnection("adb", "serial", "android",
            "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

        var observed = await new CareerScreenObserver(visual).ObserveAsync(
            connection, pack, state, false, CancellationToken.None);

        Assert.Equal(expectedScreen, observed?.ScreenId);
    }

    [Fact]
    public async Task Credit_counter_is_readable_from_the_capture()
    {
        using var image = Image.Load<Rgba32>(Path.Combine(
            ScreensDirectory(), "captures", "claw_machine_ready.png"));
        image.Mutate(context => context.Crop(new Rectangle(555, 15, 325, 75))
            .Resize(650, 150));
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        var recognized = await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(image.Width, image.Height, pixels), "en-US");
        Assert.Contains(recognized.Detections, detection =>
            detection.Text.Contains('3'));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_tracking_releases_the_held_control(
        bool cancel)
    {
        using var source = new CancellationTokenSource();
        var runtime = TouchFailureRuntime.Create(source, cancel);
        var connection = new LastVerifiedConnection("adb", "serial", "android",
            "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var context = new CareerFlowContext(connection, null!, true, null!,
            null!, string.Empty, new UraCareerSessionState(),
            new CareerObservation("claw_machine", 1), null, source.Token);
        var flow = new CareerClawMachineFlow(runtime);
        var ready = new CareerClawMachineFlow.ReadyFrame(2,
            new CareerClawVision.Point(780, 880),
            new CareerClawVision.Point(320, 430));

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                flow.GrabAsync(context, ready));
        else
            Assert.False((await flow.GrabAsync(context, ready))?.Succeeded);

        var recorded = (TouchFailureRuntime)(object)runtime;
        Assert.Equal(1, recorded.DownCount);
        Assert.Equal(0, recorded.UpCount);
        Assert.Equal(1, recorded.CancelCount);
    }

    public class TouchFailureRuntime : DispatchProxy
    {
        public CancellationTokenSource Source { get; set; } = null!;
        public bool CancelOnCapture { get; set; }
        public int DownCount { get; private set; }
        public int UpCount { get; private set; }
        public int CancelCount { get; private set; }

        public static IVisualPipelineRuntime Create(
            CancellationTokenSource source, bool cancel)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime,
                TouchFailureRuntime>();
            var proxy = (TouchFailureRuntime)(object)runtime;
            proxy.Source = source;
            proxy.CancelOnCapture = cancel;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "TouchDownAsync":
                    DownCount++;
                    return Task.CompletedTask;
                case "TouchUpAsync":
                    UpCount++;
                    return Task.CompletedTask;
                case "TouchCancelAsync":
                    CancelCount++;
                    return Task.CompletedTask;
                case "CaptureGrayAsync":
                    if (CancelOnCapture)
                    {
                        Source.Cancel();
                        return Task.FromCanceled<GrayImage?>(Source.Token);
                    }
                    return Task.FromException<GrayImage?>(
                        new InvalidOperationException("capture failed"));
                default:
                    throw new InvalidOperationException(
                        $"Unexpected visual runtime call: {targetMethod?.Name}.");
            }
        }
    }

    private static string ScreensDirectory() => Path.Combine(
        FindWorkspaceRoot(), "resource", "hachimi", "ura", "screens");

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource",
                    "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
}
