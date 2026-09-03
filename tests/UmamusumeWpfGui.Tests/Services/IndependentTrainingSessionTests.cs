using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingSessionTests
{
    private static readonly string[] ExpectedStageNames =
    {
        "EnterCareer",
        "HandleExistingCareer",
        "SelectScenario",
        "SelectTrainee",
        "SelectLegacy",
        "SelectSupportDeck",
        "OpenFinalConfirmation",
        "SelectIndependentMode",
        "ExpandLineup",
        "ConfigureFocus",
        "ConfigureAgenda",
        "ConfigureSkills",
        "CollapseLineup",
        "ConfigureStrategy",
        "StartTraining",
        "HandlePostStartDialog",
        "ReturnHome",
        "Completed",
    };

    [Fact]
    public void Stage_is_the_single_ordered_progress_model()
    {
        Assert.Equal(ExpectedStageNames, Enum.GetNames<IndependentTrainingStage>());
    }

    [Fact]
    public void Session_serialization_contains_resume_cursors_and_no_flag_model()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureSkills,
            AgendaIndex = 3,
            SkillIndex = 5,
            CurrentSkillId = 12345,
            LastConfirmedScreen = "career_final_confirmation",
            Version = IndependentTrainingSessionState.CurrentCheckpointVersion,
            RetryCount = 2,
            ActionsCompleted = 7,
        };

        var json = state.Serialize();
        var roundTrip = IndependentTrainingSessionState.Deserialize(json);

        Assert.Contains("\"stage\":\"ConfigureSkills\"", json);
        Assert.Contains("\"agendaIndex\":3", json);
        Assert.Contains("\"skillIndex\":5", json);
        Assert.Contains("\"currentSkillId\":12345", json);
        Assert.Contains("\"lastConfirmedScreen\":\"career_final_confirmation\"", json);
        Assert.Contains("\"version\":1", json);
        Assert.DoesNotContain("ConfigurationStep", json);
        Assert.DoesNotContain("IndependentModeSelected", json);
        Assert.DoesNotContain("TurnIndex", json);
        Assert.Equal(IndependentTrainingStage.ConfigureSkills, roundTrip.Stage);
        Assert.Equal(3, roundTrip.AgendaIndex);
        Assert.Equal(5, roundTrip.SkillIndex);
        Assert.Equal(12345, roundTrip.CurrentSkillId);
    }

    [Fact]
    public void Resume_keeps_the_current_skill_cursor_only_for_skill_stage()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureSkills,
            SkillIndex = 5,
            CurrentSkillId = 12345,
        };

        state.NormalizeForResume();

        Assert.Equal(5, state.SkillIndex);
        Assert.Equal(12345, state.CurrentSkillId);

        state.Stage = IndependentTrainingStage.ConfigureStrategy;
        state.NormalizeForResume();

        Assert.Null(state.CurrentSkillId);
    }

    [Fact]
    public void Previous_two_part_checkpoint_is_migrated_to_one_stage()
    {
        var state = IndependentTrainingSessionState.Deserialize(
            "{\"Stage\":1,\"ConfigurationStep\":4,\"LastScreenId\":\"career_entry\",\"ActionsCompleted\":7}");

        Assert.Equal(IndependentTrainingStage.ConfigureSkills, state.Stage);
        Assert.Equal("career_entry", state.LastConfirmedScreen);
        Assert.Equal(7, state.ActionsCompleted);
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
            Assert.Equal(IndependentTrainingStage.ExpandLineup, migrated!.Stage);

            var completed = new IndependentTrainingSessionState
            {
                Stage = IndependentTrainingStage.Completed,
                LastConfirmedScreen = "home",
                ActionsCompleted = 12,
            };
            await store.SaveAsync(completed);

            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(IndependentTrainingStage.Completed, loaded!.Stage);
            Assert.Equal("home", loaded.LastConfirmedScreen);

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
