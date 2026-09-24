using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTrainingClickGuardTests
{
    [Fact]
    public async Task Already_clicked_training_checks_only_its_own_logo()
    {
        var root = FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "ura", "captures",
            "turn3_training_selection.png"));
        Assert.NotNull(frame);

        var visual = TrainingFrameRuntime.Create(frame!);
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(),
            visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"training-click-guard-{Guid.NewGuid():N}.json")));
        var dispatcher = new CareerFlowDispatcher(visual, runner);
        var state = new UraCareerSessionState
        {
            PendingTrainingType = "speed",
            TrainingClickIssuedType = "speed",
        };
        var context = new CareerFlowContext(
            new LastVerifiedConnection("adb", "serial", "android", "version",
                900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack, true, null!, null!, string.Empty, state,
            new CareerObservation("training_selection", 1), null,
            CancellationToken.None);

        Assert.Null(await dispatcher.RunAsync(context, "training_selection", "training.speed"));
        Assert.False(state.TrainingClickTargetGone);
        Assert.All(((TrainingFrameRuntime)(object)visual).LoadedTemplates,
            path => Assert.EndsWith("speed.png", path, StringComparison.Ordinal));

        ((TrainingFrameRuntime)(object)visual).Frame =
            new GrayImage(frame!.Width, frame.Height,
                new byte[frame.Width * frame.Height]);
        Assert.Null(await dispatcher.RunAsync(context, "training_selection", "training.speed"));
        Assert.True(state.TrainingClickTargetGone);
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "hachimi", "ura", "manifest.json"))
                && File.Exists(Path.Combine(directory.FullName,
                    "testdata", "hachimi", "ura", "captures",
                    "turn3_training_selection.png")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find workspace root.");
    }

    public class TrainingFrameRuntime : DispatchProxy
    {
        public GrayImage Frame { get; set; } = null!;
        public List<string> LoadedTemplates { get; } = [];

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, TrainingFrameRuntime>();
            ((TrainingFrameRuntime)(object)runtime).Frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "CaptureGrayAsync")
                return Task.FromResult<GrayImage?>(Frame);
            if (targetMethod?.Name == "LoadTemplateAsync")
            {
                var path = (string)args![0]!;
                LoadedTemplates.Add(path);
                return Task.FromResult(GrayImageCodec.FromFile(
                    Path.Combine((string)args[1]!, path)));
            }

            throw new InvalidOperationException(
                $"Unexpected visual runtime call: {targetMethod?.Name}.");
        }
    }

    public class UnexpectedAdbRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected ADB call: {targetMethod?.Name}.");
    }
}
