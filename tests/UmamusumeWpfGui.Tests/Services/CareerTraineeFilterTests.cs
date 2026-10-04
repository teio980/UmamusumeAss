using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTraineeFilterTests
{
    [Fact]
    public async Task Career_filter_tab_matches_the_captured_display_settings_without_changing_its_policy()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var resources = pack.VisualResources!;
        Assert.True(resources.TryGetAsset("career.entry.trainee.filter_tab", out var asset));
        Assert.True(resources.TryGetRegion("career.entry.trainee.filter_tab", out var region));
        Assert.NotNull(asset);
        Assert.NotNull(region);
        var threshold = asset.Threshold ?? throw new InvalidDataException("Career Filter threshold is missing.");
        Assert.Equal(0.78, threshold);
        Assert.Equal([500, 120, 300, 120], region.Roi!);
        var frame = LoadDisplaySettings(root);
        var template = GrayImageCodec.FromFile(asset.Path);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(frame, template, region.Roi, threshold, 900, 1600);

        Assert.True(match.Found, $"Career Filter score {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 640, 680);
        Assert.InRange(match.CenterY, 150, 190);
        var dailyRaceTemplate = GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi",
            "pipelines", "templates", "daily_race", "runner_filter_tab.png"));
        Assert.NotNull(dailyRaceTemplate);
        Assert.False(TemplateMatcher.Find(frame, dailyRaceTemplate, region.Roi,
            threshold, 900, 1600).Found);
        Console.WriteLine($"Career Filter score={match.Score:0.000}; center=({match.CenterX},{match.CenterY}).");
    }

    [Fact]
    public async Task Trainee_selector_reaches_the_filter_tab_using_the_captured_career_page()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        using var cancellation = new CancellationTokenSource();
        var visual = FilterEntryRuntime.Create(LoadDisplaySettings(root), cancellation, out var recorder);
        var selector = new UraTraineeSelector(visual, database);
        var connection = new LastVerifiedConnection("adb", "emulator-5554", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

        // Stop immediately after the Filter tap. The fixture represents the
        // reported Display Settings page, not a whole trainee-selection run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.SelectAsync(
            connection, pack.ExecutionDefinition, "trainee_select_pick",
            pack.ExecutionDefinition.GetTask("trainee_select_pick"), 102602,
            null, pack.VisualResources, cancellation.Token));

        Assert.Equal(["careerRunnerFilterOpen", "careerRunnerFilterTab"], recorder.Taps);
        Assert.NotNull(recorder.FilterMatch);
        Assert.True(recorder.FilterMatch.Found);
    }

    private static GrayImage LoadDisplaySettings(string root) =>
        GrayImageCodec.FromFile(Path.Combine(root, "tests", "UmamusumeWpfGui.Tests", "Fixtures",
            "Career", "trainee-display-settings-sort.png"))
        ?? throw new FileNotFoundException("The captured Career Display Settings fixture is missing.");

    public class FilterEntryRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;
        private CancellationTokenSource _cancellation = null!;
        public List<string> Taps { get; } = [];
        public TemplateMatchResult? FilterMatch { get; private set; }

        public static IVisualPipelineRuntime Create(GrayImage frame, CancellationTokenSource cancellation,
            out FilterEntryRuntime recorder)
        {
            var runtime = Create<IVisualPipelineRuntime, FilterEntryRuntime>();
            recorder = (FilterEntryRuntime)(object)runtime;
            recorder._frame = frame;
            recorder._cancellation = cancellation;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "WaitForMatchAsync" when (string)args![8]! == "careerRunnerFilterOpen":
                    return Task.FromResult<TemplateMatchResult?>(new(true, 1, 600, 1200, 100, 50));
                case "WaitForMatchAsync" when (string)args![8]! == "careerRunnerFilterTab":
                    var template = GrayImageCodec.FromFile((string)args[1]!);
                    Assert.NotNull(template);
                    FilterMatch = TemplateMatcher.Find(_frame, template, (int[]?)args[2],
                        (double)args[3]!, (int)args[4]!, (int)args[5]!);
                    return Task.FromResult<TemplateMatchResult?>(FilterMatch);
                case "TapMatchAsync":
                    var action = (string)args![2]!;
                    Taps.Add(action);
                    if (action == "careerRunnerFilterTab")
                        _cancellation.Cancel();
                    return Task.CompletedTask;
                case "DelayAsync":
                    ((CancellationToken)args![1]!).ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected filter-entry call: {targetMethod?.Name}");
            }
        }
    }
}
