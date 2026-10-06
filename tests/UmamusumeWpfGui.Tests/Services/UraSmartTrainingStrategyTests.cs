using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingStrategyTests
{
    [Theory]
    [InlineData(0, UraPlannedAction.Rest)]
    [InlineData(49, UraPlannedAction.Rest)]
    [InlineData(50, UraPlannedAction.Training)]
    [InlineData(51, UraPlannedAction.Training)]
    public async Task Energy_below_fifty_rests_without_entering_training(int energy, UraPlannedAction expected)
    {
        var module = new UraScenarioModule(await CareerTestResourceResolver.LoadBuiltInUraPackAsync());
        var state = module.CreateInitialState();
        state.Energy = UraObservedValueFactory.FromObservation(energy, 1);
        state.Mood = UraObservedValueFactory.FromObservation(CareerMood.Great, 1);
        var intent = new UraSmartTrainingStrategy().ChooseTurnAction(module, state);
        Assert.Equal(expected, intent.Action);
        if (expected == UraPlannedAction.Rest)
            Assert.Null(intent.TargetId);
    }

    [Fact]
    public async Task Low_energy_rests_even_when_normal_mood_would_choose_recreation()
    {
        var module = new UraScenarioModule(await CareerTestResourceResolver.LoadBuiltInUraPackAsync());
        var state = module.CreateInitialState();
        state.Energy = UraObservedValueFactory.FromObservation(49, 1);
        state.Mood = UraObservedValueFactory.FromObservation(CareerMood.Normal, 1);
        Assert.Equal(UraPlannedAction.Rest,
            new UraSmartTrainingStrategy().ChooseTurnAction(module, state).Action);
    }

    [Fact]
    public async Task Required_race_keeps_priority_over_low_energy_rest()
    {
        var module = new UraScenarioModule(await CareerTestResourceResolver.LoadBuiltInUraPackAsync());
        var state = module.CreateInitialState();
        state.Energy = UraObservedValueFactory.FromObservation(49, 1);
        state.HasPendingRace = true;
        state.ObservedGoalKind = CareerGoalTextParser.Race;
        Assert.Equal(UraPlannedAction.Race,
            new UraSmartTrainingStrategy().ChooseTurnAction(module, state).Action);
    }

    [Fact]
    public void Failure_rate_at_five_is_safe_but_higher_is_excluded()
    {
        var safe = UraTrainingCandidate.Reliable(
            "speed", 20, 0, 0, 0, 0, 0, 5);
        var risky = safe with { FailureRatePercent = 6 };

        Assert.True(UraSmartTrainingScorer.Score(
            safe, UraTrainingDistance.Middle, "junior", 80).ExclusionReason is null);
        Assert.Contains("exceeds", UraSmartTrainingScorer.Score(
            risky, UraTrainingDistance.Middle, "junior", 80).ExclusionReason);
    }

    [Fact]
    public void Zero_gain_candidate_is_excluded_even_when_auxiliary_bonus_is_present()
    {
        var candidate = new UraTrainingCandidate(
            "speed", 0, 0, 0, 0, 0, 0, 0,
            UnbondedSupportCount: 3,
            AuxiliaryConfidence: 1,
            CoreConfidence: 1,
            FailureRateConfidence: 1);

        var score = UraSmartTrainingScorer.Score(
            candidate, UraTrainingDistance.Middle, "junior", 40);

        Assert.Equal("expected gains are zero", score.ExclusionReason);
    }

    [Fact]
    public void Distance_weights_follow_global_distance_boundaries()
    {
        Assert.Equal(UraTrainingDistance.Sprint, UraSmartTrainingScorer.ParseDistance(null, 1400));
        Assert.Equal(UraTrainingDistance.Mile, UraSmartTrainingScorer.ParseDistance(null, 1600));
        Assert.Equal(UraTrainingDistance.Middle, UraSmartTrainingScorer.ParseDistance(null, 2000));
        Assert.Equal(UraTrainingDistance.Extended, UraSmartTrainingScorer.ParseDistance(null, 2500));
    }

    [Fact]
    public async Task Smart_strategy_prefers_stamina_for_long_distance()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        state.CurrentRaceId = "arima_kinen_goal";
        var strategy = new UraSmartTrainingStrategy();
        var decision = strategy.SelectCandidate(module, state,
        [
            UraTrainingCandidate.Reliable("speed", 15, 0, 0, 0, 0, 0, 0),
            UraTrainingCandidate.Reliable("stamina", 0, 15, 0, 0, 0, 0, 0),
        ]);

        Assert.Equal("stamina", decision.Candidate?.TrainingType);
    }

    [Fact]
    public async Task Smart_strategy_uses_rest_fallback_when_every_candidate_is_unknown()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        var strategy = new UraSmartTrainingStrategy();
        strategy.SelectCandidate(module, state,
            [new UraTrainingCandidate("speed", null, null, null, null, null, null, null)]);

        Assert.Equal(UraPlannedAction.Rest,
            strategy.ChooseTurnAction(module, state).Action);
        Assert.False(state.SmartTrainingFallbackPending);
    }

    [Fact]
    public async Task Smart_strategy_prepares_for_summer_camp_below_eighty_energy()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        state.Energy = UraObservedValueFactory.FromObservation(70, 1);
        state.TurnPositionLabel = "Classic Year Late June";

        Assert.Equal(UraPlannedAction.Rest,
            new UraSmartTrainingStrategy().ChooseTurnAction(module, state).Action);
    }

    [Fact]
    public void Stable_merge_does_not_turn_a_missing_ocr_value_into_zero()
    {
        var first = UraTrainingCandidate.Reliable("speed", 17, 0, 10, 0, 0, 6, 0);
        var second = first with { StaminaGain = null, CoreConfidence = 0 };

        var merged = UraSmartTrainingCandidateReader.MergeStable(first, second);

        Assert.Null(merged.StaminaGain);
        Assert.False(merged.HasReliableCoreValues);
    }

    [Fact]
    public void Distance_resolver_uses_selected_trainee_catalog_without_changing_state()
    {
        var trainee = new UmaTraineeRecord
        {
            TraineeId = 77,
            CareerObjectives =
            [
                new UmaCareerObjectiveRecord
                {
                    ObjectiveId = "goal",
                    Order = 1,
                    Turn = 12,
                    RaceIds = ["123"],
                },
            ],
        };
        var state = new UraCareerSessionState { TurnIndex = 12, CurrentRaceId = "pack_goal" };
        var resolver = new UraSmartTrainingDistanceResolver(
            trainee,
            [new UmaCareerRaceRecord
            {
                RaceId = 123,
                NameEn = "Long target",
                Distance = 2500,
                DistanceBand = "Long",
            }]);

        var race = resolver.Resolve(state);

        Assert.Equal("pack_goal", state.CurrentRaceId);
        Assert.Equal(2500, race?.Course.Distance);
        Assert.Equal("Long", race?.Course.DistanceBand);
    }

    [Fact]
    public async Task Confirmation_store_roundtrips_a_pending_guard()
    {
        var connection = new LastVerifiedConnection(
            "adb", "serial", Guid.NewGuid().ToString("N"), "version",
            900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        var expected = new UraSmartTrainingPendingConfirmation(
            77, "stamina", 14, DateTimeOffset.UtcNow);
        try
        {
            await UraSmartTrainingConfirmationStore.SaveAsync(
                connection, expected, CancellationToken.None);
            var actual = await UraSmartTrainingConfirmationStore.LoadAsync(
                connection, expected.TraineeId, CancellationToken.None);

            Assert.Equal(expected.TraineeId, actual?.TraineeId);
            Assert.Equal(expected.TrainingType, actual?.TrainingType);
            Assert.Equal(expected.TurnIndex, actual?.TurnIndex);
        }
        finally
        {
            await UraSmartTrainingConfirmationStore.ClearAsync(
                connection, expected.TraineeId);
        }
    }
}
