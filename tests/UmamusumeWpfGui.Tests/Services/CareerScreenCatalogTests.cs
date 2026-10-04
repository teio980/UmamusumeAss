using System.Text.Json;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerScreenCatalogTests
{
    [Theory]
    [InlineData("career_main", "main", CareerScreenKind.Main)]
    [InlineData("rest_confirmation", "turn", CareerScreenKind.Turn)]
    [InlineData("training_event", "event", CareerScreenKind.Event)]
    [InlineData("race_runner", "race", CareerScreenKind.Race)]
    [InlineData("career_result", "settlement", CareerScreenKind.Settlement)]
    [InlineData("career_entry_career", "entry", CareerScreenKind.Unknown)]
    [InlineData("normal_mode_select", "normal", CareerScreenKind.Unknown)]
    [InlineData("independent_agenda", "independent", CareerScreenKind.Unknown)]
    public void ProfileFlowOwnsClassification(
        string screenId,
        string flow,
        CareerScreenKind expected)
    {
        var profile = new UraScreenProfile
        {
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = screenId,
                    Flow = flow,
                },
            ],
        };

        Assert.Equal(expected, CareerScreenClassification.Classify(screenId, profile));
    }

    [Fact]
    public void RuntimeClassificationUsesTheBuiltInScreenCatalog()
    {
        Assert.Equal(CareerScreenKind.Main, CareerScreenClassification.Classify("career_main"));
        Assert.Equal(CareerScreenKind.Turn, CareerScreenClassification.Classify("training_selection"));
        Assert.Equal(CareerScreenKind.Race, CareerScreenClassification.Classify("race_runner"));
        Assert.Equal(CareerScreenKind.Event, CareerScreenClassification.Classify("event_choice"));
        Assert.Equal(CareerScreenKind.Settlement, CareerScreenClassification.Classify("career_result"));
        Assert.Equal(CareerScreenKind.Unknown, CareerScreenClassification.Classify("normal_quick_mode_settings"));
    }

    [Fact]
    public void ProfileRecognitionPriorityIsUsedWithoutASecondObserverCatalog()
    {
        var screen = new UraScreenDefinition
        {
            ScreenId = "custom_overlay",
            Recognition = new UraScreenRecognition { Priority = 3 },
        };

        Assert.Equal(3, CareerScreenClassification.GetRecognitionPriority(screen));
    }

    [Fact]
    public void ObservationKindCanBeStampedWithoutChangingItsConstructor()
    {
        var observation = new CareerObservation("custom_overlay", 0.99)
        {
            ClassifiedKind = CareerScreenKind.Event,
        };

        Assert.Equal(CareerScreenKind.Event, observation.Kind);
    }

    [Fact]
    public void NestedCanonicalStateKeepsTheLegacyFlatCheckpointShape()
    {
        var state = new UraCareerSessionState
        {
            ScenarioId = "ura",
            PhaseId = "finale_underway",
            TurnIndex = 42,
            CareerStarted = true,
            LastScreenId = "race_runner",
            LastAction = UraPlannedAction.Race,
            NormalSetupStage = NormalCareerSetupStage.InCareer,
            PendingTurnAction = UraPlannedAction.Race,
            RaceRetryDeclined = true,
            TrainingClickIssuedType = "speed",
            RaceRetryDialogWaitCount = 2,
        };
        var energy = UraObservedValueFactory.FromObservation(67, 0.94);
        state.Runtime.Energy = energy;
        state.Setup.Stage = NormalCareerSetupStage.ConfirmQuickMode;
        state.Flows.HasPendingRace = true;

        var session = new CareerSessionState<UraCareerSessionState>
        {
            Scenario = state,
            Runtime = state.Runtime,
        };
        Assert.Same(state.Runtime, session.Runtime);
        Assert.Same(session.Runtime, session.Scenario.Runtime);
        Assert.Same(energy, state.Energy);
        Assert.Equal(NormalCareerSetupStage.ConfirmQuickMode, state.NormalSetupStage);
        Assert.True(state.HasPendingRace);

        var json = JsonSerializer.Serialize(state);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("ScenarioId", out _));
        Assert.True(root.TryGetProperty("PhaseId", out _));
        Assert.True(root.TryGetProperty("TurnIndex", out _));
        Assert.True(root.TryGetProperty("Energy", out _));
        Assert.True(root.TryGetProperty("NormalSetupStage", out _));
        Assert.True(root.TryGetProperty("HasPendingRace", out _));
        Assert.True(root.TryGetProperty("RaceRetryDeclined", out _));
        Assert.False(root.TryGetProperty("Runtime", out _));
        Assert.False(root.TryGetProperty("Setup", out _));
        Assert.False(root.TryGetProperty("Flows", out _));
        Assert.False(root.TryGetProperty("TrainingClickIssuedType", out _));
        Assert.False(root.TryGetProperty("RaceRetryDialogWaitCount", out _));

        var restored = JsonSerializer.Deserialize<UraCareerSessionState>(json)!;
        Assert.Equal("finale_underway", restored.Runtime.PhaseId);
        Assert.Equal(42, restored.Runtime.TurnIndex);
        Assert.Equal(UraPlannedAction.Race, restored.Runtime.LastAction);
        Assert.Equal(67, restored.Runtime.Energy.Value);
        Assert.Equal(NormalCareerSetupStage.ConfirmQuickMode, restored.Setup.Stage);
        Assert.True(restored.Flows.HasPendingRace);
        Assert.Equal(UraPlannedAction.Race, restored.PendingTurnAction);
        Assert.True(restored.RaceRetryDeclined);
        Assert.Null(restored.TrainingClickIssuedType);
        Assert.Equal(0, restored.RaceRetryDialogWaitCount);
    }
}
