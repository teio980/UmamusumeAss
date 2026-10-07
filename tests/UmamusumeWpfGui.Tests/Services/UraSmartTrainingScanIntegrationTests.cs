using System.Collections.Concurrent;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingScanIntegrationTests
{
    [Theory]
    [InlineData("speed")]
    [InlineData("guts")]
    [InlineData("wit")]
    [InlineData("speed", true)]
    public async Task Full_smart_scan_uses_two_frames_per_card_and_one_fresh_winner_frame(
        string initial, bool cancelDuringScan = false)
    {
        using var store = UraSmartTrainingConfirmationStore.UseDirectoryForTesting(
            System.IO.Path.Combine(AppContext.BaseDirectory, "smart-scan-" + Guid.NewGuid().ToString("N")));
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var module = new UraScenarioModule(pack);
        var strategy = new UraSmartTrainingStrategy();
        var state = module.CreateInitialState();
        state.TraineeId = 100602;
        state.Energy = UraObservedValueFactory.FromObservation(58, 1);
        state.TurnIndex = 17;
        var connection = new LastVerifiedConnection("offline", Guid.NewGuid().ToString("N"), "offline", "test",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, ScanRuntime>();
        var fake = (ScanRuntime)(object)visual;
        using var cancellation = new CancellationTokenSource();
        if (cancelDuringScan)
            fake.CancelOnSecondCapture = cancellation;
        fake.Selected = initial;
        fake.Connection = connection;
        foreach (var type in UraTrainingTypeCatalog.SupportedTypes)
            fake.Frames[type] = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
                CareerTestResourceResolver.FindWorkspaceRoot(),
                type == "wit" && initial == "wit"
                    ? "smart_runtime_20261006/wit-duel-1.png"
                    : $"smart_runtime_20261006/{type}-{(type == "wit" ? 2 : 1)}.png"))!;
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), visual,
            new JsonSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"smart-scan-test-{Guid.NewGuid():N}.json")));
        var dispatcher = new CareerFlowDispatcher(visual, runner);
        var flow = new UraSmartTrainingSelectionFlow(visual, dispatcher);
        var context = new CareerFlowContext(connection, pack, true, module, strategy, "", state,
            new CareerObservation("training_selection", 1), null, cancellation.Token);
        try
        {
            if (cancelDuringScan)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.HandleAsync(context, strategy));
                Assert.Equal(2, fake.Captures);
                Assert.Equal(0, fake.Confirmations);
                Assert.Null(state.TrainingClickIssuedType);
                Assert.Null(await UraSmartTrainingConfirmationStore.LoadAsync(connection, 100602, CancellationToken.None));
                return;
            }
            Assert.Null(await flow.HandleAsync(context, strategy));
            Assert.Equal(11, fake.Captures);
            Assert.Equal(5, strategy.LastDecision!.ScoredCandidates.Count);
            Assert.All(strategy.LastDecision.ScoredCandidates,
                candidate => Assert.True(candidate.Candidate.IsSafe));
            Assert.Equal(initial, fake.CapturedTypes.First());
            Assert.Equal(2, fake.CapturedTypes.Take(10).Count(type => type == initial));
            Assert.All(UraTrainingTypeCatalog.SupportedTypes,
                type => Assert.Equal(2, fake.CapturedTypes.Take(10).Count(captured => captured == type)));
            Assert.Equal(1, fake.Confirmations);
            Assert.True(fake.GuardBeforeConfirmation);
            Assert.Equal(strategy.LastDecision.Candidate!.TrainingType, state.TrainingClickIssuedType);
            Assert.Equal(state.TrainingClickIssuedType, fake.CapturedTypes.Last());
            // Scanning a second time while the result is pending must not tap again.
            Assert.Null(await flow.HandleAsync(context, strategy));
            Assert.Equal(1, fake.Confirmations);
            Assert.Equal(11, fake.Captures);
        }
        finally
        {
            await UraSmartTrainingConfirmationStore.ClearAsync(connection, 100602);
        }
    }

    public class ScanRuntime : DispatchProxy
    {
        public Dictionary<string, GrayImage> Frames { get; } = [];
        public ConcurrentQueue<string> CapturedTypes { get; } = new();
        public string Selected { get; set; } = "speed";
        public LastVerifiedConnection Connection { get; set; } = null!;
        public int Captures { get; private set; }
        public int Confirmations { get; private set; }
        public bool GuardBeforeConfirmation { get; private set; }
        public CancellationTokenSource? CancelOnSecondCapture { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
            switch (targetMethod!.Name)
            {
                case "CaptureGrayAsync":
                    Captures++;
                    if (Captures == 2 && CancelOnSecondCapture is { } cancellation)
                    {
                        cancellation.Cancel();
                        ((CancellationToken)args[^1]!).ThrowIfCancellationRequested();
                    }
                    CapturedTypes.Enqueue(Selected);
                    var frame = Frames[Selected];
                    return Task.FromResult<GrayImage?>(frame with { Pixels = (byte[])frame.Pixels.Clone(),
                        RgbaPixels = (byte[])frame.RgbaPixels!.Clone() });
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile((string)args[0]!));
                case "DetectTextAsync":
                    return Task.FromResult<ScreenTextRecognitionResult?>(null);
                case "DelayAsync": return Task.CompletedTask;
                case "TapMatchAsync":
                    var task = (string)args[2]!;
                    if (task.EndsWith("_preview", StringComparison.Ordinal))
                    {
                        Selected = task.Split('_')[2];
                        return Task.CompletedTask;
                    }
                    return ConfirmAsync();
                default: throw new InvalidOperationException($"Unexpected device call: {targetMethod.Name}");
            }
        }

        private async Task ConfirmAsync()
        {
            GuardBeforeConfirmation = await UraSmartTrainingConfirmationStore.LoadAsync(
                Connection, 100602, CancellationToken.None) is not null;
            Confirmations++;
        }
    }
}
