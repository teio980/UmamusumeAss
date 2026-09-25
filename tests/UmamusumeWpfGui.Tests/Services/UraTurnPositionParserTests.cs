using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraTurnPositionParserTests
{
    [Theory]
    [InlineData("Junior Year Early Jul", UraCalendarStage.Regular)]
    [InlineData("Classic Year Late Jun", UraCalendarStage.Regular)]
    [InlineData("Classic Year Early Jul", UraCalendarStage.SummerCamp)]
    [InlineData("Classic Year Late Aug", UraCalendarStage.SummerCamp)]
    [InlineData("Classic Year Early Sep", UraCalendarStage.Regular)]
    [InlineData("Senior Year Early Jul", UraCalendarStage.SummerCamp)]
    [InlineData("Senior Year Late Aug", UraCalendarStage.SummerCamp)]
    [InlineData("Senior Year Early Sep", UraCalendarStage.Regular)]
    public void Tags_only_classic_and_senior_july_through_august_as_summer_camp(
        string text,
        UraCalendarStage expected)
    {
        Assert.True(UraTurnPositionParser.TryParse(text, out var position));
        Assert.Equal(expected, UraTurnPositionParser.GetCalendarStage(position));
    }

    [Theory]
    [InlineData("Junior Year Pre-Debut", 0, "junior", "pre-debut", null)]
    [InlineData("Junior Year Early Jan", 1, "junior", "early", 1)]
    [InlineData("Junior Year Late Jul", 14, "junior", "late", 7)]
    [InlineData("Classic Year Early Jan", 25, "classic", "early", 1)]
    [InlineData("SeniorYear Early Jan", 49, "senior", "early", 1)]
    [InlineData("Senior Year Late Dec", 72, "senior", "late", 12)]
    public void Parses_career_position_into_a_stable_schedule_index(
        string text,
        int expectedIndex,
        string expectedYear,
        string expectedPhase,
        int? expectedMonth)
    {
        var parsed = UraTurnPositionParser.TryParse(text, out var position);

        Assert.True(parsed);
        Assert.Equal(expectedIndex, position.TurnIndex);
        Assert.Equal(expectedYear, position.Year);
        Assert.Equal(expectedPhase, position.Phase);
        Assert.Equal(expectedMonth, position.Month);
    }

    [Theory]
    [InlineData("Junior Year Late Jui")]
    [InlineData("Junior Year Late Iul")]
    public void Tolerates_common_ocr_month_errors(string text)
    {
        Assert.True(UraTurnPositionParser.TryParse(text, out var position));
        Assert.Equal(14, position.TurnIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Junior Year")]
    [InlineData("Career")]
    public void Rejects_text_that_is_not_a_career_position(string? text)
    {
        Assert.False(UraTurnPositionParser.TryParse(text, out _));
    }

    [Fact]
    public async Task Career_main_profile_exposes_the_existing_top_left_ocr_region()
    {
        var root = FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var careerMain = pack.ScreenProfile.Find("career_main");

        var region = careerMain?.FindOcrRegion("scenario.phase");

        Assert.NotNull(region);
        var bounds = region!.Bounds;
        Assert.NotNull(bounds);
        Assert.Equal(15, bounds!.X);
        Assert.Equal(45, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(45, bounds.Height);

        var turnsLeft = careerMain?.FindOcrRegion("objective.turns_left");
        Assert.NotNull(turnsLeft);
        Assert.Equal(10, turnsLeft!.Bounds!.X);
        Assert.Equal(90, turnsLeft.Bounds.Y);
        Assert.Equal(175, turnsLeft.Bounds.Width);
        Assert.Equal(140, turnsLeft.Bounds.Height);
    }

    [Fact]
    public async Task Observing_career_main_reconstructs_turn_index_from_the_label()
    {
        var root = FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();

        module.ObserveScreen(
            state,
            "career_main",
            0.98,
            energyPercent: 63,
            energyConfidence: 0.94,
            turnPositionText: "Junior Year Late Jul",
            turnsToGoal: 7,
            goalText: "Earn 3000 fans Progress 1731 fan(s) to go");

        Assert.Equal(14, state.TurnIndex);
        Assert.Equal("Junior Year Late Jul", state.TurnPositionLabel);
        Assert.Equal(UraStateSource.Observed, state.TurnIndexSource);
        Assert.Equal(63, state.Energy.Value);
        Assert.Equal(0.94, state.Energy.Confidence);
        Assert.Equal(7, state.TurnsToGoal);
        Assert.Equal(
            "Earn 3000 fans Progress 1731 fan(s) to go",
            state.ObservedGoalText);
        Assert.Equal(CareerGoalTextParser.Fans, state.ObservedGoalKind);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository workspace.");
    }
}
