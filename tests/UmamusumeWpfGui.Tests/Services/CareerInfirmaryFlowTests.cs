using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerInfirmaryFlowTests
{
    [Theory]
    [InlineData("infirmary_current.png", true)]
    [InlineData("infirmary_unavailable_reference.png", false)]
    [InlineData("infirmary_unavailable_shifted.png", false)]
    public async Task Lit_infirmary_is_distinguished_from_the_dim_button(
        string frameName,
        bool expected)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        Assert.True(CareerInfirmaryDetector.TryGetVisualPolicy(pack.ScreenProfile, out var policy));
        Assert.NotNull(policy);
        var visualPolicy = policy!;
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, frameName));
        var available = Load(visualPolicy.AvailableTemplatePath);
        var unavailable = Load(visualPolicy.UnavailableTemplatePath);

        Assert.Equal((150, 42), (available.Width, available.Height));
        Assert.Equal((available.Width, available.Height),
            (unavailable.Width, unavailable.Height));

        Assert.Equal(expected, CareerInfirmaryDetector.IsAvailable(
            frame, available, unavailable, 900, 1600, visualPolicy));
    }

    [Fact]
    public async Task Stable_main_with_lit_infirmary_treats_before_the_turn_strategy()
    {
        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);
        var state = new UraCareerSessionState();
        var context = Context(state, new CareerObservation(
            "career_main", 0.99, InfirmaryAvailable: true));

        var result = await flow.HandleAsync(context);

        Assert.Null(result);
        Assert.Equal(("career_main", "action.infirmary"), actions.LastAction);
        Assert.Equal(UraPlannedAction.Infirmary, state.LastAction);

        await flow.HandleAsync(Context(state,
            new CareerObservation("infirmary_confirmation", 0.99)));
        Assert.Equal(("infirmary_confirmation", "infirmary.confirm"), actions.LastAction);
    }

    [Fact]
    public async Task Summer_camp_position_is_recognized_and_clickable()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        Assert.True(CareerInfirmaryDetector.TryGetVisualPolicy(pack.ScreenProfile, out var policy));
        Assert.NotNull(policy);
        var visualPolicy = policy!;
        Assert.True(pack.ExecutionDefinition.TryGetTask(
            "career_main_action_infirmary", out var task));
        Assert.NotNull(task);

        var dimFrame = Load(CareerTestResourceResolver.FindUraCapture(root, "infirmary_unavailable_shifted.png"));
        var available = Load(visualPolicy.AvailableTemplatePath);
        var unavailable = Load(visualPolicy.UnavailableTemplatePath);
        var shifted = TemplateMatcher.FindColor(dimFrame, unavailable,
            task.Roi, 0.95, 900, 1600);
        Assert.True(shifted.Found);
        Assert.Equal(260, shifted.X);
        Assert.Equal(1420, shifted.Y);

        // The current live screen is dim. Replace only its button label with
        // the captured lit pixels to check both detection and click placement.
        var pixels = (byte[])dimFrame.RgbaPixels!.Clone();
        for (var row = 0; row < available.Height; row++)
        {
            Buffer.BlockCopy(available.RgbaPixels!, row * available.Width * 4,
                pixels, ((shifted.Y + row) * dimFrame.Width + shifted.X) * 4,
                available.Width * 4);
        }
        var shiftedLitFrame = new GrayImage(
            dimFrame.Width, dimFrame.Height, dimFrame.Pixels, pixels);

        Assert.True(CareerInfirmaryDetector.IsAvailable(
            shiftedLitFrame, available, unavailable, 900, 1600, visualPolicy));
        var click = TemplateMatcher.FindColor(shiftedLitFrame, available,
            task.Roi, task.TemplateThreshold, 900, 1600);
        Assert.True(click.Found);
        Assert.Equal(260, click.X);
    }

    [Fact]
    public async Task Confirmation_title_and_ok_are_bound_to_the_captured_dialog()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var dialog = pack.ScreenProfile.Find("infirmary_confirmation");
        Assert.NotNull(dialog);
        Assert.Equal("infirmary_confirmation_ok", dialog.FindAction("infirmary.confirm")?.Task);

        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "infirmary_confirm.png"));
        var title = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, dialog, dialog.Recognition.Template!));
        var match = TemplateMatcher.Find(frame, title, dialog.Recognition.Roi,
            dialog.Recognition.TemplateThreshold, 900, 1600);
        Assert.True(match.Found, $"Title match score={match.Score:0.000} at {match.X},{match.Y}.");

        var main = Load(CareerTestResourceResolver.FindUraCapture(root, "infirmary_current.png"));
        Assert.False(TemplateMatcher.Find(main, title, dialog.Recognition.Roi,
            dialog.Recognition.TemplateThreshold, 900, 1600).Found);

        Assert.True(pack.ExecutionDefinition.TryGetTask(
            "career_main_action_infirmary", out var infirmaryTask));
        Assert.NotNull(infirmaryTask);
        var available = Load(CareerTestResourceResolver.ResolveUraVisualResource(
            pack, "career.infirmary.available"));
        Assert.True(TemplateMatcher.FindColor(main, available,
            infirmaryTask.Roi, infirmaryTask.TemplateThreshold, 900, 1600).Found);
        var dimMain = Load(CareerTestResourceResolver.FindUraCapture(
            root, "infirmary_unavailable_reference.png"));
        Assert.False(TemplateMatcher.FindColor(dimMain, available,
            infirmaryTask.Roi, infirmaryTask.TemplateThreshold, 900, 1600).Found);

        Assert.True(pack.ExecutionDefinition.TryGetTask(
            "infirmary_confirmation_ok", out var okTask));
        Assert.NotNull(okTask);
        var okTemplate = Load(CareerTestResourceResolver.ResolveUraVisualResource(
            pack, okTask.Template!));
        Assert.True(TemplateMatcher.FindColor(frame, okTemplate,
            okTask.Roi, okTask.TemplateThreshold, 900, 1600).Found);
    }

    [Fact]
    public async Task Submitted_infirmary_confirmation_is_not_sent_twice()
    {
        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);
        var state = new UraCareerSessionState();
        var context = Context(state, new CareerObservation("infirmary_confirmation", 1));

        Assert.Null(await flow.HandleAsync(context));
        var transition = state.Runtime.TurnActionTransition;
        Assert.NotNull(transition);
        Assert.True(transition.AwaitingConfirmation);
        Assert.Null(await flow.HandleAsync(context));
        Assert.Equal(1, actions.CallCount);
        Assert.Same(transition, state.Runtime.TurnActionTransition);
    }

    private static CareerFlowContext Context(
        UraCareerSessionState state,
        CareerObservation observation) =>
        new(null!, null!, true, null!, null!, string.Empty, state,
            observation, null, CancellationToken.None);

    private static GrayImage Load(string path) =>
        GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing test image", path);

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public (string ScreenId, string ActionId)? LastAction { get; private set; }
        public int CallCount { get; private set; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            LastAction = (screenId, actionId);
            CallCount++;
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }
}
