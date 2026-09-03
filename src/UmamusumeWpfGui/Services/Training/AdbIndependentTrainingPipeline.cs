using System.Globalization;
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
    private readonly CareerJsonActionExecutor _actions;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbIndependentTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        CareerJsonActionExecutor actions)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _umaDatabase = umaDatabase ?? throw new ArgumentNullException(nameof(umaDatabase));
        _entryNavigator = entryNavigator ?? throw new ArgumentNullException(nameof(entryNavigator));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
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

        var checkpointStore = new IndependentCheckpointStore(settings.TraineeId);
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
            var newCheckpoint = await checkpointStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
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
            return new IndependentTrainingResult(
                true,
                "Independent Training is already completed.",
                state.ActionsCompleted,
                state.LastScreenId);
        }

        if (state.Stage == IndependentTrainingStage.EntryConfiguration)
        {
            var entryState = new CareerEntryNavigationState
            {
                LastScreenId = state.LastScreenId,
                ActionsCompleted = state.ActionsCompleted,
            };
            var entry = await _entryNavigator.NavigateAsync(
                    connection,
                    pack,
                    settings,
                    entryState,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            state.LastScreenId = entry.LastScreenId;
            state.ActionsCompleted = entry.ActionsCompleted;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (!entry.Succeeded)
                return Failure(entry.Message, state.LastScreenId, state.ActionsCompleted);

            state.Stage = IndependentTrainingStage.IndependentConfiguration;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Mode;
            state.LineupCollapseVerifiedThisRun = false;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.Stage == IndependentTrainingStage.IndependentConfiguration)
        {
            var configurationFailure = await ConfigureIndependentAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (configurationFailure is not null)
                return configurationFailure;
        }

        if (state.Stage == IndependentTrainingStage.Start)
        {
            var startFailure = await StartIndependentAsync(
                    connection,
                    pack,
                    state,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (startFailure is not null)
                return startFailure;
        }

        state.Stage = IndependentTrainingStage.Completed;
        state.LastScreenId = "home";
        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        logSink?.Add(
            "Independent Training",
            "Independent Training started and the Home screen was verified.",
            LogEntryKind.Success);
        return new IndependentTrainingResult(
            true,
            "Independent Training started and returned to Home.",
            state.ActionsCompleted,
            state.LastScreenId);
    }

    private async Task<IndependentTrainingResult?> ConfigureIndependentAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.Mode)
        {
            var result = await RunIndependentActionAsync(
                    connection, pack, state, "independent.select_mode", logSink, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupExpanded;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.LineupExpanded)
        {
            var result = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    IndependentTrainingCatalog.LineupExpandSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Focus;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.Focus)
        {
            var focusAction = settings.TrainingFocus.Trim().ToLowerInvariant() switch
            {
                "stamina" => "independent.focus.stamina",
                "sprint" => "independent.focus.sprint",
                _ => "independent.focus.balanced",
            };
            var result = await RunIndependentActionAsync(
                    connection, pack, state, focusAction, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Agenda;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.Agenda)
        {
            var agendaError = await ConfigureAgendaAsync(
                    connection, pack, settings, state, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (agendaError is not null)
                return agendaError;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Skills;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.Skills)
        {
            var skillError = await ConfigureSkillsAsync(
                    connection, pack, settings, state, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (skillError is not null)
                return skillError;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupPrepared;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.LineupPrepared)
        {
            var scroll = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
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
                    IndependentTrainingCatalog.LineupCollapseSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (collapse is not null)
                return collapse;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupVerified;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        // This probe is always performed in the current run. The persisted
        // step never substitutes for live evidence after a restart.
        if (!state.LineupCollapseVerifiedThisRun)
        {
            var verification = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    IndependentTrainingCatalog.LineupClosedVerifySemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification is not null)
                return verification;
            state.LineupCollapseVerifiedThisRun = true;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupVerified;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ConfigurationStep <= IndependentTrainingConfigurationStep.LineupVerified)
        {
            if (!IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                    settings.LineupStrategy,
                    out var strategyOptionAction,
                    out _))
            {
                return Failure(
                    $"Independent lineup strategy '{settings.LineupStrategy}' is invalid.",
                    state.LastScreenId,
                    state.ActionsCompleted);
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
                        connection, pack, state, action, logSink, cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Strategy;
            state.Stage = IndependentTrainingStage.Start;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<IndependentTrainingResult?> ConfigureAgendaAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var selections = settings.EffectiveAgendaSelections;
        if (selections.Count == 0)
            return null;

        var catalog = IndependentTrainingCatalog.Load();
        foreach (var action in new[]
        {
            "independent.agenda.open",
            "independent.agenda.reset",
            "independent.agenda.reset.confirm",
            "independent.agenda.reset.done",
        })
        {
            var result = await RunIndependentActionAsync(
                    connection, pack, state, action, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        foreach (var selection in selections)
        {
            if (!catalog.TryGetAgendaPickerEntry(selection, out var pickerRace))
            {
                return Failure(
                    $"Independent agenda race '{selection.RaceName}' has no Global Race ID mapping.",
                    state.LastScreenId,
                    state.ActionsCompleted);
            }

            var yearAction = $"independent.agenda.year.{Slug(selection.Year)}";
            var year = await RunIndependentActionAsync(
                    connection, pack, state, yearAction, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (year is not null)
                return year;
            var slot = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
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
                        state.LastScreenId,
                        state.ActionsCompleted);
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
                    connection, pack, state, "independent.agenda.save", logSink, cancellationToken)
                .ConfigureAwait(false);
            if (save is not null)
                return save;
        }

        return await RunIndependentActionAsync(
                connection, pack, state, "independent.agenda.close", logSink, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IndependentTrainingResult?> ConfigureSkillsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSettings settings,
        IndependentTrainingSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var skillIds = settings.EffectiveSkillIds;
        if (skillIds.Count == 0)
            return null;

        var catalog = IndependentTrainingCatalog.Load();
        var scroll = await RunIndependentActionAsync(
                connection,
                pack,
                state,
                IndependentTrainingCatalog.SkillSearchScrollSemanticAction(),
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (scroll is not null)
            return scroll;
        var reset = await RunIndependentActionAsync(
                connection, pack, state, "independent.skills.reset", logSink, cancellationToken)
            .ConfigureAwait(false);
        if (reset is not null)
            return reset;

        foreach (var skillId in skillIds)
        {
            var skill = catalog.Skills.FirstOrDefault(item => item.SkillId == skillId);
            if (skill is null || string.IsNullOrWhiteSpace(skill.EffectiveSearchText))
            {
                return Failure(
                    $"Independent skill {skillId.ToString(CultureInfo.InvariantCulture)} is not selectable.",
                    state.LastScreenId,
                    state.ActionsCompleted);
            }
            if (!IndependentTrainingCatalog.TryGetVerifiedSkillFallback(
                    skill,
                    out var page,
                    out var pickerRow))
            {
                return Failure(
                    $"Independent skill {skillId.ToString(CultureInfo.InvariantCulture)} has no verified search mapping.",
                    state.LastScreenId,
                    state.ActionsCompleted);
            }

            foreach (var action in new[]
            {
                "independent.skills.open",
                IndependentTrainingCatalog.SkillSearchResetSemanticAction(),
                IndependentTrainingCatalog.SkillSearchFocusSemanticAction(),
            })
            {
                var result = await RunIndependentActionAsync(
                        connection, pack, state, action, logSink, cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }
            var input = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
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
                    IndependentTrainingCatalog.SkillSearchSubmitSemanticAction(),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (submit is not null)
                return submit;
            for (var index = 0; index < page; index++)
            {
                var pageScroll = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
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
                    "independent.skills.save",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (save is not null)
                return save;
        }

        return null;
    }

    private async Task<IndependentTrainingResult?> StartIndependentAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSessionState state,
        IndependentCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        // Start checkpoints are resumed at the same top-level stage, but a
        // collapse observation is never trusted across a process boundary.
        var startIssued = state.LastScreenId.Equals(
            StartIssuedScreenId,
            StringComparison.OrdinalIgnoreCase);
        if (!startIssued && !state.LineupCollapseVerifiedThisRun)
        {
            foreach (var action in new[]
            {
                IndependentTrainingCatalog.LineupScrollTopSemanticAction(),
                IndependentTrainingCatalog.LineupCollapseSemanticAction(),
                IndependentTrainingCatalog.LineupClosedVerifySemanticAction(),
            })
            {
                var lineup = await RunIndependentActionAsync(
                        connection,
                        pack,
                        state,
                        action,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (lineup is not null)
                    return lineup;
            }
            state.LineupCollapseVerifiedThisRun = true;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (!startIssued)
        {
            var start = await RunIndependentActionAsync(
                    connection,
                    pack,
                    state,
                    "independent.start",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (start is not null)
                return start;
            state.LastScreenId = StartIssuedScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

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
                    action,
                    logSink,
                    cancellationToken,
                    allowVisualMiss: true)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.LastScreenId = StartIssuedScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        var home = await RunIndependentActionAsync(
                connection,
                pack,
                state,
                IndependentTrainingCatalog.PostStartHomeProbeSemanticAction(),
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (home is not null)
            return home;

        state.LastScreenId = "home";
        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<IndependentTrainingResult?> RunIndependentActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IndependentTrainingSessionState state,
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
        state.LastScreenId = result.LastScreenId;
        if (!result.Succeeded)
        {
            state.RetryCount++;
            return Failure(result.Message, state.LastScreenId, state.ActionsCompleted);
        }
        state.RetryCount = 0;
        state.ActionsCompleted++;
        return null;
    }

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
