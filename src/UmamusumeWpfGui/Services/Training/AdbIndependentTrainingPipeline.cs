using System.Globalization;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Executes only the Independent Training setup on the final confirmation
/// page. It has its own stage/checkpoint model and never enters the URA turn
/// engine.
/// </summary>
public sealed class AdbIndependentTrainingPipeline : IIndependentTrainingPipeline
{
    private const string FinalConfirmationScreenId = "career_final_confirmation";
    private const string StartIssuedScreenId = "independent_start_issued";
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly CareerEntryNavigator _entryNavigator;
    private readonly ICareerActionExecutor _actions;
    private readonly Func<int, IndependentCheckpointStore> _checkpointStoreFactory;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbIndependentTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        CareerJsonActionExecutor actions)
        : this(
            visualRuntime,
            umaDatabase,
            entryNavigator,
            actions,
            traineeId => new IndependentCheckpointStore(traineeId))
    {
    }

    internal AdbIndependentTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        ICareerActionExecutor actions,
        Func<int, IndependentCheckpointStore> checkpointStoreFactory)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _umaDatabase = umaDatabase ?? throw new ArgumentNullException(nameof(umaDatabase));
        _entryNavigator = entryNavigator ?? throw new ArgumentNullException(nameof(entryNavigator));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _checkpointStoreFactory = checkpointStoreFactory
            ?? throw new ArgumentNullException(nameof(checkpointStoreFactory));
    }

    public async Task<IndependentTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        IndependentTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
                return Failure("An Independent Training run is already in progress.", "busy");
            _runCancellation = linked;
        }

        try
        {
            return await RunCoreAsync(connection, settings, logSink, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logSink?.Add("Independent Training", "Independent Training was stopped.", LogEntryKind.Failure);
            return Failure("Independent Training was stopped.", "canceled");
        }
        finally
        {
            lock (_runLock)
            {
                if (ReferenceEquals(_runCancellation, linked))
                    _runCancellation = null;
            }
        }
    }

    public Task<IndependentTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }
        logSink?.Add("Independent Training", "Stop requested.");
        return Task.FromResult(new IndependentTrainingResult(true, "Stop requested.", 0, "stop"));
    }

    private async Task<IndependentTrainingResult> RunCoreAsync(
        LastVerifiedConnection connection,
        IndependentTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (!_umaDatabase.TryGetTrainee(settings.TraineeId, out var trainee)
            || trainee is null
            || !trainee.Available)
        {
            return Failure(
                $"Configured trainee ID {settings.TraineeId.ToString(CultureInfo.InvariantCulture)} "
                    + "was not found or is unavailable.",
                "unknown");
        }

        ValidateSettings(settings);
        var pack = await UraScenarioPackLoader.LoadAsync(
                settings.ManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        logSink?.Add(
            "Independent Training",
            $"Loaded {pack.Manifest.DisplayName} for {trainee.NameEn} ({trainee.TraineeId}).");

        var runtime = new IndependentTrainingRuntimeContext();
        var checkpointStore = _checkpointStoreFactory(settings.TraineeId);
        IndependentTrainingSessionState state;
        if (!settings.ContinueExistingCareer)
        {
            await checkpointStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            state = new IndependentTrainingSessionState();
            logSink?.Add(
                "Independent Training",
                "Restart selected; the independent checkpoint was reset.");
        }
        else
        {
            IndependentTrainingSessionState? newCheckpoint;
            try
            {
                newCheckpoint = await checkpointStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                logSink?.Add("Independent Training", exception.Message, LogEntryKind.Failure);
                return Failure(exception.Message, "checkpoint");
            }
            var migrated = newCheckpoint is null
                ? await checkpointStore.LoadLegacyAsync(cancellationToken).ConfigureAwait(false)
                : null;
            state = newCheckpoint ?? migrated ?? new IndependentTrainingSessionState();
            state.NormalizeForResume();
            if (migrated is not null)
            {
                logSink?.Add(
                    "Independent Training",
                    "No new Independent checkpoint found; migrated the old URA checkpoint best-effort.",
                    LogEntryKind.Info);
                await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }

        if (state.Stage == IndependentTrainingStage.Completed)
        {
            var completionProbe = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.PostStartHomeProbeSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (completionProbe is not null)
                return completionProbe;

            state.LastConfirmedScreen = "home";
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            logSink?.Add(
                "Independent Training",
                "Completed checkpoint verified by the Training Independently marker.",
                LogEntryKind.Info);
            return new IndependentTrainingResult(
                true,
                "Independent Training is already completed.",
                runtime.ActionsCompleted,
                state.LastConfirmedScreen);
        }

        if (IsEntryStage(state.Stage))
        {
            var entryState = new CareerEntryNavigationState
            {
                Step = ToEntryNavigationStep(state.Stage),
                LastScreenId = state.LastConfirmedScreen,
            };

            async Task SaveEntryProgressAsync(CareerEntryNavigationState progress)
            {
                state.Stage = progress.Step == CareerEntryNavigationStep.Support
                    && progress.LastScreenId.Equals(
                        "support_start_transition",
                        StringComparison.OrdinalIgnoreCase)
                    ? IndependentTrainingStage.OpenFinalConfirmation
                    : FromEntryNavigationStep(progress.Step);
                state.LastConfirmedScreen = progress.LastScreenId;
                runtime.AdoptEntryProgress(progress);
                await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            var entry = await _entryNavigator.NavigateAsync(
                    connection,
                    pack,
                    settings,
                    entryState,
                    logSink,
                    SaveEntryProgressAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            state.LastConfirmedScreen = entry.LastScreenId;
            runtime.AdoptEntryProgress(entryState);
            if (!entry.Succeeded)
            {
                state.Stage = FromEntryScreen(entry.LastScreenId, entryState.Step);
                await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return Failure(entry.Message, state.LastConfirmedScreen, runtime.ActionsCompleted);
            }

            state.Stage = IndependentTrainingStage.SelectIndependentMode;
            state.LastConfirmedScreen = FinalConfirmationScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (IsIndependentConfigurationStage(state.Stage))
        {
            var configurationFailure = await ConfigureIndependentAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    runtime,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (configurationFailure is not null)
                return configurationFailure;
        }

        if (IsStartStage(state.Stage))
        {
            var startFailure = await StartIndependentAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (startFailure is not null)
                return startFailure;
        }

        state.Stage = IndependentTrainingStage.Completed;
        state.LastConfirmedScreen = "home";
        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        logSink?.Add(
            "Independent Training",
            "Independent Training started and the Home screen was verified.",
            LogEntryKind.Success);
        return new IndependentTrainingResult(
            true,
            "Independent Training started and returned to Home.",
            runtime.ActionsCompleted,
            state.LastConfirmedScreen);
    }

    private async Task<IndependentTrainingResult?> ConfigureIndependentAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IndependentTrainingRuntimeContext runtime,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.Stage == IndependentTrainingStage.SelectIndependentMode)
        {
            var result = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    "independent.select_mode",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.Stage = IndependentTrainingStage.ExpandLineup;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ExpandLineup)
        {
            var result = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.LineupExpandSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.Stage = IndependentTrainingStage.ConfigureFocus;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ConfigureFocus)
        {
            var focusAction = settings.TrainingFocus.Trim().ToLowerInvariant() switch
            {
                "stamina" => "independent.focus.stamina",
                "sprint" => "independent.focus.sprint",
                _ => "independent.focus.balanced",
            };
            var result = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    focusAction,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.Stage = IndependentTrainingStage.ConfigureAgenda;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ConfigureAgenda)
        {
            var agendaError = await ConfigureAgendaAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    runtime,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (agendaError is not null)
                return agendaError;
            state.Stage = IndependentTrainingStage.ConfigureSkills;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ConfigureSkills)
        {
            var skillError = await ConfigureSkillsAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    runtime,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (skillError is not null)
                return skillError;
            state.Stage = IndependentTrainingStage.CollapseLineup;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.CollapseLineup)
        {
            var scroll = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.LineupScrollTopSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (scroll is not null)
                return scroll;
            var collapse = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.LineupCollapseSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (collapse is not null)
                return collapse;
            var verification = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.LineupClosedVerifySemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification is not null)
                return verification;
            state.Stage = IndependentTrainingStage.ConfigureStrategy;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ConfigureStrategy)
        {
            if (!IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                    settings.LineupStrategy,
                    out var strategyOptionAction,
                    out _))
            {
                return Failure(
                    $"Independent lineup strategy '{settings.LineupStrategy}' is invalid.",
                    state.LastConfirmedScreen,
                    runtime.ActionsCompleted);
            }

            foreach (var action in new[]
            {
                IndependentTrainingCatalog.StrategyChangeSemanticAction(),
                strategyOptionAction,
                IndependentTrainingCatalog.StrategySaveSemanticAction(),
                IndependentTrainingCatalog.StrategyReturnSemanticAction(),
            })
            {
                var result = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        action,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }
            state.Stage = IndependentTrainingStage.StartTraining;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<IndependentTrainingResult?> ConfigureAgendaAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IndependentTrainingRuntimeContext runtime,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var selections = settings.EffectiveAgendaSelections;
        if (selections.Count == 0)
            return null;

        var catalog = IndependentTrainingCatalog.Load();
        var openActions = state.AgendaIndex == 0
            ? new[]
            {
                "independent.agenda.open",
                "independent.agenda.reset",
                "independent.agenda.reset.confirm",
                "independent.agenda.reset.done",
            }
            : new[] { "independent.agenda.open" };
        foreach (var action in openActions)
        {
            var result = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    action,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        var startIndex = Math.Min(state.AgendaIndex, selections.Count);
        state.AgendaIndex = startIndex;
        for (var index = startIndex; index < selections.Count; index++)
        {
            var selection = selections[index];
            if (!catalog.TryGetAgendaPickerEntry(selection, out var pickerRace))
            {
                return Failure(
                    $"Independent agenda race '{selection.RaceName}' has no Global Race ID mapping.",
                    state.LastConfirmedScreen,
                    runtime.ActionsCompleted);
            }

            var yearAction = $"independent.agenda.year.{Slug(selection.Year)}";
            var year = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    yearAction,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (year is not null)
                return year;
            var slot = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.AgendaSlotSemanticAction(selection),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (slot is not null)
                return slot;

            var selected = false;
            if (catalog.TryGetAgendaPickerOcrTarget(pickerRace, out var target))
            {
                var ocr = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        IndependentTrainingCatalog.AgendaRaceSemanticAction(),
                        logSink,
                        cancellationToken,
                        new HachimiPipelineRunOptions
                        {
                            TargetTextOverrides = new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["independent_agenda_race_find"] = target,
                            },
                        })
                    .ConfigureAwait(false);
                if (ocr is null)
                {
                    var verify = await RunIndependentActionAsync(
                            connection,
                            pack,
                            state,
                            runtime,
                            IndependentTrainingCatalog.AgendaRaceVerifySemanticAction(),
                            logSink,
                            cancellationToken,
                            new HachimiPipelineRunOptions
                            {
                                TargetTextOverrides = new Dictionary<string, string>(
                                    StringComparer.OrdinalIgnoreCase)
                                {
                                    ["independent_agenda_race_verify"] = target,
                                },
                            })
                        .ConfigureAwait(false);
                    if (verify is not null)
                        return verify;
                    selected = true;
                }
                else if (!IndependentTrainingContracts.IsAgendaOcrRecognitionMiss(ocr.Message, target))
                {
                    return ocr;
                }
            }

            if (!selected)
            {
                var rewind = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        "independent.agenda.race.scroll.top",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (rewind is not null)
                    return rewind;
                var cardPath = IndependentTrainingCatalog.TryResolveRaceCardImagePath(
                    pickerRace,
                    pack.ExecutionDefinition.BaseDirectory);
                if (cardPath is null)
                {
                    return Failure(
                        $"Independent agenda race '{selection.RaceName}' has no Race ID card asset.",
                        state.LastConfirmedScreen,
                        runtime.ActionsCompleted);
                }
                foreach (var action in new[]
                {
                    IndependentTrainingCatalog.AgendaRaceCardSemanticAction(),
                    IndependentTrainingCatalog.AgendaRaceCardVerifySemanticAction(),
                })
                {
                    var card = await RunIndependentActionAsync(
                            connection,
                            pack,
                            state,
                            runtime,
                            action,
                            logSink,
                            cancellationToken,
                            new HachimiPipelineRunOptions
                            {
                                TemplateOverrides = new Dictionary<string, string>(
                                    StringComparer.OrdinalIgnoreCase)
                                {
                                    [action.EndsWith("verify", StringComparison.OrdinalIgnoreCase)
                                        ? "independent_agenda_race_card_verify"
                                        : "independent_agenda_race_card_find"] = cardPath,
                                },
                            })
                        .ConfigureAwait(false);
                    if (card is not null)
                        return card;
                }
            }

            var save = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    "independent.agenda.save",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (save is not null)
                return save;

            state.AgendaIndex = index + 1;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return await RunIndependentActionAsync(
                connection,
                pack,
                state,
                runtime,
                "independent.agenda.close",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IndependentTrainingResult?> ConfigureSkillsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IndependentTrainingRuntimeContext runtime,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var skillIds = settings.EffectiveSkillIds;
        if (skillIds.Count == 0)
        {
            state.SkillIndex = 0;
            state.CurrentSkillId = null;
            return null;
        }

        var catalog = IndependentTrainingCatalog.Load();
        var scroll = await RunIndependentActionAsync(
                connection,
                pack,
                state,
                runtime,
                IndependentTrainingCatalog.SkillSearchScrollSemanticAction(),
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (scroll is not null)
            return scroll;
        var reset = await RunIndependentActionAsync(
                connection,
                pack,
                state,
                runtime,
                "independent.skills.reset",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (reset is not null)
            return reset;

        var startIndex = Math.Min(state.SkillIndex, skillIds.Count);
        state.SkillIndex = startIndex;
        if (startIndex == skillIds.Count)
            state.CurrentSkillId = null;
        for (var index = startIndex; index < skillIds.Count; index++)
        {
            var skillId = skillIds[index];
            state.SkillIndex = index;
            state.CurrentSkillId = skillId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            var skill = catalog.Skills.FirstOrDefault(item => item.SkillId == skillId);
            if (skill is null || string.IsNullOrWhiteSpace(skill.EffectiveSearchText))
            {
                return Failure(
                    $"Independent skill {skillId.ToString(CultureInfo.InvariantCulture)} is not selectable.",
                    state.LastConfirmedScreen,
                    runtime.ActionsCompleted);
            }
            if (!IndependentTrainingCatalog.TryGetVerifiedSkillFallback(
                    skill,
                    out var page,
                    out var pickerRow))
            {
                return Failure(
                    $"Independent skill {skillId.ToString(CultureInfo.InvariantCulture)} has no verified search mapping.",
                    state.LastConfirmedScreen,
                    runtime.ActionsCompleted);
            }

            foreach (var action in new[]
            {
                "independent.skills.open",
                IndependentTrainingCatalog.SkillSearchResetSemanticAction(),
                IndependentTrainingCatalog.SkillSearchFocusSemanticAction(),
            })
            {
                var result = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        action,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }
            var input = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.SkillSearchInputSemanticAction(),
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        InputTextOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["independent_skills_search_input"] = skill.EffectiveSearchText,
                        },
                    })
                .ConfigureAwait(false);
            if (input is not null)
                return input;
            var submit = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.SkillSearchSubmitSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (submit is not null)
                return submit;
            for (var pageIndex = 0; pageIndex < page; pageIndex++)
            {
                var pageScroll = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        IndependentTrainingCatalog.SkillSearchScrollSemanticAction(),
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pageScroll is not null)
                    return pageScroll;
            }

            var checkbox = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.SkillSearchCheckboxSemanticAction(),
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TargetTextOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["independent_skills_search_checkbox_ocr"] = skill.OcrTargetText,
                        },
                    })
                .ConfigureAwait(false);
            if (checkbox is not null)
            {
                var fallback = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        IndependentTrainingCatalog.SkillSearchCheckboxFallbackSemanticAction(),
                        logSink,
                        cancellationToken,
                        new HachimiPipelineRunOptions
                        {
                            RoiOverrides = new Dictionary<string, int[]>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["independent_skills_search_checkbox"] =
                                    IndependentTrainingCatalog.GetSkillPickerCheckboxFallbackRoi(pickerRow),
                            },
                        })
                    .ConfigureAwait(false);
                if (fallback is not null)
                    return fallback;
            }

            var save = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    "independent.skills.save",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (save is not null)
                return save;

            state.SkillIndex = index + 1;
            state.CurrentSkillId = null;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<IndependentTrainingResult?> StartIndependentAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSessionState state,
        IndependentTrainingRuntimeContext runtime,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.Stage == IndependentTrainingStage.StartTraining)
        {
            var startIssued = state.LastConfirmedScreen.Equals(
                StartIssuedScreenId,
                StringComparison.OrdinalIgnoreCase);
            if (!startIssued)
            {
                var start = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        "independent.start",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (start is not null)
                    return start;
                state.LastConfirmedScreen = StartIssuedScreenId;
            }

            state.Stage = IndependentTrainingStage.HandlePostStartDialog;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.HandlePostStartDialog)
        {
            foreach (var action in new[]
            {
                IndependentTrainingCatalog.PostStartOkSemanticAction(),
                IndependentTrainingCatalog.PostStartMenuSemanticAction(),
                IndependentTrainingCatalog.PostStartToHomeSemanticAction(),
            })
            {
                var result = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        runtime,
                        action,
                        logSink,
                        cancellationToken,
                        allowVisualMiss: true)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
                state.LastConfirmedScreen = StartIssuedScreenId;
                await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            state.Stage = IndependentTrainingStage.ReturnHome;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.ReturnHome)
        {
            var home = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    runtime,
                    IndependentTrainingCatalog.PostStartHomeProbeSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (home is not null)
                return home;

            state.Stage = IndependentTrainingStage.Completed;
            state.LastConfirmedScreen = "home";
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<IndependentTrainingResult?> RunIndependentActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSessionState state,
        IndependentTrainingRuntimeContext runtime,
        string action,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null,
        bool allowVisualMiss = false)
    {
        var result = await _actions.RunAsync(
                connection,
                pack,
                "career_entry",
                action,
                logSink,
                cancellationToken,
                options,
                allowVisualMiss)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            runtime.RecordActionFailure();
            return Failure(result.Message, state.LastConfirmedScreen, runtime.ActionsCompleted);
        }
        state.LastConfirmedScreen = result.LastScreenId;
        runtime.RecordActionSuccess();
        return null;
    }

    private static bool IsEntryStage(IndependentTrainingStage stage) =>
        (int)stage <= (int)IndependentTrainingStage.OpenFinalConfirmation;

    private static bool IsIndependentConfigurationStage(IndependentTrainingStage stage) =>
        (int)stage >= (int)IndependentTrainingStage.SelectIndependentMode
        && (int)stage <= (int)IndependentTrainingStage.ConfigureStrategy;

    private static bool IsStartStage(IndependentTrainingStage stage) =>
        (int)stage >= (int)IndependentTrainingStage.StartTraining
        && (int)stage <= (int)IndependentTrainingStage.ReturnHome;

    private static CareerEntryNavigationStep ToEntryNavigationStep(
        IndependentTrainingStage stage) => stage switch
    {
        IndependentTrainingStage.HandleExistingCareer => CareerEntryNavigationStep.Continue,
        IndependentTrainingStage.SelectScenario => CareerEntryNavigationStep.Scenario,
        IndependentTrainingStage.SelectTrainee => CareerEntryNavigationStep.Trainee,
        IndependentTrainingStage.SelectLegacy => CareerEntryNavigationStep.Legacy,
        IndependentTrainingStage.SelectSupportDeck
            or IndependentTrainingStage.OpenFinalConfirmation => CareerEntryNavigationStep.Support,
        _ => CareerEntryNavigationStep.Home,
    };

    private static IndependentTrainingStage FromEntryNavigationStep(
        CareerEntryNavigationStep step) => step switch
        {
            CareerEntryNavigationStep.Continue => IndependentTrainingStage.HandleExistingCareer,
            CareerEntryNavigationStep.Scenario => IndependentTrainingStage.SelectScenario,
            CareerEntryNavigationStep.Trainee => IndependentTrainingStage.SelectTrainee,
            CareerEntryNavigationStep.Legacy => IndependentTrainingStage.SelectLegacy,
            CareerEntryNavigationStep.Support => IndependentTrainingStage.SelectSupportDeck,
            CareerEntryNavigationStep.FinalConfirmation => IndependentTrainingStage.OpenFinalConfirmation,
            _ => IndependentTrainingStage.EnterCareer,
        };

    private static IndependentTrainingStage FromEntryScreen(
        string screenId,
        CareerEntryNavigationStep fallbackStep) =>
        screenId.Trim().ToLowerInvariant() switch
        {
            "home" => IndependentTrainingStage.EnterCareer,
            "career_continue" => IndependentTrainingStage.HandleExistingCareer,
            "scenario_select" => IndependentTrainingStage.SelectScenario,
            "trainee_select" => IndependentTrainingStage.SelectTrainee,
            "legacy_select" => IndependentTrainingStage.SelectLegacy,
            "support_select" or "support_autofill_confirmation" or "support_ready"
                or "support_start_transition" => IndependentTrainingStage.SelectSupportDeck,
            FinalConfirmationScreenId => IndependentTrainingStage.OpenFinalConfirmation,
            _ => FromEntryNavigationStep(fallbackStep),
        };

    private static void ValidateSettings(IndependentTrainingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ManifestPath))
            throw new InvalidOperationException("Independent Training manifest path is empty.");
        if (settings.TraineeId <= 0)
            throw new InvalidOperationException("Independent Training trainee ID is invalid.");
        if (settings.TrainingFocus.Trim().ToLowerInvariant() is not ("balanced" or "stamina" or "sprint"))
        {
            throw new InvalidOperationException(
                $"Independent training focus '{settings.TrainingFocus}' is invalid.");
        }
        var supportDeckMode = settings.SupportDeckMode.Trim().ToLowerInvariant();
        if (supportDeckMode is not ("auto" or "selected" or "highest-star"))
        {
            throw new InvalidOperationException(
                $"Independent support deck mode '{settings.SupportDeckMode}' is invalid.");
        }
        if (supportDeckMode == "selected"
            && (settings.SupportCardIds.Count is not (5 or 6)
                || (settings.FriendSupportCardId is > 0 && settings.SupportCardIds.Count != 5)))
        {
            throw new InvalidOperationException(
                "Independent selected support deck requires 5 cards, or 5 cards plus a guest card.");
        }
        if (supportDeckMode == "highest-star"
            && string.Equals(settings.SupportDeckPreset, "custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Independent highest-star support selection requires a support deck preset.");
        }
        if (!IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                settings.LineupStrategy,
                out _))
        {
            throw new InvalidOperationException(
                $"Independent lineup strategy '{settings.LineupStrategy}' is invalid.");
        }
    }

    private static string Slug(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "first year" => "first_year",
            "second year" => "second_year",
            "third year" => "third_year",
            _ => value.Trim().ToLowerInvariant().Replace(' ', '_'),
        };

    private static IndependentTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);
}
