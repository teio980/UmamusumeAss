using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingConfirmationTests
{
    [Theory]
    [InlineData("speed")]
    [InlineData("stamina")]
    [InlineData("power")]
    [InlineData("guts")]
    [InlineData("wit")]
    [InlineData("speed", true)]
    public async Task Uses_original_offset_after_persisting_guard_and_never_confirms_twice(string type, bool interruptTap = false)
    {
        using var store = UraSmartTrainingConfirmationStore.UseDirectoryForTesting(
            System.IO.Path.Combine(AppContext.BaseDirectory, "smart-confirm-" + Guid.NewGuid().ToString("N")));
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var connection = new LastVerifiedConnection("offline", Guid.NewGuid().ToString("N"), "offline-test", "test",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, ConfirmationRuntime>();
        var fake = (ConfirmationRuntime)(object)visual;
        fake.Connection = connection;
        fake.TraineeId = 100602;
        fake.InterruptTap = interruptTap;
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), visual,
            new JsonSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smart-confirm-test-{Guid.NewGuid():N}.json")));
        var dispatcher = new CareerFlowDispatcher(visual, runner);
        var state = new UraCareerSessionState { TraineeId = fake.TraineeId, TurnIndex = 17, TurnsToGoal = 4 };
        var context = new CareerFlowContext(connection, pack, true, null!, null!, "", state,
            new CareerObservation("training_selection", 1), null, CancellationToken.None);
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), $"smart_runtime_20261006/{type}-{(type == "wit" ? 2 : 1)}.png"))!;
        var detectorRuntime = CareerTrainingClickGuardTests.TrainingFrameRuntime.Create(frame);
        var selection = await new UraSmartTrainingHeightDetector(detectorRuntime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        try
        {
            if (interruptTap)
                await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.RunConfirmedSmartTrainingSelectionAsync(context, type, selection));
            else
                Assert.Null(await dispatcher.RunConfirmedSmartTrainingSelectionAsync(context, type, selection));
            Assert.Equal(1, fake.Taps);
            Assert.True(fake.GuardExistedBeforeTap);
            if (interruptTap)
            {
                Assert.True(state.TrainingTurnCommitPending);
                Assert.Null(state.TrainingClickIssuedType);
                Assert.NotNull(await dispatcher.RunConfirmedSmartTrainingSelectionAsync(context, type, selection));
                Assert.Equal(1, fake.Taps);
                Assert.NotNull(await UraSmartTrainingConfirmationStore.LoadAsync(connection, fake.TraineeId, CancellationToken.None));
                return;
            }
            var expected = UraSmartTrainingConfirmationTap.Create(pack, connection, selection, type)!;
            Assert.Equal(expected, fake.Tap);
            Assert.Equal(type, state.TrainingClickIssuedType);
            Assert.Equal(UraPlannedAction.Training, state.PendingTurnAction);
            Assert.Equal(17, state.PendingActionTurnIndex);
            Assert.Equal(4, state.PendingActionTurnsToGoal);
            Assert.Null(await dispatcher.RunConfirmedSmartTrainingSelectionAsync(context, type, selection));
            Assert.Equal(1, fake.Taps);
        }
        finally
        {
            await UraSmartTrainingConfirmationStore.ClearAsync(connection, fake.TraineeId);
        }
    }

    [Fact]
    public async Task Invalid_winner_incomplete_logos_and_unraised_geometry_are_rejected()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var connection = new LastVerifiedConnection("offline", "offline", "offline", "test", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var matches = UraTrainingTypeCatalog.SupportedTypes.Select((type, index) => new UraTrainingLogoMatch(type,
            new(true, .99, 70 + index * 160, type == "speed" ? 1233 : 1287, 82, 52))).ToArray();
        var selection = new UraTrainingSelectionHeightResult("speed", matches, null);
        var tap = UraSmartTrainingConfirmationTap.Create(pack, connection, selection, "speed");
        Assert.NotNull(tap);
        Assert.Equal(matches[0].Match.CenterY + 54, tap.CenterY);
        Assert.Null(UraSmartTrainingConfirmationTap.Create(pack, connection, selection, "wit"));
        Assert.Null(UraSmartTrainingConfirmationTap.Create(pack, connection, selection with { Matches = matches[..4] }, "speed"));
        Assert.Null(UraSmartTrainingConfirmationTap.Create(pack, connection, selection with { ScreenChanged = true }, "speed"));
        var changed = matches.ToArray();
        changed[0] = matches[0] with { Match = matches[0].Match with { Found = false } };
        Assert.Null(UraSmartTrainingConfirmationTap.Create(pack, connection, selection with { Matches = changed }, "speed"));
        changed[0] = matches[0] with { Match = matches[0].Match with { Y = 1280 } };
        Assert.Null(UraSmartTrainingConfirmationTap.Create(pack, connection, selection with { Matches = changed }, "speed"));
    }

    public class ConfirmationRuntime : DispatchProxy
    {
        public int Taps { get; private set; }
        public TemplateMatchResult? Tap { get; private set; }
        public bool GuardExistedBeforeTap { get; private set; }
        public LastVerifiedConnection Connection { get; set; } = null!;
        public int TraineeId { get; set; }
        public bool InterruptTap { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "TapMatchAsync")
            {
                return TapAsync((TemplateMatchResult)args![1]!);
            }
            throw new InvalidOperationException($"Unnecessary screenshot, matching or device call: {targetMethod?.Name}");
        }

        private async Task TapAsync(TemplateMatchResult tap)
        {
            GuardExistedBeforeTap = await UraSmartTrainingConfirmationStore.LoadAsync(
                Connection, TraineeId, CancellationToken.None) is not null;
            Tap = tap;
            Taps++;
            if (InterruptTap)
                throw new OperationCanceledException("Offline simulated interruption after input.");
        }
    }
}
