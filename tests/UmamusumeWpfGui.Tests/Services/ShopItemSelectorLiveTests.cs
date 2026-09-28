using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class ShopItemSelectorLiveTests
{
    private readonly ITestOutputHelper _output;

    public ShopItemSelectorLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Live")]
    public async Task Opens_daily_sales_from_home_using_templates_without_purchasing()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SHOP_ENTRY") != "1")
            return;

        var root = FindRoot();
        var adbPath = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB")
            ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe";
        var serial = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL")
            ?? "emulator-5554";
        var delay = new AsyncDelay();
        var adb = new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(30)), delay);
        var visual = new AdbVisualPipelineRuntime(adb, delay, new WindowsOcrTextRecognizer());
        var runner = new HachimiJsonPipelineRunner(
            adb, visual, new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"shop-entry-live-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            adbPath, serial, "live-test", "android", 900, 1600, 900, 1600,
            DateTimeOffset.UtcNow);
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "pipelines", "shop_task.json"));
        Assert.NotNull(definition);
        // Stop at a page probe, before the real RunPipeline purchase action.
        definition!.Tasks["runShop"] = new HachimiPipelineTask
        {
            Algorithm = "MatchTemplate",
            Action = "DoNothing",
            Template = "templates/shop/daily_sales_header.png",
            Roi = [580, 60, 300, 110],
            TemplateThreshold = 0.86,
            TimeoutMilliseconds = 5000,
            Success = true,
        };
        var logs = new RecordingLogSink();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            var result = await runner.RunAsync(
                connection, definition, "specialShop", new HachimiPipelineRunOptions(),
                logs, timeout.Token);
            foreach (var line in logs.Lines)
                _output.WriteLine(line);
            Assert.True(result.Succeeded, result.Message);
        }
        finally
        {
            await adb.TapAsync(adbPath, serial, 95, 1350);
            await Task.Delay(600);
            await adb.TapAsync(adbPath, serial, 95, 1350);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Selects_visible_sashes_without_confirming_purchase()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SHOP_SELECT") != "1")
            return;

        await SelectsRowsWithoutConfirmingPurchase(
            new ShopPurchaseOptions(false, false, false, false, false, false, true), 2);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Selects_all_four_star_pieces_without_confirming_purchase()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SHOP_STARS") != "1")
            return;

        await SelectsRowsWithoutConfirmingPurchase(
            new ShopPurchaseOptions(false, true, false, false, false, false, false), 4);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Selects_every_row_in_the_current_shop_without_confirming_purchase()
    {
        if (Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SHOP_FULL") != "1")
            return;

        await SelectsRowsWithoutConfirmingPurchase(
            new ShopPurchaseOptions(false, true, true, true, true, true, true), 14);
    }

    private async Task SelectsRowsWithoutConfirmingPurchase(
        ShopPurchaseOptions options,
        int expectedSelectedRows)
    {
        var root = FindRoot();
        var adbPath = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_ADB")
            ?? @"C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe";
        var serial = Environment.GetEnvironmentVariable("UMAMUSUME_LIVE_SERIAL")
            ?? "emulator-5554";
        var delay = new AsyncDelay();
        var adb = new AdbRuntime(new AdbRunner(TimeSpan.FromSeconds(30)), delay);
        var visual = new AdbVisualPipelineRuntime(adb, delay, new WindowsOcrTextRecognizer());
        var runner = new HachimiJsonPipelineRunner(
            adb, visual, new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"shop-live-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            adbPath, serial, "live-test", "android", 900, 1600, 900, 1600,
            DateTimeOffset.UtcNow);
        var definition = new HachimiPipelineDefinition
        {
            BaseDirectory = Path.Combine(root, "resource", "hachimi", "pipelines"),
            Tasks = new Dictionary<string, HachimiPipelineTask>
            {
                ["shopSelectItems"] = new()
                {
                    Algorithm = "JustReturn",
                    Action = "SelectShopItems",
                    Success = true,
                },
            },
        };
        var logs = new RecordingLogSink();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var result = await runner.RunAsync(
                connection, definition, "shopSelectItems",
                new HachimiPipelineRunOptions
                {
                    ShopPurchaseOptions = options,
                },
                logs, timeout.Token);
            foreach (var line in logs.Lines)
                _output.WriteLine(line);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(expectedSelectedRows, logs.Lines.Count(line => line.Contains(
                "Selected '", StringComparison.Ordinal)));
        }
        finally
        {
            // Leave Daily Sales open and undo the temporary test selection.
            await adb.TapAsync(adbPath, serial, 770, 1350);
        }
    }

    private sealed class RecordingLogSink : IGrassTaskLogSink
    {
        public List<string> Lines { get; } = [];

        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Lines.Add($"{kind} {type}: {details}");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "pipelines", "shop.json"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests", "UmamusumeWpfGui.Tests")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Solution root was not found.");
    }
}
