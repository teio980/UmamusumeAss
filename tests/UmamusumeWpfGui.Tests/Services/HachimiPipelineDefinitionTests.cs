using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class HachimiPipelineDefinitionTests
{
    [Theory]
    [InlineData("mail_collection.json", "Home")]
    [InlineData("team_race.json", "RaceTab")]
    [InlineData("daily_race.json", "DailyProgram")]
    public async Task Ordinary_pipeline_definitions_load_with_the_shared_schema(
        string fileName,
        string expectedTask)
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", fileName);

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal(1, definition!.SchemaVersion);
        Assert.Contains(expectedTask, definition.Tasks.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(definition.ReferenceWidth > 0);
        Assert.True(definition.ReferenceHeight > 0);
    }

    [Fact]
    public async Task Team_race_can_call_the_shared_shop_pipeline()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "team_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var trigger = definition!.GetTask("runRandomShop");
        Assert.Equal("RunPipeline", trigger.Action);
        Assert.Equal("shop.json", trigger.Pipeline);
        Assert.Equal("shopProbe", trigger.Entry);
        Assert.Contains("MiddleNext", trigger.Next);
        Assert.Contains("MiddleNext", trigger.OnErrorNext);
    }

    [Fact]
    public void Start_game_keeps_its_special_monitor_and_trigger_chain_definition()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "start_game.json");
        var json = File.ReadAllText(path);
        var definition = System.Text.Json.JsonSerializer.Deserialize<StartGamePipelineDefinition>(json);

        Assert.NotNull(definition);
        Assert.Equal("StartupMonitor", definition!.Start);
        var monitor = definition.Tasks[definition.Start];
        Assert.Equal("StartupMonitor", monitor.Algorithm);
        Assert.Equal("CheckStartNoticeSkip", monitor.TriggerTask);
        Assert.Equal(["CheckLogoSkip"], monitor.TriggerChain);
        Assert.Contains("CheckGameHome", monitor.MonitorTasks);
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
