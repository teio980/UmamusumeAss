using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingBehaviorTests
{
    private const int TraineeId = 100601;

    private static readonly LastVerifiedConnection Connection = new(
        "adb", "emulator-5554", "android", "test",
        900, 1600, 900, 1600, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Entry_navigation_records_the_existing_main_stage_order()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var progress = new List<CareerEntryNavigationStep>();
        var state = new CareerEntryNavigationState();

        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: false),
            state,
            null,
            current =>
            {
                progress.Add(current.Step);
                return Task.CompletedTask;
            });

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal(
            [
                CareerEntryNavigationStep.Continue,
                CareerEntryNavigationStep.Scenario,
                CareerEntryNavigationStep.Trainee,
                CareerEntryNavigationStep.Legacy,
                CareerEntryNavigationStep.Support,
            ],
            progress.Distinct().ToArray());
        Assert.Equal(CareerEntryNavigationStep.FinalConfirmation, state.Step);
        Assert.Equal(
            [
                "task:home",
                "career_continue.delete",
                "task:home",
                "scenario_select.next",
                "trainee_select.pick",
                "legacy_select.choose",
                "support_select.auto_fill",
            ],
            harness.Actions.Calls);
    }

    [Fact]
    public async Task Delete_existing_career_reopens_career_when_game_returns_home()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.ReturnsHomeAfterDelete = true;

        var state = new CareerEntryNavigationState();
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: false),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal(
            [
                "task:home",
                "career_continue.delete",
                "task:home",
                "scenario_select.next",
                "trainee_select.pick",
                "legacy_select.choose",
                "support_select.auto_fill",
            ],
            harness.Actions.Calls);
    }

    [Fact]
    public async Task Normal_resume_enters_existing_career_without_scenario_navigation()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.ReturnsCareerAfterResume = true;

        var state = new CareerEntryNavigationState
        {
            ResumeDirectlyToCareer = true,
        };
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: true),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal("career_main", result.LastScreenId);
        Assert.Equal(CareerEntryNavigationStep.Career, state.Step);
        Assert.Equal(["task:home", "career_continue.resume"], harness.Actions.Calls);
    }

    [Fact]
    public async Task Resume_from_existing_career_prompt_does_not_reopen_home()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.ReturnsCareerAfterResume = true;
        harness.Actions.SetScreen("career_continue");

        var state = new CareerEntryNavigationState
        {
            Step = CareerEntryNavigationStep.Continue,
            LastScreenId = "career_continue",
            ResumeDirectlyToCareer = true,
        };
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: true),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal("career_main", result.LastScreenId);
        Assert.Equal(["career_continue.resume"], harness.Actions.Calls);
    }

    [Fact]
    public async Task Career_main_resume_step_continues_without_reopening_home()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.SetScreen("career_main");

        var state = new CareerEntryNavigationState
        {
            LastScreenId = "career_main",
            ResumeDirectlyToCareer = true,
        };
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: true),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal("career_main", result.LastScreenId);
        Assert.Equal(CareerEntryNavigationStep.Career, state.Step);
        Assert.Empty(harness.Actions.Calls);
    }

    [Fact]
    public async Task Support_start_does_not_click_again_while_final_confirmation_is_loading()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.HoldSupportReadyAfterStartOnce = true;
        harness.Actions.SetScreen("support_ready");

        var state = new CareerEntryNavigationState
        {
            Step = CareerEntryNavigationStep.Support,
            LastScreenId = "support_ready",
        };
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: false),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal(["support_ready.start"], harness.Actions.Calls);
        Assert.Equal(CareerEntryNavigationStep.FinalConfirmation, state.Step);
    }

    [Fact]
    public async Task Entry_action_failure_keeps_stage_and_does_not_run_the_next_stage()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.FailWhen = call => call == "career_continue.delete";

        var state = new CareerEntryNavigationState();
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(root, continueExistingCareer: false),
            state,
            null);

        Assert.False(result.Succeeded);
        Assert.Equal(CareerEntryNavigationStep.Continue, state.Step);
        Assert.Equal("career_continue", result.LastScreenId);
        Assert.Equal(["task:home", "career_continue.delete"], harness.Actions.Calls);
        Assert.DoesNotContain(harness.Actions.Calls, call => call.Contains("scenario_select", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("auto", "custom", "support_select.auto_fill")]
    [InlineData("selected", "custom", "support_select.start")]
    [InlineData("highest-star", "speed3-stamina3", "support_select.start")]
    public async Task Entry_navigation_support_deck_modes_reach_final_confirmation(
        string supportDeckMode,
        string supportDeckPreset,
        string expectedFinalAction)
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var supportCardIds = supportDeckMode == "selected"
            ? harness.Database.SupportCards
                .Where(card => card.Available)
                .Take(5)
                .Select(card => card.SupportCardId)
                .ToArray()
            : [];

        var state = new CareerEntryNavigationState();
        var result = await harness.Navigator.NavigateAsync(
            Connection,
            harness.Pack,
            CreateSettings(
                root,
                continueExistingCareer: false,
                supportDeckMode: supportDeckMode,
                supportDeckPreset: supportDeckPreset,
                supportCardIds: supportCardIds),
            state,
            null);

        Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
        Assert.Equal(CareerEntryNavigationStep.FinalConfirmation, state.Step);
        Assert.Equal("career_final_confirmation", result.LastScreenId);
        Assert.Contains(expectedFinalAction, harness.Actions.Calls);
        Assert.Equal(expectedFinalAction, harness.Actions.Calls[^1]);
    }

    [Fact]
    public async Task Pipeline_records_the_complete_independent_stage_order()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var catalog = IndependentTrainingCatalog.Load(root);
        var agenda = SelectAgenda(catalog, 2);
        var skills = SelectSkills(catalog, 2);
        var settings = CreateSettings(root, false, agenda, skills);

        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var result = await harness.Pipeline.RunAsync(Connection, settings, null);

            Assert.True(result.Succeeded, result.Message + " calls=" + string.Join(",", harness.Actions.Calls));
            Assert.Equal("home", result.LastScreenId);
            var checkpoint = await harness.Store.LoadAsync();
            Assert.NotNull(checkpoint);
            Assert.Equal(IndependentTrainingStage.Completed, checkpoint!.Stage);
            Assert.True(checkpoint.CompletionVerified);
            Assert.Equal(2, checkpoint.AgendaIndex);
            Assert.Equal(2, checkpoint.SkillIndex);
            Assert.Null(checkpoint.CurrentSkillId);

            var expected = new List<string>
            {
                "task:home",
                "career_continue.delete",
                "task:home",
                "scenario_select.next",
                "trainee_select.pick",
                "legacy_select.choose",
                "support_select.auto_fill",
                "independent.select_mode",
                "independent.lineup.expand",
                "independent.focus.balanced",
                "independent.agenda.open",
                "independent.agenda.reset",
                "independent.agenda.reset.confirm",
                "independent.agenda.reset.done",
            };
            foreach (var selection in agenda)
            {
                expected.Add($"independent.agenda.year.{Slug(selection.Year)}");
                expected.Add(IndependentTrainingCatalog.AgendaSlotSemanticAction(selection));
                expected.Add(IndependentTrainingCatalog.AgendaRaceSemanticAction());
                expected.Add(IndependentTrainingCatalog.AgendaRaceVerifySemanticAction());
                expected.Add("independent.agenda.save");
            }
            expected.Add("independent.agenda.close");
            expected.Add(IndependentTrainingCatalog.SkillSearchScrollSemanticAction());
            expected.Add("independent.skills.reset");
            foreach (var _ in skills)
            {
                expected.Add("independent.skills.open");
                expected.Add(IndependentTrainingCatalog.SkillSearchResetSemanticAction());
                expected.Add(IndependentTrainingCatalog.SkillSearchFocusSemanticAction());
                expected.Add(IndependentTrainingCatalog.SkillSearchInputSemanticAction());
                expected.Add(IndependentTrainingCatalog.SkillSearchSubmitSemanticAction());
                expected.Add(IndependentTrainingCatalog.SkillSearchCheckboxSemanticAction());
                expected.Add("independent.skills.save");
            }
            expected.Add(IndependentTrainingCatalog.LineupScrollTopSemanticAction());
            expected.Add(IndependentTrainingCatalog.LineupCollapseSemanticAction());
            expected.Add(IndependentTrainingCatalog.LineupClosedVerifySemanticAction());
            expected.Add(IndependentTrainingCatalog.StrategyChangeSemanticAction());
            expected.Add("independent.strategy.option.pace");
            expected.Add(IndependentTrainingCatalog.StrategySaveSemanticAction());
            expected.Add(IndependentTrainingCatalog.StrategyReturnSemanticAction());
            expected.Add("independent.start");
            expected.Add(IndependentTrainingCatalog.PostStartOkSemanticAction());
            expected.Add(IndependentTrainingCatalog.PostStartMenuSemanticAction());
            expected.Add(IndependentTrainingCatalog.PostStartToHomeSemanticAction());
            expected.Add(IndependentTrainingCatalog.PostStartHomeProbeSemanticAction());

            Assert.Equal(expected, harness.Actions.Calls);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Completed_checkpoint_is_restarted_automatically()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        await File.WriteAllTextAsync(
            harness.Store.CheckpointPath,
            "{\"Version\":2,\"Stage\":\"Completed\","
                + "\"LastConfirmedScreen\":\"home\",\"CompletionVerified\":true}");

        var result = await harness.Pipeline.RunAsync(
            Connection,
            CreateSettings(
                root,
                continueExistingCareer: true),
            null);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("career_continue.resume", harness.Actions.Calls);
        Assert.DoesNotContain("career_continue.delete", harness.Actions.Calls);
        Assert.Contains("independent.start", harness.Actions.Calls);
        Assert.True((await harness.Store.LoadAsync())!.CompletionVerified);
    }

    [Fact]
    public async Task Failed_stage_is_saved_and_resume_does_not_repeat_completed_stages()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        harness.Actions.FailWhen = call => call == "independent.focus.balanced";

        var first = await harness.Pipeline.RunAsync(
            Connection,
            CreateSettings(root, false),
            null);

        Assert.False(first.Succeeded);
        Assert.Equal(IndependentTrainingStage.ConfigureFocus, (await harness.Store.LoadAsync())!.Stage);
        Assert.DoesNotContain("independent.agenda.open", harness.Actions.Calls);

        harness.Actions.Calls.Clear();
        harness.Actions.FailWhen = null;
        var resumed = await harness.Pipeline.RunAsync(
            Connection,
            CreateSettings(root, true),
            null);

        Assert.True(resumed.Succeeded, resumed.Message);
        Assert.Equal(harness.Actions.Calls.Count, resumed.ActionsCompleted);
        Assert.Equal("independent.focus.balanced", harness.Actions.Calls[0]);
        Assert.DoesNotContain("independent.select_mode", harness.Actions.Calls);
        Assert.DoesNotContain(IndependentTrainingCatalog.LineupExpandSemanticAction(), harness.Actions.Calls);
    }

    [Fact]
    public async Task Agenda_resume_starts_at_the_saved_index_and_advances_after_completion()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var catalog = IndependentTrainingCatalog.Load(root);
        var selections = SelectAgenda(catalog, 2);
        await harness.Store.SaveAsync(new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureAgenda,
            AgendaIndex = 1,
            LastConfirmedScreen = "career_final_confirmation",
        });

        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var result = await harness.Pipeline.RunAsync(
                Connection,
                CreateSettings(root, true, selections),
                null);

            Assert.True(result.Succeeded, result.Message);
            Assert.DoesNotContain(
                IndependentTrainingCatalog.AgendaSlotSemanticAction(selections[0]),
                harness.Actions.Calls);
            Assert.Contains(
                IndependentTrainingCatalog.AgendaSlotSemanticAction(selections[1]),
                harness.Actions.Calls);
            var checkpoint = await harness.Store.LoadAsync();
            Assert.Equal(2, checkpoint!.AgendaIndex);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Skill_resume_starts_at_the_saved_index_and_does_not_repeat_completed_skill()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var catalog = IndependentTrainingCatalog.Load(root);
        var skills = SelectSkills(catalog, 2);
        await harness.Store.SaveAsync(new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureSkills,
            SkillIndex = 1,
            CurrentSkillId = skills[1],
            LastConfirmedScreen = "career_final_confirmation",
        });

        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var result = await harness.Pipeline.RunAsync(
                Connection,
                CreateSettings(root, true, skillIds: skills),
                null);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal([catalog.Skills.First(skill => skill.SkillId == skills[1]).EffectiveSearchText], harness.Actions.SearchInputs);
            var checkpoint = await harness.Store.LoadAsync();
            Assert.Equal(2, checkpoint!.SkillIndex);
            Assert.Null(checkpoint.CurrentSkillId);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Stop_preserves_stage_and_cursor_and_next_run_starts_runtime_counts_at_zero()
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        var catalog = IndependentTrainingCatalog.Load(root);
        var selection = SelectAgenda(catalog, 1);
        await harness.Store.SaveAsync(new IndependentTrainingSessionState
        {
            Stage = IndependentTrainingStage.ConfigureAgenda,
            AgendaIndex = 0,
            LastConfirmedScreen = "career_final_confirmation",
        });
        harness.Actions.BlockAction = "independent.agenda.reset";

        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var runTask = harness.Pipeline.RunAsync(
                Connection,
                CreateSettings(root, true, selection),
                null);
            await harness.Actions.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = await harness.Pipeline.StopAsync(Connection);
            var stopped = await runTask;

            Assert.True(stop.Succeeded);
            Assert.False(stopped.Succeeded);
            Assert.Equal("canceled", stopped.LastScreenId);
            var checkpoint = await harness.Store.LoadAsync();
            Assert.Equal(IndependentTrainingStage.ConfigureAgenda, checkpoint!.Stage);
            Assert.Equal(0, checkpoint.AgendaIndex);

            harness.Actions.BlockAction = null;
            harness.Actions.Calls.Clear();
            var resumed = await harness.Pipeline.RunAsync(
                Connection,
                CreateSettings(root, true, selection),
                null);

            Assert.True(resumed.Succeeded, resumed.Message);
            Assert.Equal(harness.Actions.Calls.Count, resumed.ActionsCompleted);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Theory]
    [InlineData(IndependentTrainingStage.SelectSupportDeck, "support_select", "support_select.auto_fill")]
    [InlineData(IndependentTrainingStage.OpenFinalConfirmation, "career_final_confirmation", "independent.select_mode")]
    public async Task Final_confirmation_checkpoint_resumes_without_replaying_entry(
        IndependentTrainingStage stage,
        string screen,
        string expectedFirstAction)
    {
        var root = FindSolutionRoot();
        await using var scope = new TestScope();
        var harness = await CreateHarnessAsync(root, scope.CheckpointRoot);
        await harness.Store.SaveAsync(new IndependentTrainingSessionState
        {
            Stage = stage,
            LastConfirmedScreen = screen,
        });
        harness.Actions.SetScreen(screen);

        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var result = await harness.Pipeline.RunAsync(
                Connection,
                CreateSettings(root, continueExistingCareer: true),
                null);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(expectedFirstAction, harness.Actions.Calls[0]);
            Assert.DoesNotContain("task:home", harness.Actions.Calls);
            Assert.DoesNotContain("career_continue.resume", harness.Actions.Calls);
            Assert.DoesNotContain("scenario_select.next", harness.Actions.Calls);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static async Task<BehaviorHarness> CreateHarnessAsync(string root, string checkpointRoot)
    {
        var visual = new RecordingVisualRuntime();
        var actions = new RecordingActionExecutor(visual);
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        var settingsPath = Path.Combine(checkpointRoot, "settings.json");
        var runner = new HachimiJsonPipelineRunner(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(settingsPath));
        var navigator = new CareerEntryNavigator(
            visual,
            database,
            new UraTraineeSelector(visual, database),
            new UraLegacySelector(visual, runner),
            actions);
        var pipeline = new AdbIndependentTrainingPipeline(
            visual,
            database,
            navigator,
            actions,
            traineeId => new IndependentCheckpointStore(traineeId, checkpointRoot));
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));
        return new BehaviorHarness(
            pack,
            actions,
            navigator,
            pipeline,
            new IndependentCheckpointStore(TraineeId, checkpointRoot),
            database);
    }

    private static IndependentTrainingSettings CreateSettings(
        string root,
        bool continueExistingCareer,
        IReadOnlyList<IndependentTrainingAgendaSelection>? agenda = null,
        IReadOnlyList<int>? skillIds = null,
        string supportDeckMode = "auto",
        string supportDeckPreset = "custom",
        IReadOnlyList<int>? supportCardIds = null,
        int? friendSupportCardId = null) =>
        new(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            TraineeId,
            continueExistingCareer,
            supportCardIds ?? [],
            supportDeckMode,
            supportDeckPreset,
            friendSupportCardId,
            "auto",
            false,
            false,
            [],
            [],
            "balanced",
            "pace",
            agenda,
            skillIds);

    private static IndependentTrainingAgendaSelection[] SelectAgenda(
        IndependentTrainingCatalog catalog,
        int count) =>
        catalog.Races
            .Where(race => race.IsGameAvailable && race.RaceId > 0)
            .Where(race => catalog.TryGetAgendaPickerOcrTarget(race, out _))
            .Take(count)
            .Select(race => new IndependentTrainingAgendaSelection(
                race.Year, race.Turn, race.RaceName))
            .ToArray();

    private static int[] SelectSkills(IndependentTrainingCatalog catalog, int count) =>
        catalog.Skills
            .Where(skill => skill.IsGameSearchMapped
                && skill.SearchResultPage == 0
                && !string.IsNullOrWhiteSpace(skill.EffectiveSearchText))
            .Take(count)
            .Select(skill => skill.SkillId)
            .ToArray();

    private static string Slug(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record BehaviorHarness(
        UraScenarioPack Pack,
        RecordingActionExecutor Actions,
        CareerEntryNavigator Navigator,
        AdbIndependentTrainingPipeline Pipeline,
        IndependentCheckpointStore Store,
        UmaDatabaseService Database);

    private sealed class TestScope : IAsyncDisposable
    {
        public TestScope()
        {
            CheckpointRoot = Path.Combine(
                Path.GetTempPath(),
                $"independent-behavior-{Guid.NewGuid():N}");
            Directory.CreateDirectory(CheckpointRoot);
        }

        public string CheckpointRoot { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(CheckpointRoot))
                Directory.Delete(CheckpointRoot, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActionExecutor : ICareerActionExecutor
    {
        private readonly RecordingVisualRuntime _visual;

        public RecordingActionExecutor(RecordingVisualRuntime visual) => _visual = visual;

        public List<string> Calls { get; } = [];
        public List<string> SearchInputs { get; } = [];
        public Func<string, bool>? FailWhen { get; set; }
        public string? BlockAction { get; set; }
        public bool ReturnsHomeAfterDelete { get; set; }
        public bool ReturnsCareerAfterResume { get; set; }
        public bool HoldSupportReadyAfterStartOnce { get; set; }
        private bool _careerDataDeleted;
        public TaskCompletionSource<bool> BlockEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetScreen(string screen) => _visual.SetScreen(screen);

        public async Task<CareerActionExecutionResult> RunAsync(
            LastVerifiedConnection connection,
            UraScenarioPack pack,
            string screenId,
            string actionId,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken,
            HachimiPipelineRunOptions? options = null,
            bool allowVisualMiss = false)
        {
            var call = screenId.Equals("career_entry", StringComparison.OrdinalIgnoreCase)
                ? actionId
                : $"{screenId}.{actionId}";
            Calls.Add(call);
            if (options?.InputTextOverrides?.TryGetValue(
                    "independent_skills_search_input", out var input) == true)
            {
                SearchInputs.Add(input);
            }

            if (call.Equals(BlockAction, StringComparison.OrdinalIgnoreCase))
            {
                BlockEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailWhen?.Invoke(call) == true)
                return new(false, $"fake failure: {call}", screenId);

            AdvanceEntryScreen(screenId, actionId);
            return CareerActionExecutionResult.Success(screenId);
        }

        public Task<CareerActionExecutionResult> RunTaskAsync(
            LastVerifiedConnection connection,
            UraScenarioPack pack,
            string taskName,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add($"task:{taskName}");
            // The first Home task opens the existing-career prompt.  After
            // Delete Data, the production navigator explicitly reruns this
            // task and expects it to open Scenario Select directly.
            _visual.SetScreen(
                _careerDataDeleted
                    ? "scenario_select"
                    : "career_continue");
            return Task.FromResult(CareerActionExecutionResult.Success(taskName));
        }

        private void AdvanceEntryScreen(string screenId, string actionId)
        {
            var next = (screenId, actionId) switch
            {
                ("career_continue", "delete") or ("career_continue", "resume") =>
                    AdvanceAfterCareerContinue(actionId),
                ("scenario_select", "next") or ("scenario_select", "next_card") => "trainee_select",
                ("trainee_select", "pick") => "legacy_select",
                ("legacy_select", "choose") => "support_select",
                ("support_select", "auto_fill") or ("support_select", "start") => "career_final_confirmation",
                ("support_ready", "start") => AdvanceAfterSupportStart(),
                _ => null,
            };
            if (next is not null)
                _visual.SetScreen(next);
        }

        private string AdvanceAfterSupportStart()
        {
            if (!HoldSupportReadyAfterStartOnce)
                return "career_final_confirmation";

            HoldSupportReadyAfterStartOnce = false;
            _visual.SetScreen("support_ready");
            _visual.TransitionTo("career_final_confirmation", afterCaptures: 2);
            return "support_ready";
        }

        private string AdvanceAfterCareerContinue(string actionId)
        {
            if (actionId.Equals("delete", StringComparison.OrdinalIgnoreCase))
                _careerDataDeleted = true;
            if (actionId.Equals("resume", StringComparison.OrdinalIgnoreCase)
                && ReturnsCareerAfterResume)
                return "career_main";
            return ReturnsHomeAfterDelete && _careerDataDeleted
                ? "home"
                : "scenario_select";
        }
    }

    private sealed class RecordingVisualRuntime : IVisualPipelineRuntime
    {
        private static readonly Dictionary<string, (int X, int Y)> Markers =
            new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase)
            {
                ["home"] = (1, 1),
                ["career_continue"] = (50, 52),
                ["scenario_select"] = (25, 29),
                ["scenario_card"] = (58, 76),
                ["trainee_select"] = (66, 66),
                ["legacy_select"] = (17, 28),
                ["support_select"] = (68, 145),
                ["support_ready"] = (50, 165),
                ["career_final_confirmation"] = (50, 10),
                ["career_main"] = (5, 0),
            };

        private string _screen = "home";
        private string? _screenAfterCapture;
        private int _capturesUntilScreenChange;

        public void SetScreen(string screen) => _screen = screen;

        public void TransitionTo(string screen, int afterCaptures)
        {
            _screenAfterCapture = screen;
            _capturesUntilScreenChange = afterCaptures;
        }

        public Task<GrayImage?> CaptureGrayAsync(
            LastVerifiedConnection connection,
            CancellationToken cancellationToken = default)
        {
            if (_capturesUntilScreenChange > 0)
            {
                _capturesUntilScreenChange--;
                if (_capturesUntilScreenChange == 0 && _screenAfterCapture is not null)
                {
                    _screen = _screenAfterCapture;
                    _screenAfterCapture = null;
                }
            }
            var pixels = new byte[100 * 200];
            if (_screen.Equals("home", StringComparison.OrdinalIgnoreCase))
                SetHomePattern(pixels, Markers[_screen]);
            else
                SetMarker(pixels, Markers[_screen]);
            if (_screen.Equals("scenario_select", StringComparison.OrdinalIgnoreCase))
                SetMarker(pixels, Markers["scenario_card"]);
            return Task.FromResult<GrayImage?>(new GrayImage(100, 200, pixels));
        }

        public Task<GrayImage?> LoadTemplateAsync(
            string? templatePath,
            string baseDirectory,
            CancellationToken cancellationToken = default)
        {
            var key = GetTemplateKey(templatePath);
            var pixels = key.Equals("home", StringComparison.OrdinalIgnoreCase)
                ? HomePattern
                : new byte[] { 255 };
            return Task.FromResult<GrayImage?>(Markers.ContainsKey(key)
                ? new GrayImage(
                    key.Equals("home", StringComparison.OrdinalIgnoreCase) ? 3 : 1,
                    key.Equals("home", StringComparison.OrdinalIgnoreCase) ? 3 : 1,
                    pixels)
                : null);
        }

        public Task<TemplateMatchResult?> WaitForMatchAsync(
            LastVerifiedConnection connection, string? templatePath, int[]? roi,
            double threshold, int referenceWidth, int referenceHeight,
            int timeoutMilliseconds, int pollIntervalMilliseconds, string taskName,
            string baseDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<TemplateMatchResult?>(null);

        public Task<TemplateMatchResult?> WaitForMatchScaledAsync(
            LastVerifiedConnection connection, string? templatePath, int[]? roi,
            double threshold, int referenceWidth, int referenceHeight,
            int timeoutMilliseconds, int pollIntervalMilliseconds, string taskName,
            string baseDirectory, IReadOnlyList<double> scaleCandidates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TemplateMatchResult?>(null);

        public Task<TemplateMatchResult?> WaitForMatchInRoisAsync(
            LastVerifiedConnection connection, string? templatePath, double threshold,
            int referenceWidth, int referenceHeight, int timeoutMilliseconds,
            int pollIntervalMilliseconds, string taskName, string baseDirectory,
            IReadOnlyList<int[]> searchRois, double minimumScoreGap,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TemplateMatchResult?>(null);

        public Task<ScreenTextRecognitionResult?> DetectTextAsync(
            LastVerifiedConnection connection, int[]? roi, int referenceWidth,
            int referenceHeight, string? language, string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextRecognitionResult?>(null);

        public Task<ScreenTextRecognitionResult?> DetectTextAsync(
            GrayImage frame, int[]? roi, int referenceWidth,
            int referenceHeight, string? language, string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextRecognitionResult?>(null);

        public Task<ScreenTextQueryResult?> FindTextAsync(
            LastVerifiedConnection connection, string targetText, int[]? roi,
            double fuzzyThreshold, bool unique, int referenceWidth,
            int referenceHeight, string? language, string taskName,
            string? matchMode = null, int groupRowHeight = 0, int rowGap = 0,
            bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextQueryResult?>(null);

        public Task<ScreenTextQueryResult?> WaitForTextAsync(
            LastVerifiedConnection connection, string targetText, int[]? roi,
            double fuzzyThreshold, bool unique, int referenceWidth,
            int referenceHeight, int timeoutMilliseconds, int pollIntervalMilliseconds,
            string? language, string taskName, string? matchMode = null,
            int groupRowHeight = 0, int rowGap = 0, bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextQueryResult?>(null);

        public Task TapTextAsync(
            LastVerifiedConnection connection, ScreenTextCandidate match,
            int[]? clickOffset, int[]? rowExpansion, string taskName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TapMatchAsync(
            LastVerifiedConnection connection, TemplateMatchResult match,
            string taskName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TapAsync(
            LastVerifiedConnection connection, int x, int y, int referenceWidth,
            int referenceHeight, string taskName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<HsvColorProbeResult?> ProbeHsvAsync(
            LastVerifiedConnection connection, int centerXReference,
            int centerYReference, int[]? offsetReference, int radiusReference,
            int referenceWidth, int referenceHeight, double hueMin, double hueMax,
            double saturationMin, double saturationMax, double valueMin,
            double valueMax, double minimumMatchRatio, int timeoutMilliseconds,
            int pollIntervalMilliseconds, string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<HsvColorProbeResult?>(null);

        public Task SwipeAsync(
            LastVerifiedConnection connection, int[] coordinates, int referenceWidth,
            int referenceHeight, string taskName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveScreenshotAsync(
            LastVerifiedConnection connection, string definitionPath, string name,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DelayAsync(
            int milliseconds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static void SetMarker(
            byte[] pixels,
            (int X, int Y) marker,
            byte value = 255) =>
            pixels[marker.Y * 100 + marker.X] = value;

        private static readonly byte[] HomePattern =
        [
            254, 0, 17,
            41, 0, 83,
            127, 191, 253,
        ];

        private static void SetHomePattern(
            byte[] pixels,
            (int X, int Y) marker)
        {
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                    pixels[(marker.Y + y) * 100 + marker.X + x] = HomePattern[y * 3 + x];
            }
        }

        private static string GetTemplateKey(string? path)
        {
            var value = path ?? string.Empty;
            if (value.Contains("ura_returned_home", StringComparison.OrdinalIgnoreCase))
                return "home";
            if (value.Contains("career_final_confirmation", StringComparison.OrdinalIgnoreCase))
                return "career_final_confirmation";
            if (value.Contains("scenario_select_ura", StringComparison.OrdinalIgnoreCase))
                return "scenario_card";
            if (value.Contains("scenario_select", StringComparison.OrdinalIgnoreCase))
                return "scenario_select";
            if (value.Contains("career_continue", StringComparison.OrdinalIgnoreCase))
                return "career_continue";
            if (value.Contains("trainee_select", StringComparison.OrdinalIgnoreCase))
                return "trainee_select";
            if (value.Contains("legacy_select", StringComparison.OrdinalIgnoreCase))
                return "legacy_select";
            if (value.Contains("support_ready", StringComparison.OrdinalIgnoreCase))
                return "support_ready";
            if (value.Contains("support_select", StringComparison.OrdinalIgnoreCase))
                return "support_select";
            if (value.Contains("career_main", StringComparison.OrdinalIgnoreCase))
                return "career_main";
            return string.Empty;
        }
    }

    private sealed class NoOpAdbRunner : IAdbRunner
    {
        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(
            string adbPath) =>
            (string.Empty, string.Empty, 0, false, null);
    }
}
