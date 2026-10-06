using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingResumeTests
{
    [Theory]
    [InlineData("career_main", true, null, false, true)]
    [InlineData("training_selection", true, null, false, false)]
    [InlineData("training_result", true, null, false, false)]
    [InlineData("unknown", true, null, false, false)]
    [InlineData("career_main", false, null, false, false)]
    [InlineData("career_main", true, "stamina", false, false)]
    [InlineData("career_main", true, null, true, false)]
    public void Only_restored_confirmation_at_observed_main_can_be_reset(
        string screen, bool restored, string? clickedType, bool actionPending, bool expected)
    {
        var state = new UraCareerSessionState
        {
            // The real Finale screen has no parseable calendar date.
            TurnIndexSource = UraStateSource.Unknown,
            TrainingTurnCommitPending = true,
            TrainingTurnCommitType = "stamina",
            TrainingTurnCommitTurnIndex = 71,
            TrainingClickIssuedType = clickedType,
            PendingTurnAction = actionPending ? UraPlannedAction.Training : null,
        };
        var strategy = new UraRatioStrategy(new UraTrainingRatio(1, 1, 0, 0, 0));
        var session = new CareerSessionState<UraCareerSessionState>
        {
            Runtime = state.Runtime,
            Scenario = state,
        };

        Assert.Equal(expected, CareerTrainingEngine.TryReconcileResumedTrainingConfirmation(
            state, new CareerObservation(screen, 1), restored));
        if (expected)
        {
            CareerTrainingEngine.ConfirmTrainingIfConsumed(
                strategy, session, state, new CareerObservation(screen, 1));
            Assert.Null(state.TrainingTurnCommitType);
            Assert.Null(state.TrainingTurnCommitTurnIndex);
            Assert.Equal(0, strategy.NextTrainingIndex);
        }
        Assert.Equal(!expected, state.TrainingTurnCommitPending);
    }

    [Fact]
    public async Task Captured_finale_main_clears_restored_guard_before_opening_training()
    {
        using var store = UraSmartTrainingConfirmationStore.UseDirectoryForTesting(
            Path.Combine(AppContext.BaseDirectory, "smart-resume-" + Guid.NewGuid().ToString("N")));
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "smart_resume_20261006/finale-semifinal-main.png"))!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connection = new LastVerifiedConnection("offline", Guid.NewGuid().ToString("N"),
            "resume-test", "android", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, ResumeRuntime>();
        var fake = (ResumeRuntime)(object)visual;
        fake.Frame = frame;
        fake.Cancellation = cancellation;
        fake.ExpectedTask = "career_main_action_training";
        fake.Connection = connection;
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"), cancellation.Token);
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), $"smart-resume-{Guid.NewGuid():N}.json")));
        var navigator = new CareerEntryNavigator(visual, database,
            new UraTraineeSelector(visual, database), new UraLegacySelector(visual, runner),
            new CareerJsonActionExecutor(runner));
        var engine = new CareerTrainingEngine(visual, database, navigator, runner);
        var settings = new CareerTrainingSettings(Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            100602, true, [], "auto", "", null, UraSmartTrainingStrategy.StrategyId,
            true, false, "", false, false, [], []);
        await UraSmartTrainingConfirmationStore.SaveAsync(connection,
            new(100602, "stamina", 71, DateTimeOffset.UtcNow), cancellation.Token);
        try
        {
            var result = await engine.RunAsync(connection, settings, null, cancellationToken: cancellation.Token);

            Assert.Equal("canceled", result.LastScreenId);
            Assert.Equal(["career_main_action_training"], fake.TappedTasks);
            Assert.True(fake.GuardClearedBeforeTap);
            Assert.Null(await UraSmartTrainingConfirmationStore.LoadAsync(connection, 100602, CancellationToken.None));
        }
        finally
        {
            await UraSmartTrainingConfirmationStore.ClearAsync(connection, 100602);
        }
    }

    public class ResumeRuntime : CareerGoalResumeTests.ResumeVisualRuntime
    {
        public LastVerifiedConnection Connection { get; set; } = null!;
        public bool GuardClearedBeforeTap { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "TapMatchAsync")
                return TapAsync((string)args![2]!);
            return base.Invoke(targetMethod, args);
        }

        private async Task TapAsync(string task)
        {
            Assert.Equal(ExpectedTask, task);
            GuardClearedBeforeTap = await UraSmartTrainingConfirmationStore.LoadAsync(
                Connection, 100602, CancellationToken.None) is null;
            TappedTasks.Add(task);
            Cancellation.Cancel();
        }
    }
}
