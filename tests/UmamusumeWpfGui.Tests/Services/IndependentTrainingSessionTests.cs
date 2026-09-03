using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingSessionTests
{
    private static readonly string[] ExpectedCheckpointFields =
    {
        "agendaIndex",
        "currentSkillId",
        "lastConfirmedScreen",
        "skillIndex",
        "stage",
        "version",
    };

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
    public void Session_serialization_contains_only_checkpoint_fields()
    {
        var state = new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureSkills,
            AgendaIndex = 3,
            SkillIndex = 5,
            CurrentSkillId = 12345,
            LastConfirmedScreen = "career_final_confirmation",
            Version = IndependentTrainingSessionState.CurrentCheckpointVersion,
        };

        var json = state.Serialize();
        var roundTrip = IndependentTrainingSessionState.Deserialize(json);
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedCheckpointFields,
            fields);
        Assert.Equal(IndependentTrainingStage.ConfigureSkills, roundTrip.Stage);
        Assert.Equal(3, roundTrip.AgendaIndex);
        Assert.Equal(5, roundTrip.SkillIndex);
        Assert.Equal(12345, roundTrip.CurrentSkillId);
    }

    [Fact]
    public void Legacy_runtime_counters_are_ignored_and_not_written_back()
    {
        var state = IndependentTrainingSessionState.Deserialize(
            "{\"Version\":1,\"Stage\":\"ConfigureSkills\","
            + "\"AgendaIndex\":3,\"SkillIndex\":5,\"CurrentSkillId\":12345,"
            + "\"LastConfirmedScreen\":\"career_final_confirmation\","
            + "\"RetryCount\":9,\"ActionsCompleted\":42}");

        Assert.Equal(IndependentTrainingStage.ConfigureSkills, state.Stage);
        Assert.Equal(3, state.AgendaIndex);
        Assert.Equal(5, state.SkillIndex);
        Assert.Equal(12345, state.CurrentSkillId);
        var rewritten = state.Serialize();
        Assert.DoesNotContain("RetryCount", rewritten);
        Assert.DoesNotContain("ActionsCompleted", rewritten);
        Assert.DoesNotContain("retryCount", rewritten);
        Assert.DoesNotContain("actionsCompleted", rewritten);
    }

    [Fact]
    public void Runtime_context_starts_at_zero_for_each_run()
    {
        var runtime = new IndependentTrainingRuntimeContext();
        runtime.AdoptEntryProgress(new CareerEntryNavigationState
        {
            RetryCount = 3,
            ActionsCompleted = 8,
        });
        runtime.RecordActionFailure();

        Assert.Equal(4, runtime.RetryCount);
        Assert.Equal(8, runtime.ActionsCompleted);

        var restarted = new IndependentTrainingRuntimeContext();
        Assert.Equal(0, restarted.RetryCount);
        Assert.Equal(0, restarted.ActionsCompleted);
    }

    [Fact]
    public void Future_checkpoint_version_is_rejected_without_downgrade()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            IndependentTrainingSessionState.Deserialize(
                "{\"Version\":2,\"Stage\":\"ConfigureSkills\","
                + "\"AgendaIndex\":3,\"SkillIndex\":5,\"CurrentSkillId\":12345,"
                + "\"LastConfirmedScreen\":\"career_final_confirmation\"}"));

        Assert.Contains("version 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supported version 1", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("ActionsCompleted", state.Serialize());
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
