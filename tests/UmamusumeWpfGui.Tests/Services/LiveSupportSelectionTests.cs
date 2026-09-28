using System.Diagnostics;
using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class LiveSupportSelectionTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task Select_five_owned_cards_then_friend_without_starting_career_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SUPPORT_SELECTOR") != "1")
            return;

        var root = FindSolutionRoot();
        var adbPath = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB")
            ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe";
        var serial = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL")
            ?? "emulator-5554";
        var delay = new AsyncDelay();
        var adb = new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(30)), delay);
        var visual = new AdbVisualPipelineRuntime(adb, delay, new WindowsOcrTextRecognizer());
        var runner = new HachimiJsonPipelineRunner(adb, visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), "uma-live-support-settings.json")));
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        var actions = new StopBeforeCareerStartActionExecutor(
            new CareerJsonActionExecutor(runner), output);
        var navigator = new CareerEntryNavigator(
            visual, database,
            new UraTraineeSelector(visual, database),
            new UraLegacySelector(visual, runner),
            actions);
        var manifestPath = Path.Combine(root, "resource", "hachimi", "ura", "manifest.json");
        var pack = await UraScenarioPackLoader.LoadAsync(manifestPath);
        var settings = new IndependentTrainingSettings(
            manifestPath,
            101101,
            ContinueExistingCareer: true,
            SupportCardIds: [20024, 20009, 20013, 30028, 20020],
            SupportDeckMode: "selected",
            SupportDeckPreset: "custom",
            FriendSupportCardId: 30016,
            LegacySelectionMode: "auto",
            UseLegacyGuest: false,
            UseCachedLegacy: false,
            LegacyAttributeSparks: [],
            LegacyAptitudeSparks: []);
        var connection = new LastVerifiedConnection(
            adbPath, serial, "live-support-test", "android",
            900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        var state = new CareerEntryNavigationState
        {
            Step = CareerEntryNavigationStep.Support,
            LastScreenId = "support_select",
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var result = await navigator.NavigateAsync(
            connection, pack, settings, state, new SupportSelectionLogSink(output),
            cancellationToken: cancellation.Token);

        output.WriteLine($"Result: {result.Message}");
        Assert.True(actions.StoppedBeforeStart, result.Message);
        Assert.Equal(6, actions.SelectedCards);
        Assert.Equal(6, actions.OpenedSlots);
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

    private sealed class StopBeforeCareerStartActionExecutor(
        CareerJsonActionExecutor inner,
        ITestOutputHelper output) : ICareerActionExecutor
    {
        public int OpenedSlots { get; private set; }
        public int SelectedCards { get; private set; }
        public bool StoppedBeforeStart { get; private set; }

        public async Task<CareerActionExecutionResult> RunAsync(
            LastVerifiedConnection connection,
            UraScenarioPack pack,
            string screenId,
            string actionId,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken,
            HachimiPipelineRunOptions? options = null,
            bool allowVisualMiss = false)
        {
            if (screenId == "support_select" && actionId == "start")
            {
                StoppedBeforeStart = true;
                return new(false, "Live selection verified; Career start was intercepted.", screenId);
            }

            var timer = Stopwatch.StartNew();
            var result = await inner.RunAsync(connection, pack, screenId, actionId,
                logSink, cancellationToken, options, allowVisualMiss);
            output.WriteLine($"{screenId}.{actionId}: {timer.Elapsed.TotalSeconds:0.00}s, "
                + $"success={result.Succeeded} {result.Message}");
            if (screenId == "support_select" && actionId == "open" && result.Succeeded)
                OpenedSlots++;
            if (screenId == "support_select" && actionId == "ranked.select_exact_card"
                && result.Succeeded)
                SelectedCards++;
            return result;
        }

        public Task<CareerActionExecutionResult> RunTaskAsync(
            LastVerifiedConnection connection,
            UraScenarioPack pack,
            string taskName,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken,
            HachimiPipelineRunOptions? options = null) =>
            inner.RunTaskAsync(connection, pack, taskName, logSink, cancellationToken, options);
    }

    private sealed class SupportSelectionLogSink(ITestOutputHelper output) : IGrassTaskLogSink
    {
        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info)
        {
            if (type.Contains("card_exact", StringComparison.Ordinal)
                || type.Contains("friend_remove", StringComparison.Ordinal))
                output.WriteLine($"{kind} {type}: {details}");
        }
    }
}
