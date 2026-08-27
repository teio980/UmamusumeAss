using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingCatalogTests
{
    [Fact]
    public void Every_game_available_race_uses_a_race_id_card_path()
    {
        var root = FindSolutionRoot();
        var catalog = IndependentTrainingCatalog.Load(root);
        var races = catalog.Races
            .Where(item => item.IsGameAvailable)
            .GroupBy(item => item.RaceId)
            .Select(group => group.First())
            .ToArray();

        Assert.NotEmpty(races);
        Assert.All(races, race =>
        {
            Assert.True(race.RaceId > 0);
            Assert.Equal(
                $"templates{Path.DirectorySeparatorChar}independent"
                + $"{Path.DirectorySeparatorChar}race_cards"
                + $"{Path.DirectorySeparatorChar}{race.RaceId}.png",
                race.RaceCardTemplatePath);
            Assert.True(
                File.Exists(IndependentTrainingCatalog.TryResolveRaceCardImagePath(
                    race,
                    root)),
                $"Missing card image for Race ID {race.RaceId}.");
        });
    }

    [Fact]
    public void Picker_mapping_uses_year_turn_and_race_name_not_game_order()
    {
        var catalog = IndependentTrainingCatalog.Load(FindSolutionRoot());
        var selection = new IndependentTrainingAgendaSelection(
            "Second Year",
            "01_01",
            "Junior Cup");

        Assert.True(catalog.TryGetAgendaPickerEntry(selection, out var race));
        Assert.Equal(4002, race.RaceId);
        Assert.Equal("Junior Cup", race.RaceName);
    }

    [Theory]
    [InlineData("front", "Front Runner")]
    [InlineData("pace", "Pace Chaser")]
    [InlineData("late", "Late Surger")]
    [InlineData("end", "End Closer")]
    public void Existing_lineup_strategy_setting_maps_to_the_strategy_dialog(
        string settingValue,
        string expectedTargetText)
    {
        Assert.True(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                settingValue,
                out var targetText));
        Assert.Equal(expectedTargetText, targetText);
    }

    [Fact]
    public void Strategy_mapping_rejects_ura_strategy_ids_and_unknown_values()
    {
        Assert.False(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                "default-speed-medium",
                out _));
        Assert.False(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                "race-strategy",
                out _));
    }

    [Theory]
    [InlineData("front", "independent.strategy.option.front")]
    [InlineData("pace", "independent.strategy.option.pace")]
    [InlineData("late", "independent.strategy.option.late")]
    [InlineData("end", "independent.strategy.option.end")]
    public void Lineup_strategy_setting_selects_the_matching_semantic_option(
        string settingValue,
        string expectedSemanticAction)
    {
        Assert.True(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                settingValue,
                out var semanticAction,
                out _));
        Assert.Equal(expectedSemanticAction, semanticAction);
    }

    [Fact]
    public async Task Independent_setup_wires_scroll_collapse_and_strategy_in_order()
    {
        var root = FindSolutionRoot();
        var profilePath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json");
        var executionPath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json");

        using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var actions = profile.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString()
                == "career_final_confirmation")
            .GetProperty("actions")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("semanticId").GetString()!,
                item => item.GetProperty("task").GetString()!,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "independent_lineup_scroll_to_top",
            actions[IndependentTrainingCatalog.LineupScrollTopSemanticAction()]);
        Assert.Equal(
            "independent_lineup_collapse_prepare",
            actions[IndependentTrainingCatalog.LineupCollapseSemanticAction()]);
        Assert.Equal(
            "independent_strategy_change",
            actions[IndependentTrainingCatalog.StrategyChangeSemanticAction()]);
        Assert.Equal(
            "independent_strategy_option",
            actions[IndependentTrainingCatalog.StrategyOptionSemanticAction()]);
        Assert.Equal(
            "independent_strategy_save",
            actions[IndependentTrainingCatalog.StrategySaveSemanticAction()]);
        Assert.Equal(
            "independent_strategy_return_probe",
            actions[IndependentTrainingCatalog.StrategyReturnSemanticAction()]);
        foreach (var strategyValue in new[] { "front", "pace", "late", "end" })
        {
            Assert.True(
                IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                    strategyValue,
                    out var strategyAction,
                    out _));
            Assert.Equal("independent_strategy_option", actions[strategyAction]);
        }

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(executionPath);
        Assert.Equal(
            "Swipe",
            definition!.GetTask("independent_lineup_scroll_to_top").Action,
            ignoreCase: true);
        Assert.Equal(
            "templates/independent/lineup_open_down.png",
            definition.GetTask("independent_lineup_collapse").Template);
        Assert.Equal(
            "templates/independent/lineup_closed_right.png",
            definition.GetTask("independent_strategy_return_probe").Template);
        Assert.Equal(
            "MatchTemplate",
            definition.GetTask("independent_strategy_change").Algorithm,
            ignoreCase: true);
        Assert.Equal(
            "templates/independent/strategy_change.png",
            definition.GetTask("independent_strategy_change").Template);
        Assert.Equal(
            "templates/independent/strategy_confirm.png",
            definition.GetTask("independent_strategy_save").Template);
        Assert.True(File.Exists(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "templates",
            "independent",
            "strategy_change.png")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "templates",
            "independent",
            "strategy_confirm.png")));

        var pipelineSource = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Services",
            "Training",
            "AdbCareerTrainingPipeline.cs"));
        var skillsIndex = pipelineSource.IndexOf(
            "if (!state.IndependentSkillsConfigured)",
            StringComparison.Ordinal);
        var scrollIndex = pipelineSource.IndexOf(
            "LineupScrollTopSemanticAction()",
            skillsIndex,
            StringComparison.Ordinal);
        var collapseIndex = pipelineSource.IndexOf(
            "LineupCollapseSemanticAction()",
            scrollIndex,
            StringComparison.Ordinal);
        var strategyIndex = pipelineSource.IndexOf(
            "StrategyChangeSemanticAction()",
            collapseIndex,
            StringComparison.Ordinal);
        var startIndex = pipelineSource.IndexOf(
            "if (!state.IndependentSetupCompleted)",
            strategyIndex,
            StringComparison.Ordinal);

        Assert.True(skillsIndex >= 0);
        Assert.True(skillsIndex < scrollIndex);
        Assert.True(scrollIndex < collapseIndex);
        Assert.True(collapseIndex < strategyIndex);
        Assert.True(strategyIndex < startIndex);
        Assert.Contains("settings.IndependentLineupStrategy", pipelineSource);
        Assert.Contains("TryGetLineupStrategyUiMapping", pipelineSource);
        Assert.Contains("IndependentLineupCollapsed", pipelineSource);
        Assert.Contains("IndependentStrategyConfigured", pipelineSource);
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
