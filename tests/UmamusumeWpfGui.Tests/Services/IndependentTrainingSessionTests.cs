using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingSessionTests
{
    [Fact]
    public void Session_serialization_contains_only_independent_progress_data()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.IndependentConfiguration,
            ConfigurationStep = IndependentTrainingConfigurationStep.LineupVerified,
            LastScreenId = "career_entry",
            RetryCount = 2,
            ActionsCompleted = 7,
        };

        var json = state.Serialize();

        Assert.Contains("\"Stage\":1", json);
        Assert.Contains("\"ConfigurationStep\":6", json);
        Assert.DoesNotContain("TurnIndex", json);
        Assert.DoesNotContain("Energy", json);
        Assert.DoesNotContain("HasPendingRace", json);
        Assert.DoesNotContain("CurrentObjectiveId", json);
        Assert.DoesNotContain("FinaleStageIndex", json);
    }

    [Fact]
    public void Resume_forces_live_lineup_verification_after_lineup_progress()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.IndependentConfiguration,
            ConfigurationStep = IndependentTrainingConfigurationStep.Strategy,
            LineupCollapseVerifiedThisRun = true,
        };

        state.NormalizeForResume();

        Assert.Equal(IndependentTrainingConfigurationStep.LineupPrepared, state.ConfigurationStep);
        Assert.False(state.LineupCollapseVerifiedThisRun);
    }

    [Fact]
    public void Resume_rewinds_incoherent_later_stage_to_last_trusted_stage()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.Completed,
            ConfigurationStep = IndependentTrainingConfigurationStep.Agenda,
        };

        state.NormalizeForResume();

        Assert.Equal(IndependentTrainingStage.IndependentConfiguration, state.Stage);
        Assert.Equal(IndependentTrainingConfigurationStep.Agenda, state.ConfigurationStep);
    }

    [Fact]
    public async Task New_checkpoint_wins_and_legacy_checkpoint_is_never_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"independent-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new IndependentCheckpointStore(100601, root);
            await File.WriteAllTextAsync(
                store.LegacyCheckpointPath,
                "{\"IndependentModeSelected\":true,\"IndependentLineupConfigured\":false,\"IndependentTrainingFocusConfigured\":true,\"IndependentAgendaConfigured\":true,\"IndependentSkillsConfigured\":true,\"IndependentLineupCollapsed\":true,\"IndependentStrategyConfigured\":true,\"IndependentSetupCompleted\":true}");

            var migrated = await store.LoadLegacyAsync();
            Assert.NotNull(migrated);
            Assert.Equal(IndependentTrainingStage.IndependentConfiguration, migrated!.Stage);
            Assert.Equal(IndependentTrainingConfigurationStep.LineupExpanded, migrated.ConfigurationStep);

            var completed = new IndependentTrainingSessionState
            {
                Stage = IndependentTrainingStage.Completed,
                ConfigurationStep = IndependentTrainingConfigurationStep.Strategy,
                LastScreenId = "home",
                ActionsCompleted = 12,
            };
            await store.SaveAsync(completed);

            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(IndependentTrainingStage.Completed, loaded!.Stage);
            Assert.Equal("home", loaded.LastScreenId);

            await store.ClearAsync();
            Assert.False(File.Exists(store.CheckpointPath));
            Assert.True(File.Exists(store.LegacyCheckpointPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
