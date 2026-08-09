using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class LiveDailyRacePipelineTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task Run_one_daily_race_with_100601_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_DAILY_RACE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = FindSolutionRoot();
        var adbPath = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB")
            ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe";
        var serial = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL")
            ?? "127.0.0.1:16384";
        var delay = new AsyncDelay();
        var adbRuntime = new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(30)), delay);
        var visualRuntime = new AdbVisualPipelineRuntime(adbRuntime, delay);
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        var selector = new DailyRaceRunnerSelector(visualRuntime, database);
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"umamusume-live-settings-{Guid.NewGuid():N}.json");
        var runner = new HachimiJsonPipelineRunner(
            adbRuntime,
            visualRuntime,
            new JsonSettingsService(settingsPath));
        var pipeline = new AdbDailyRacePipeline(runner, selector);
        var connection = new LastVerifiedConnection(
            adbPath,
            serial,
            "live-test",
            "android",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var result = await pipeline.RunWithTraineeAsync(
            connection,
            Path.Combine(root, "resource", "hachimi", "daily_race.json"),
            "monies",
            "hard",
            1,
            100601,
            cancellationToken: cancellation.Token);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.RacesCompleted);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
