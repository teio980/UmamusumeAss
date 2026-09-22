using System.IO;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class RacePlaybackFlowContractTests
{
    [Fact]
    public async Task Playback_race_button_is_centered_and_does_not_match_runner_race_button()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(
            Path.Combine(
                root,
                "resource",
                "hachimi",
                "ura",
                "screens",
                "execution.json"));

        Assert.NotNull(definition);
        var task = definition!.GetTask("race_runner_playback_start");
        Assert.Equal("MatchTemplateColor", task.Algorithm);
        Assert.NotNull(task.Roi);
        Assert.Equal([320, 1380, 260, 180], task.Roi!);

        var template = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "templates",
            "career",
            "race",
            "race_playback_start.png"));
        var playbackReady = GrayImageCodec.FromFile(Path.Combine(
            root,
            "testdata",
            "hachimi",
            "ura",
            "captures",
            "senior_february_playback_ready2.png"));
        var runnerPage = GrayImageCodec.FromFile(Path.Combine(
            root,
            "testdata",
            "hachimi",
            "ura",
            "captures",
            "senior_arima_race_stage.png"));

        Assert.NotNull(template);
        Assert.NotNull(playbackReady);
        Assert.NotNull(runnerPage);

        var readyMatch = TemplateMatcher.FindColor(
            playbackReady!,
            template!,
            task.Roi,
            task.TemplateThreshold,
            definition.ReferenceWidth,
            definition.ReferenceHeight);
        Assert.True(readyMatch.Found);
        Assert.InRange(readyMatch.CenterX, 420, 480);

        var runnerMatch = TemplateMatcher.FindColor(
            runnerPage!,
            template!,
            task.Roi,
            task.TemplateThreshold,
            definition.ReferenceWidth,
            definition.ReferenceHeight);
        Assert.False(runnerMatch.Found);
    }

    [Fact]
    public async Task Playback_race_waits_for_skip_as_a_state_transition()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(
            Path.Combine(
                root,
                "resource",
                "hachimi",
                "ura",
                "screens",
                "execution.json"));

        Assert.NotNull(definition);
        var task = definition!.GetTask("race_runner_playback_start");
        Assert.Equal("ClickSelf", task.Action);
        Assert.Equal("ParallelMonitor", definition!.GetTask(
            "race_runner_playback_result_monitor").Algorithm);
        Assert.True(definition.GetTask("race_runner_playback_result_monitor").SuccessTask
            is "race_runner_replay_probe");
        var resultMonitor = definition.GetTask("race_runner_playback_result_monitor");
        Assert.Contains("race_runner_playback_skip", resultMonitor.MonitorTasks);
        Assert.Contains("race_runner_playback_start", resultMonitor.MonitorTasks);
        var readyMonitor = definition.GetTask("race_runner_playback_ready_monitor");
        Assert.Contains("race_runner_playback_resume_race", readyMonitor.MonitorTasks);
        Assert.Equal("race_runner_playback_ready_probe", readyMonitor.SuccessTask);
        Assert.True(definition.GetTask("race_runner_playback_ready_probe").TimeoutMilliseconds > 0);
        Assert.Equal(
            "race_runner_playback_start",
            definition.GetTask("race_runner_playback_ready_probe").Next.Single());
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
