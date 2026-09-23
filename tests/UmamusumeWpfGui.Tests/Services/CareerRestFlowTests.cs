using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRestFlowTests
{
    [Fact]
    public void Rest_waits_for_a_new_turn_and_refreshed_energy_before_another_action()
    {
        var state = new UraCareerSessionState
        {
            AwaitingRestReturn = true,
            RestStartedTurnIndex = 1,
            RestStartedEnergyPercent = 39,
        };

        Assert.False(CareerRestReturnGate.HasReachedNextTurn(
            state, new CareerObservation("career_main", 1, 39, 0.9,
                "Junior Year Early Jan")));
        Assert.False(CareerRestReturnGate.HasReachedNextTurn(
            state, new CareerObservation("career_main", 1, 80, 0.9,
                "Junior Year Early Jan")));
        Assert.False(CareerRestReturnGate.HasReachedNextTurn(
            state, new CareerObservation("career_main", 1, 39, 0.9,
                "Junior Year Late Jan")));
        Assert.True(state.AwaitingRestReturn);

        Assert.True(CareerRestReturnGate.HasReachedNextTurn(
            state, new CareerObservation("career_main", 1, 80, 0.9,
                "Junior Year Late Jan")));
        Assert.False(state.AwaitingRestReturn);
    }

    [Fact]
    public async Task Rest_does_not_require_a_transient_result_screen()
    {
        var pack = await LoadPackAsync();

        Assert.Equal(
            "rest_confirmation_rest_confirm",
            pack.ScreenProfile.Find("rest_confirmation")?.FindAction("confirm")?.Task);
        Assert.Null(pack.ScreenProfile.Find("rest_result"));
        Assert.False(pack.ExecutionDefinition.TryGetTask("rest_result_event_advance", out _));
        Assert.Equal(CareerScreenKind.Unknown, CareerScreenClassification.Classify("rest_result"));
    }

    [Fact]
    public async Task Return_to_main_reobserves_energy_and_turn_after_rest()
    {
        var pack = await LoadPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();

        scenario.ObserveScreen(
            state, "career_main", 0.98,
            energyPercent: 30,
            energyConfidence: 0.92,
            turnPositionText: "Junior Year Early Jan");
        scenario.ObserveScreen(state, "rest_confirmation", 0.98);
        scenario.ObserveScreen(
            state, "career_main", 0.98,
            energyPercent: 80,
            energyConfidence: 0.93,
            turnPositionText: "Junior Year Late Jan");

        Assert.Equal(80, state.Energy.Value);
        Assert.Equal(2, state.TurnIndex);
        Assert.Equal("career_main", state.LastScreenId);
    }

    [Theory]
    [InlineData("career_main_after_rest.png", true)]
    [InlineData("rest_result.png", false)]
    [InlineData("rest_confirmation.png", false)]
    public void Only_the_returned_main_screen_has_a_visible_training_action(
        string frameName,
        bool expected)
    {
        var screens = Path.Combine(FindWorkspaceRoot(), "resource", "hachimi", "ura", "screens");
        var frame = GrayImageCodec.FromFile(Path.Combine(screens, "captures", frameName));
        var template = GrayImageCodec.FromFile(Path.Combine(
            screens, "templates", "career_main_action_training.png"));

        Assert.NotNull(frame);
        Assert.NotNull(template);
        var match = TemplateMatcher.Find(frame!, template!, [200, 1050, 500, 400], 0.78, 900, 1600);

        Assert.Equal(expected, match.Found);
    }

    [Fact]
    public void Rest_event_does_not_look_like_the_training_action()
    {
        var screens = Path.Combine(FindWorkspaceRoot(), "resource", "hachimi", "ura", "screens");
        var frame = GrayImageCodec.FromFile(Path.Combine(
            screens, "templates", "runtime_frames", "rest_event_ura.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(
            screens, "templates", "career_main_action_training.png"));
        var header = GrayImageCodec.FromFile(Path.Combine(
            screens, "templates", "career_main_header.png"));

        Assert.NotNull(frame);
        Assert.NotNull(template);
        Assert.NotNull(header);
        Assert.True(TemplateMatcher.Find(frame!, header!, [0, 0, 120, 50], 0.92, 900, 1600).Found);
        var match = TemplateMatcher.Find(frame!, template!, [200, 1050, 500, 400], 0.78, 900, 1600);
        Assert.False(match.Found, $"Rest event matched Training at {match.Score:0.000}.");
    }

    [Fact]
    public async Task Transient_rest_result_is_not_an_actionable_event_or_confirmation()
    {
        var pack = await LoadPackAsync();
        var screens = Path.Combine(FindWorkspaceRoot(), "resource", "hachimi", "ura", "screens");
        var frame = GrayImageCodec.FromFile(Path.Combine(screens, "captures", "rest_result.png"));
        Assert.NotNull(frame);

        foreach (var screenId in new[]
                 {
                     "event_choice", "training_event", "scenario_event",
                     "rest_confirmation", "training_result",
                 })
        {
            var screen = pack.ScreenProfile.Find(screenId);
            Assert.NotNull(screen);
            foreach (var path in screen.Templates)
            {
                var template = GrayImageCodec.FromFile(Path.Combine(
                    screens, path.Replace('/', Path.DirectorySeparatorChar)));
                Assert.NotNull(template);
                var match = TemplateMatcher.Find(
                    frame!, template!, screen.Recognition.Roi,
                    screen.Recognition.TemplateThreshold, 900, 1600);
                Assert.False(match.Found,
                    $"Transient rest result matched {screenId} via {path} ({match.Score:0.000}).");
            }
        }
    }

    private static async Task<UraScenarioPack> LoadPackAsync() =>
        await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "resource", "hachimi", "ura", "manifest.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
}
