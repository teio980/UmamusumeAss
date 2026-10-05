using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerHomeEntryRecognitionTests
{
    private static readonly int[] ImmediateDelays = [650];
    private static readonly int[] PolledDelays = [250, 650];

    [Theory]
    [InlineData("home_home_career", false)]
    [InlineData("home_home_career_active", false)]
    [InlineData("home_home_career_active", true)]
    public async Task Either_home_entry_is_clicked_from_the_first_frame_where_it_appears(
        string visibleTask, bool blankFirstFrame)
    {
        var pack = await LoadPackAsync();
        var task = pack.ExecutionDefinition.GetTask(visibleTask);
        var template = GrayImageCodec.FromFile(Path.Combine(
            pack.ExecutionDefinition.BaseDirectory, task.Template!));
        Assert.NotNull(template);
        var x = task.Roi![0] + 8;
        var y = task.Roi[1] + 8;
        var pixels = new byte[900 * 1600];
        for (var row = 0; row < template.Height; row++)
            Buffer.BlockCopy(template.Pixels, row * template.Width,
                pixels, (y + row) * 900 + x, template.Width);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, HomeVisualRuntime>();
        var runtime = (HomeVisualRuntime)(object)visual;
        runtime.Frames = blankFirstFrame
            ? [BlankFrame(), new GrayImage(900, 1600, pixels)]
            : [new GrayImage(900, 1600, pixels)];

        var result = await Runner(visual).RunAsync(
            Connection, pack.ExecutionDefinition, "home_home_career");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(blankFirstFrame ? 2 : 1, runtime.Captures);
        Assert.Equal(2, runtime.LoadedTemplates.Count);
        var tap = Assert.Single(runtime.Taps);
        Assert.Equal(x + template.Width / 2, tap.CenterX);
        Assert.Equal(y + template.Height / 2, tap.CenterY);
        Assert.Equal(blankFirstFrame ? PolledDelays : ImmediateDelays, runtime.Delays);
    }

    [Fact]
    public async Task Neither_entry_matching_uses_one_timeout_and_does_not_click()
    {
        var pack = await LoadPackAsync();
        var entry = pack.ExecutionDefinition.GetTask("home_home_career");
        entry.TimeoutMilliseconds = 20;
        entry.RetryTimes = 0;
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, HomeVisualRuntime>();
        var runtime = (HomeVisualRuntime)(object)visual;
        runtime.Frames = [BlankFrame()];
        runtime.WaitForDelays = true;

        var result = await Runner(visual).RunAsync(
            Connection, pack.ExecutionDefinition, "home_home_career");

        Assert.False(result.Succeeded);
        Assert.Equal(HachimiFailureKind.RecognitionTimeout, result.FailureKind);
        Assert.Empty(runtime.Taps);
        Assert.Equal(2, runtime.LoadedTemplates.Count);
        Assert.All(runtime.Delays, delay => Assert.InRange(delay, 1, 20));
    }

    private static Task<UraScenarioPack> LoadPackAsync() => UraScenarioPackLoader.LoadAsync(
        Path.Combine(CareerTestResourceResolver.FindWorkspaceRoot(),
            "resource", "hachimi", "ura", "manifest.json"));

    private static GrayImage BlankFrame() => new(900, 1600, new byte[900 * 1600]);

    private static HachimiJsonPipelineRunner Runner(IVisualPipelineRuntime visual) => new(
        DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(), visual,
        new JsonSettingsService(Path.Combine(Path.GetTempPath(), $"home-entry-{Guid.NewGuid():N}.json")));

    private static LastVerifiedConnection Connection => new(
        "adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    public class HomeVisualRuntime : DispatchProxy
    {
        public GrayImage[] Frames { get; set; } = [];
        public int Captures { get; private set; }
        public bool WaitForDelays { get; set; }
        public List<string> LoadedTemplates { get; } = [];
        public List<TemplateMatchResult> Taps { get; } = [];
        public List<int> Delays { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "LoadTemplateAsync":
                    var path = Path.Combine((string)args![1]!, (string)args[0]!);
                    LoadedTemplates.Add(path);
                    return Task.FromResult(GrayImageCodec.FromFile(path));
                case "CaptureGrayAsync":
                    var frame = Frames[Math.Min(Captures++, Frames.Length - 1)];
                    return Task.FromResult<GrayImage?>(frame);
                case "TapMatchAsync":
                    Taps.Add((TemplateMatchResult)args![1]!);
                    return Task.CompletedTask;
                case "DelayAsync":
                    var delay = (int)args![0]!;
                    Delays.Add(delay);
                    return WaitForDelays ? Task.Delay(delay, (CancellationToken)args[1]!) : Task.CompletedTask;
                default:
                    // A per-template wait would restore the original serial timeout bug.
                    throw new InvalidOperationException($"Unexpected visual operation: {targetMethod?.Name}.");
            }
        }
    }

    public class UnexpectedAdbRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected ADB operation: {targetMethod?.Name}.");
    }
}
