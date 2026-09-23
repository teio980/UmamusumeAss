using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraTrainingSelectionLiveTests
{
    private readonly ITestOutputHelper _output;

    public UraTrainingSelectionLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Live")]
    public async Task Speed_training_handles_selected_entry_state()
    {
        var phase = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_URA_TRAINING_SELECTION");
        if (phase is not ("normal" or "raised"))
            return;

        var root = FindWorkspaceRoot();
        var definitionPath = Path.Combine(
            root, "resource", "hachimi", "ura", "screens", "execution.json");
        var adbPath = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB")
            ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe";
        var serial = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL")
            ?? "127.0.0.1:16384";
        var delay = new AsyncDelay();
        var runtime = new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(30)), delay);
        var visual = new AdbVisualPipelineRuntime(
            runtime, delay, new WindowsOcrTextRecognizer());
        var runner = new HachimiJsonPipelineRunner(
            runtime,
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(), $"ura-training-live-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            adbPath, serial, "live-test", "android",
            900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var logs = new RecordingLogSink();
        var result = await runner.RunAsync(
            connection,
            definitionPath,
            phase == "normal"
                ? "training_selection_training_speed"
                : "training_selection_speed_raised_probe",
            logSink: logs,
            cancellationToken: cancellation.Token);
        foreach (var line in logs.Lines)
            _output.WriteLine(line);
        Assert.True(result.Succeeded, result.Message);

        // A reported transition must have left the training selection screen.
        var selectedScreen = await visual.WaitForMatchAsync(
            connection, "templates/training_selection_header.png",
            [0, 0, 120, 50], 0.92, 900, 1600, 500, 100,
            "live_training_selection_postcondition",
            Path.Combine(root, "resource", "hachimi", "ura", "screens"),
            cancellation.Token);
        Assert.False(selectedScreen?.Found == true,
            "The task reported success while still on training selection.");
    }

    private sealed class RecordingLogSink : IGrassTaskLogSink
    {
        public List<string> Lines { get; } = [];

        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Lines.Add($"{kind} {type}: {details}");
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find workspace root.");
    }
}
