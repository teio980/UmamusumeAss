using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSummerRestFlowTests
{
    [Fact]
    public async Task Summer_stage_selects_its_rest_button_and_regular_stage_keeps_rest()
    {
        var pack = await LoadPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();
        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);
        var strategy = new UraDefaultStrategy();

        scenario.ObserveScreen(state, "career_main", 0.98,
            energyPercent: 30, energyConfidence: 0.95,
            turnPositionText: "Classic Year Early Jul");
        Assert.Equal(UraCalendarStage.SummerCamp, state.CalendarStage);
        var context = new CareerFlowContext(null!, pack, true, scenario,
            strategy, string.Empty, state,
            new CareerObservation("career_main", 0.98, EnergyPercent: 30),
            null, CancellationToken.None);
        Assert.Null(await flow.HandleAsync(context));
        Assert.Equal(("career_main", "action.summer_rest"), actions.LastAction);

        scenario.ObserveScreen(state, "career_main", 0.98,
            energyPercent: 30, energyConfidence: 0.95,
            turnPositionText: "Classic Year Early Sep");
        Assert.Equal(UraCalendarStage.Regular, state.CalendarStage);
        Assert.Null(await flow.HandleAsync(context));
        Assert.Equal(("career_main", "action.rest"), actions.LastAction);
    }

    [Fact]
    public async Task Background_free_summer_button_matches_only_the_summer_main_screen()
    {
        var pack = await LoadPackAsync();
        Assert.Equal("career_main_action_summer_rest",
            pack.ScreenProfile.Find("career_main")?.FindAction("action.summer_rest")?.Task);
        Assert.True(pack.ExecutionDefinition.TryGetTask(
            "career_main_action_summer_rest", out var task));
        Assert.NotNull(task);
        var template = Load(CareerTestResourceResolver.ResolveUraVisualResource(
            pack, task.Template!));
        Assert.Equal((162, 56), (template.Width, template.Height));

        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var summer = Load(CareerTestResourceResolver.FindUraCapture(root, "summer_rest_current.png"));
        var regular = Load(CareerTestResourceResolver.FindUraCapture(root, "rest_before_low_energy.png"));
        Assert.True(TemplateMatcher.FindColor(summer, template, task.Roi,
            task.TemplateThreshold, 900, 1600).Found);
        Assert.False(TemplateMatcher.FindColor(regular, template, task.Roi,
            task.TemplateThreshold, 900, 1600).Found);
    }

    [Fact]
    public async Task Summer_confirmation_reuses_rest_ok_and_disappearance_gate()
    {
        var pack = await LoadPackAsync();
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var dialog = pack.ScreenProfile.Find("summer_rest_confirmation");
        Assert.NotNull(dialog);
        Assert.Equal("rest_confirmation_rest_confirm",
            dialog.FindAction("rest.confirm")?.Task);
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "summer_rest_dialog.png"));
        var title = Load(pack.VisualResources!.ResolveScreenTemplate(
            dialog, dialog.Recognition.Template!));
        var titleMatch = TemplateMatcher.Find(frame, title,
            dialog.Recognition.Roi, dialog.Recognition.TemplateThreshold, 900, 1600);
        Assert.True(titleMatch.Found,
            $"Summer Rest dialog title score={titleMatch.Score:0.000}.");
        var regularDialog = Load(CareerTestResourceResolver.FindUraCapture(root, "rest_confirmation.png"));
        Assert.False(TemplateMatcher.Find(regularDialog, title,
            dialog.Recognition.Roi, dialog.Recognition.TemplateThreshold,
            900, 1600).Found);

        Assert.True(pack.ExecutionDefinition.TryGetTask(
            "rest_confirmation_rest_confirm", out var okTask));
        Assert.NotNull(okTask);
        var okTemplate = Load(CareerTestResourceResolver.ResolveUraVisualResource(
            pack, okTask.Template!));
        Assert.False(CareerRestConfirmationGate.HasOkDisappeared(
            frame, okTemplate, okTask, 900, 1600));
        var main = Load(CareerTestResourceResolver.FindUraCapture(root, "summer_rest_current.png"));
        Assert.True(CareerRestConfirmationGate.HasOkDisappeared(
            main, okTemplate, okTask, 900, 1600));

        Assert.Equal(CareerScreenKind.Turn,
            CareerScreenClassification.Classify("summer_rest_confirmation"));
        var actions = new RecordingActions();
        var state = new UraCareerSessionState();
        var context = new CareerFlowContext(null!, pack, true, null!, null!,
            string.Empty, state,
            new CareerObservation("summer_rest_confirmation", 0.98),
            null, CancellationToken.None);
        Assert.Null(await new CareerTurnFlow(actions).HandleAsync(context));
        Assert.Equal(("summer_rest_confirmation", "rest.confirm"), actions.LastAction);
        Assert.True(state.AwaitingRestConfirmationGone);
    }

    private static GrayImage Load(string path) =>
        GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing test image", path);

    private static Task<UraScenarioPack> LoadPackAsync() =>
        CareerTestResourceResolver.LoadBuiltInUraPackAsync();

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public (string ScreenId, string ActionId)? LastAction { get; private set; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            LastAction = (screenId, actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }
}
