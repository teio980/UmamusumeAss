using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraRaceResultRecognizerTests
{
    [Fact]
    public async Task Runtime_result_capture_resolves_through_the_declared_collection()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var resources = Assert.IsType<CareerVisualResourcePackage>(pack.VisualResources);
        var race = Assert.IsType<UraRaceDefinition>(
            pack.Races.Races.First(item => item.ObservedOutcome is not null));
        var observed = Assert.IsType<UraRaceObservedOutcome>(race.ObservedOutcome);
        var capture = Assert.IsType<string>(observed.Capture);
        var expectedPath = resources.ResolveVisualResource(
            $"career.runtime_frame:{Path.GetFileNameWithoutExtension(capture)}");
        Assert.True(File.Exists(expectedPath), expectedPath);

        var visual = DispatchProxy.Create<IVisualPipelineRuntime, CapturingVisualRuntime>();
        var recorder = (CapturingVisualRuntime)(object)visual;
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "test", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var result = await new UraRaceResultRecognizer(visual).RecognizeAsync(
            connection, pack, race);

        Assert.NotNull(result);
        Assert.Equal(expectedPath, recorder.TemplatePath);
        Assert.Equal(capture, result.Capture);
        Assert.Equal(race.RaceId, result.RaceId);
        Assert.Equal(observed.Placement, result.Placement);
        Assert.Equal(resources.Regions["career.race.result.runtime_frame_match"].Roi,
            recorder.Roi);
        Assert.Equal(2_500, recorder.TimeoutMilliseconds);
        Assert.Equal(250, recorder.PollIntervalMilliseconds);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "hachimi", "ura", "manifest.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    public class CapturingVisualRuntime : DispatchProxy
    {
        public string? TemplatePath { get; private set; }
        public int[]? Roi { get; private set; }
        public int TimeoutMilliseconds { get; private set; }
        public int PollIntervalMilliseconds { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IVisualPipelineRuntime.WaitForMatchAsync))
            {
                throw new InvalidOperationException(
                    $"Unexpected visual runtime call: {targetMethod?.Name}.");
            }

            TemplatePath = (string?)args![1];
            Roi = (int[]?)args[2];
            TimeoutMilliseconds = (int)args[6]!;
            PollIntervalMilliseconds = (int)args[7]!;
            return Task.FromResult<TemplateMatchResult?>(
                new TemplateMatchResult(true, 0.96, 10, 20, 30, 40));
        }
    }
}
