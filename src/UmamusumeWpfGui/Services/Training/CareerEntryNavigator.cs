using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed record CareerActionExecutionResult(
    bool Succeeded,
    string Message,
    string LastScreenId)
{
    public static CareerActionExecutionResult Success(string screenId) =>
        new(true, string.Empty, screenId);
}

/// <summary>
/// Internal seam for behavior tests. Production callers continue to use the
/// public <see cref="CareerJsonActionExecutor"/> constructor path.
/// </summary>
internal interface ICareerActionExecutor
{
    Task<CareerActionExecutionResult> RunAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null,
        bool allowVisualMiss = false);

    Task<CareerActionExecutionResult> RunTaskAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null);
}

/// <summary>
/// The small JSON action adapter shared by the entry navigator and
/// Independent setup. It owns no career strategy or session state.
/// </summary>
public sealed class CareerJsonActionExecutor : ICareerActionExecutor
{
    private readonly HachimiJsonPipelineRunner _jsonRunner;

    public CareerJsonActionExecutor(HachimiJsonPipelineRunner jsonRunner)
    {
        _jsonRunner = jsonRunner ?? throw new ArgumentNullException(nameof(jsonRunner));
    }

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
        var resolvedScreenId = screenId;
        if (resolvedScreenId.Equals("career_entry", StringComparison.OrdinalIgnoreCase)
            && pack.ScreenProfile.Find("career_entry") is null)
        {
            resolvedScreenId = "career_final_confirmation";
        }

        var screen = pack.ScreenProfile.Find(resolvedScreenId);
        if (screen is null)
        {
            return new(false,
                $"Screen '{resolvedScreenId}' is missing from screen_profile.json.",
                resolvedScreenId);
        }

        var action = screen.FindAction(actionId);
        if (action is null || string.IsNullOrWhiteSpace(action.Task))
        {
            return new(false,
                $"Screen action '{resolvedScreenId}.{actionId}' is missing from screen_profile.json.",
                resolvedScreenId);
        }

        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                action.Task,
                options,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
            return CareerActionExecutionResult.Success(resolvedScreenId);

        if (allowVisualMiss && IsVisualTaskTimeout(result.Message))
            return CareerActionExecutionResult.Success(resolvedScreenId);

        return new(false,
            $"Could not execute JSON task '{action.Task}' for '{resolvedScreenId}.{actionId}': "
                + result.Message,
            resolvedScreenId);
    }

    public static bool IsVisualTaskTimeout(string message) =>
        message.StartsWith("Timed out waiting for JSON task '", StringComparison.Ordinal);

    public async Task<CareerActionExecutionResult> RunTaskAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null)
    {
        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                taskName,
                options,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded
            ? CareerActionExecutionResult.Success(taskName)
            : new(false, result.Message, taskName);
    }
}

public enum CareerEntryNavigationStep
{
    Home = 0,
    Continue = 1,
    Scenario = 2,
    Trainee = 3,
    Legacy = 4,
    Support = 5,
    FinalConfirmation = 6,
}

public sealed class CareerEntryNavigationState
{
    // This state belongs to one navigation invocation. RetryCount and
    // ActionsCompleted are runtime counters; checkpoint callers should only
    // project Step and LastScreenId into persistent state.
    public CareerEntryNavigationStep Step { get; set; } = CareerEntryNavigationStep.Home;
    public string LastScreenId { get; set; } = "unknown";
    public int RetryCount { get; set; }
    public int ActionsCompleted { get; set; }
}

public sealed record CareerEntryNavigationResult(
    bool Succeeded,
    string Message,
    string LastScreenId,
    int ActionsCompleted);

/// <summary>
/// Lightweight Home -> Career -> Final Confirmation navigation. Both
/// pipelines may use it, but it deliberately stops before any turn engine.
/// </summary>
public sealed class CareerEntryNavigator
{
    private const string FinalConfirmationScreenId = "career_final_confirmation";
    private const double EarlyRecognitionThreshold = 0.985;

    private static readonly HashSet<string> EntryScreenIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "home",
            "career_continue",
            "scenario_select",
            "trainee_select",
            "legacy_select",
            "support_select",
            "support_autofill_confirmation",
            "support_ready",
            "career_races_ready",
            FinalConfirmationScreenId,
        };

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly UraTraineeSelector _traineeSelector;
    private readonly UraLegacySelector _legacySelector;
    private readonly ICareerActionExecutor _actions;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);

    public CareerEntryNavigator(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        UraTraineeSelector traineeSelector,
        UraLegacySelector legacySelector,
        CareerJsonActionExecutor actions)
        : this(
            visualRuntime,
            umaDatabase,
            traineeSelector,
            legacySelector,
            (ICareerActionExecutor)actions)
    {
    }

    internal CareerEntryNavigator(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        UraTraineeSelector traineeSelector,
        UraLegacySelector legacySelector,
        ICareerActionExecutor actions)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _umaDatabase = umaDatabase ?? throw new ArgumentNullException(nameof(umaDatabase));
        _traineeSelector = traineeSelector ?? throw new ArgumentNullException(nameof(traineeSelector));
        _legacySelector = legacySelector ?? throw new ArgumentNullException(nameof(legacySelector));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async Task<CareerEntryNavigationResult> NavigateAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        CareerEntryNavigationState state,
        IGrassTaskLogSink? logSink,
        Func<CareerEntryNavigationState, Task>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);

        RestoreStepFromCheckpoint(state);
        if (state.Step == CareerEntryNavigationStep.Home)
        {
            var home = pack.ScreenProfile.Find("home");
            if (home is null || string.IsNullOrWhiteSpace(home.EntryTask))
            {
                return Failure(
                    "Home entryTask is missing from screen_profile.json.",
                    state);
            }

            // The original Career flow intentionally enters through the Home
            // entry task first. That task clicks the Home/Career entry chain;
            // it must not require recognizing a returned-home capture before
            // the first Career click is issued.
            logSink?.Add("Career Training", "Entering Career from the game Home screen.");
            var homeEntry = await _actions.RunTaskAsync(
                    connection,
                    pack,
                    home.EntryTask,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!homeEntry.Succeeded)
                return Failure(homeEntry.Message, state);

            state.Step = CareerEntryNavigationStep.Continue;
            state.LastScreenId = "home";
            state.ActionsCompleted++;
            await NotifyProgressAsync(progressCallback, state).ConfigureAwait(false);
        }

        for (var attempt = 0; attempt < 160; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await ObserveAsync(connection, pack, state, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                state.RetryCount++;
                if (state.RetryCount >= 12)
                    return Failure("Could not recognize a stable Career entry screen.", state);
                await _visualRuntime.DelayAsync(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            state.RetryCount = 0;
            state.LastScreenId = observation.ScreenId;
            var result = await HandleScreenAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    observation,
                    logSink,
                    progressCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
            state.ActionsCompleted++;
            await NotifyProgressAsync(progressCallback, state).ConfigureAwait(false);
        }

        return Failure("Career entry exceeded the safety action limit.", state);
    }

    private static Task NotifyProgressAsync(
        Func<CareerEntryNavigationState, Task>? progressCallback,
        CareerEntryNavigationState state) =>
        progressCallback is null ? Task.CompletedTask : progressCallback(state);

    private static void RestoreStepFromCheckpoint(CareerEntryNavigationState state)
    {
        if (state.Step != CareerEntryNavigationStep.Home)
            return;

        state.Step = state.LastScreenId.Trim().ToLowerInvariant() switch
        {
            "career_continue" => CareerEntryNavigationStep.Continue,
            "scenario_select" => CareerEntryNavigationStep.Scenario,
            "trainee_select" => CareerEntryNavigationStep.Trainee,
            "legacy_select" => CareerEntryNavigationStep.Legacy,
            "support_select" or "support_autofill_confirmation" or "support_ready"
                or "support_start_transition" => CareerEntryNavigationStep.Support,
            FinalConfirmationScreenId => CareerEntryNavigationStep.FinalConfirmation,
            _ => CareerEntryNavigationStep.Home,
        };
    }

    private async Task<CareerEntryNavigationResult?> HandleScreenAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        CareerEntryNavigationState state,
        EntryObservation observation,
        IGrassTaskLogSink? logSink,
        Func<CareerEntryNavigationState, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        switch (observation.ScreenId)
        {
            case "home":
                var home = pack.ScreenProfile.Find("home");
                if (home is null || string.IsNullOrWhiteSpace(home.EntryTask))
                    return Failure("Home entryTask is missing from screen_profile.json.", state);
                var homeResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "home",
                        "career",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                // Some older profiles put the entry semantic action on the
                // EntryTask only. Fall back to it without inventing a tap.
                if (!homeResult.Succeeded && home.EntryTask is { Length: > 0 })
                {
                    var taskResult = await _actions.RunTaskAsync(
                            connection,
                            pack,
                            home.EntryTask,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!taskResult.Succeeded)
                        return Failure(taskResult.Message, state);
                }
                else if (!homeResult.Succeeded)
                {
                    return Failure(homeResult.Message, state);
                }
                state.Step = CareerEntryNavigationStep.Continue;
                return null;

            case "career_continue":
                var continueResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "career_continue",
                        settings.ContinueExistingCareer ? "resume" : "delete",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!continueResult.Succeeded)
                    return Failure(continueResult.Message, state);
                state.Step = CareerEntryNavigationStep.Scenario;
                return null;

            case "scenario_select":
                var scenarioResult = await HandleScenarioAsync(
                        connection,
                        pack,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!scenarioResult.Succeeded)
                    return Failure(scenarioResult.Message, state);
                state.Step = CareerEntryNavigationStep.Trainee;
                return null;

            case "trainee_select":
                var traineeResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "trainee_select",
                        "pick",
                        logSink,
                        cancellationToken,
                        new HachimiPipelineRunOptions
                        {
                            CustomActionExecutor = async (
                                    actionConnection,
                                    definition,
                                    taskName,
                                    task,
                                    actionLogSink,
                                    actionCancellationToken) =>
                            {
                                var selection = await _traineeSelector.SelectAsync(
                                        actionConnection,
                                        definition,
                                        taskName,
                                        task,
                                        settings.TraineeId,
                                        actionLogSink,
                                        actionCancellationToken)
                                    .ConfigureAwait(false);
                                return selection.Succeeded
                                    ? HachimiCustomActionResult.Success(selection.Message)
                                    : HachimiCustomActionResult.Failure(selection.Message);
                            },
                        })
                    .ConfigureAwait(false);
                if (!traineeResult.Succeeded)
                    return Failure(traineeResult.Message, state);
                state.Step = CareerEntryNavigationStep.Legacy;
                state.LastScreenId = "legacy_select";
                logSink?.Add(
                    "Career Training",
                    "Trainee selection and Next succeeded; continuing directly into Legacy Select.");
                await NotifyProgressAsync(progressCallback, state).ConfigureAwait(false);
                // The trainee_select_pick JSON task chains the Next click. The
                // live flow enters Legacy immediately after that click, so
                // invoke the Legacy selector directly instead of waiting for
                // the generic observer to rediscover a short-lived page.
                return await HandleLegacySelectionAsync(
                        connection,
                        pack,
                        settings,
                        state,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);

            case "legacy_select":
                return await HandleLegacySelectionAsync(
                        connection,
                        pack,
                        settings,
                        state,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);

            case "support_select":
                var supportResult = await HandleSupportAsync(
                        connection,
                        pack,
                        settings,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!supportResult.Succeeded)
                    return Failure(supportResult.Message, state);
                state.LastScreenId = "support_start_transition";
                return null;

            case "support_autofill_confirmation":
                var autofillResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "support_autofill_confirmation",
                        "autofill_ok",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!autofillResult.Succeeded)
                    return Failure(autofillResult.Message, state);
                state.LastScreenId = "support_start_transition";
                return null;

            case "support_ready":
                var readyResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "support_ready",
                        "start",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!readyResult.Succeeded)
                    return Failure(readyResult.Message, state);
                state.LastScreenId = "support_start_transition";
                return null;

            case FinalConfirmationScreenId:
                state.Step = CareerEntryNavigationStep.FinalConfirmation;
                return new(true, "Career entry reached Final Confirmation.",
                    FinalConfirmationScreenId, state.ActionsCompleted);

            default:
                return Failure(
                    $"Unsupported stable Career entry screen '{observation.ScreenId}'.",
                    state);
        }
    }

    private async Task<CareerEntryNavigationResult?> HandleLegacySelectionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        CareerEntryNavigationState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var legacyResult = await _actions.RunAsync(
                connection,
                pack,
                "legacy_select",
                "choose",
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    CustomActionExecutor = async (
                            actionConnection,
                            definition,
                            taskName,
                            task,
                            actionLogSink,
                            actionCancellationToken) =>
                    {
                        var selection = await _legacySelector.SelectAsync(
                                actionConnection,
                                definition,
                                settings,
                                actionLogSink,
                                actionCancellationToken)
                            .ConfigureAwait(false);
                        return selection.Succeeded
                            ? HachimiCustomActionResult.Success(selection.Message)
                            : HachimiCustomActionResult.Failure(selection.Message);
                    },
                })
            .ConfigureAwait(false);
        if (!legacyResult.Succeeded)
            return Failure(legacyResult.Message, state);

        state.Step = CareerEntryNavigationStep.Support;
        return null;
    }

    private async Task<CareerActionExecutionResult> HandleSupportAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var mode = settings.SupportDeckMode.Trim().ToLowerInvariant();
        if (mode == "auto" || mode.Length == 0)
        {
            return await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    "auto_fill",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (mode == "highest-star")
        {
            return await HandleHighestStarSupportAsync(
                    connection,
                    pack,
                    settings,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var expectedOwnCardCount = settings.FriendSupportCardId is > 0 ? 5 : 0;
        if (settings.SupportCardIds.Count is not (5 or 6)
            || (expectedOwnCardCount > 0 && settings.SupportCardIds.Count != expectedOwnCardCount))
        {
            return new(false,
                $"Support deck mode '{settings.SupportDeckMode}' requires "
                    + (expectedOwnCardCount > 0 ? "5 own cards with a guest card." : "5 or 6 cards."),
                "support_select");
        }

        var reset = await _actions.RunAsync(
                connection, pack, "support_select", "reset_if_needed", logSink, cancellationToken)
            .ConfigureAwait(false);
        if (!reset.Succeeded)
            return reset;

        // Exact card templates are data-driven and the JSON action owns the
        // click. Filtering/sorting remains optional for this lightweight
        // entry component; the normal pipeline retains its richer selector.
        foreach (var cardId in settings.SupportCardIds)
        {
            if (!_umaDatabase.TryGetSupportCard(cardId, out var card)
                || card is null
                || !card.Available)
            {
                return new(false,
                    $"Configured support card {cardId.ToString(CultureInfo.InvariantCulture)} "
                        + "was not found or is unavailable.",
                    "support_select");
            }

            var template = ResolveSupportTemplate(pack, cardId);
            if (template is null)
            {
                return new(false,
                    $"Support card {cardId.ToString(CultureInfo.InvariantCulture)} has no local selection template.",
                    "support_select");
            }

            var open = await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    "open",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!open.Succeeded)
                return open;

            var select = await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.select_exact_card",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card_exact"] = template,
                        },
                    })
                .ConfigureAwait(false);
            if (!select.Succeeded)
                return select;
        }

        if (settings.FriendSupportCardId is > 0)
        {
            var friend = await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    "open",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!friend.Succeeded)
                return friend;
            var friendTemplate = ResolveSupportTemplate(pack, settings.FriendSupportCardId.Value);
            if (friendTemplate is null)
            {
                return new(false,
                    $"Guest support card {settings.FriendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                        + "has no local selection template.",
                    "support_select");
            }
            var friendSelection = await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.select_exact_card",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card_exact"] = friendTemplate,
                        },
                    })
                .ConfigureAwait(false);
            if (!friendSelection.Succeeded)
                return friendSelection;
        }

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "start",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> HandleHighestStarSupportAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var requiredTypes = SupportDeckPresetCatalog.GetRequiredTypes(settings.SupportDeckPreset);
        if (requiredTypes is null)
        {
            return new(false,
                "Highest-star support selection requires a support deck preset.",
                "support_select");
        }

        UmaSupportCardRecord? friendCard = null;
        if (settings.FriendSupportCardId is > 0)
        {
            if (!_umaDatabase.TryGetSupportCard(
                    settings.FriendSupportCardId.Value,
                    out friendCard)
                || friendCard is null
                || !friendCard.Available)
            {
                return new(false,
                    $"Configured guest support card {settings.FriendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                        + "was not found or is unavailable.",
                    "support_select");
            }
        }

        var guestType = friendCard?.Type?.Trim();
        if (string.IsNullOrWhiteSpace(guestType))
        {
            guestType = requiredTypes.ContainsKey("Friend")
                ? _umaDatabase.SupportCards
                    .Where(card => card.Available && !string.IsNullOrWhiteSpace(card.Type))
                    .Select(card => card.Type.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : requiredTypes.Keys.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(guestType))
        {
            return new(false,
                "The selected support preset has no usable type for the guest card.",
                "support_select");
        }

        var ownRequiredTypes = requiredTypes.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        if (ownRequiredTypes.Remove("Friend"))
        {
            // Friend is represented by the separate guest slot and is not an
            // owned-card filter.
        }
        else if (ownRequiredTypes.TryGetValue(guestType, out var guestCount))
        {
            if (guestCount <= 1)
                ownRequiredTypes.Remove(guestType);
            else
                ownRequiredTypes[guestType] = guestCount - 1;
        }
        else if (friendCard is not null)
        {
            return new(false,
                $"Guest support card {friendCard.SupportCardId.ToString(CultureInfo.InvariantCulture)} "
                    + $"does not fit the selected support deck preset '{settings.SupportDeckPreset}'.",
                "support_select");
        }

        foreach (var required in ownRequiredTypes)
        {
            for (var index = 0; index < required.Value; index++)
            {
                var open = await _actions.RunAsync(
                        connection,
                        pack,
                        "support_select",
                        "open",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!open.Succeeded)
                    return open;

                var filter = await ConfigureHighestStarFilterAsync(
                        connection,
                        pack,
                        required.Key,
                        friendPage: false,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!filter.Succeeded)
                    return filter;

                var selected = await SelectHighestSupportCardAsync(
                        connection,
                        pack,
                        required.Key,
                        friendPage: false,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!selected.Succeeded)
                    return selected;
            }
        }

        var openGuest = await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "open",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (!openGuest.Succeeded)
            return openGuest;

        var guestFilter = await ConfigureHighestStarFilterAsync(
                connection,
                pack,
                guestType,
                friendPage: true,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (!guestFilter.Succeeded)
            return guestFilter;

        var guestSelection = friendCard is null
            ? await SelectHighestSupportCardAsync(
                    connection,
                    pack,
                    guestType,
                    friendPage: true,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false)
            : await SelectExactSupportCardAsync(
                    connection,
                    pack,
                    friendCard.SupportCardId,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!guestSelection.Succeeded)
            return guestSelection;

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "start",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> ConfigureHighestStarFilterAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportType,
        bool friendPage,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var filterKey = SupportDeckPresetCatalog.GetFilterKey(supportType);
        if (filterKey is null)
        {
            return new(false,
                $"Support type '{supportType}' has no filter mapping.",
                "support_select");
        }

        var actions = new[]
        {
            "ranked.display_settings",
            friendPage ? "ranked.friend_sort_level" : "ranked.sort_level",
            "ranked.filter_tab",
            "ranked.filter_reset",
            "ranked.filter_sr",
            "ranked.filter_ssr",
            $"ranked.filter_{filterKey}",
            "ranked.filter_apply",
            "ranked.display_settings",
            friendPage ? "ranked.friend_sort_level" : "ranked.sort_level",
            "ranked.sort_apply",
            "ranked.sort_desc",
        };
        foreach (var action in actions)
        {
            var result = await _actions.RunAsync(
                    connection,
                    pack,
                    "support_select",
                    action,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                return result;
        }

        return CareerActionExecutionResult.Success("support_select");
    }

    private async Task<CareerActionExecutionResult> SelectHighestSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportType,
        bool friendPage,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var filterKey = SupportDeckPresetCatalog.GetFilterKey(supportType);
        var templatePrefix = friendPage
            ? "friend_type_"
            : "type_";
        var typeTemplate = filterKey is null
            ? null
            : UraScenarioResourceResolver.Resolve(
                pack,
                $"screens/templates/support_cards/{templatePrefix}{filterKey}.png");
        if (typeTemplate is null || !File.Exists(typeTemplate))
        {
            return new(false,
                $"Support type '{supportType}' has no recognition template.",
                "support_select");
        }

        var taskPrefix = friendPage
            ? "support_select_support_friend_top_card_"
            : "support_select_support_top_card_";
        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                friendPage ? "ranked.select_friend_highest_card" : "ranked.select_highest_card",
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    TemplateOverrides = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [taskPrefix + "ssr"] = typeTemplate,
                        [taskPrefix + "sr"] = typeTemplate,
                    },
                })
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> SelectExactSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        int supportCardId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var template = ResolveSupportTemplate(pack, supportCardId);
        if (template is null)
        {
            return new(false,
                $"Support card {supportCardId.ToString(CultureInfo.InvariantCulture)} "
                    + "has no local selection template.",
                "support_select");
        }

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "ranked.select_exact_card",
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    TemplateOverrides = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["support_select_support_card_exact"] = template,
                    },
                })
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> HandleScenarioAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var selection = pack.ScreenProfile.ScenarioSelection;
        if (selection is null)
        {
            return await _actions.RunAsync(
                    connection, pack, "scenario_select", "next", logSink, cancellationToken)
                .ConfigureAwait(false);
        }

        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return new(false, "Could not capture the scenario selection screen.", "scenario_select");

        TemplateMatchResult? best = null;
        foreach (var templatePath in selection.Recognition.GetTemplates())
        {
            var template = await LoadTemplateCachedAsync(
                    UraScenarioResourceResolver.Resolve(pack, templatePath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                continue;
            var match = TemplateMatcher.Find(
                frame,
                template,
                selection.Recognition.Roi,
                selection.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
            if (match.Found && (best is null || match.Score > best.Score))
                best = match;
        }

        var action = best is { Found: true } ? "next" : "next_card";
        return await _actions.RunAsync(
                connection, pack, "scenario_select", action, logSink, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EntryObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerEntryNavigationState state,
        CancellationToken cancellationToken)
    {
        var candidates = pack.ScreenProfile.Screens
            .Where(screen => EntryScreenIds.Contains(screen.ScreenId))
            .Where(screen => state.Step != CareerEntryNavigationStep.FinalConfirmation
                || screen.ScreenId.Equals(FinalConfirmationScreenId, StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.LastScreenId != "support_start_transition"
                || screen.ScreenId is "support_autofill_confirmation"
                    or "support_ready"
                    or FinalConfirmationScreenId)
            .Where(screen => state.Step != CareerEntryNavigationStep.Scenario
                || screen.ScreenId.Equals("scenario_select", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Continue
                || !screen.ScreenId.Equals("home", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Trainee
                || screen.ScreenId.Equals("trainee_select", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Legacy
                || screen.ScreenId.Equals("legacy_select", StringComparison.OrdinalIgnoreCase))
            .OrderBy(screen => GetPriority(screen.ScreenId))
            .ToArray();

        var frames = new List<GrayImage>(2);
        for (var sample = 0; sample < 2; sample++)
        {
            var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (frame is not null)
                frames.Add(frame);
            if (sample == 0)
                await _visualRuntime.DelayAsync(120, cancellationToken).ConfigureAwait(false);
        }
        if (frames.Count == 0)
            return null;

        EntryObservation? best = null;
        foreach (var frame in frames)
        {
            EntryObservation? frameBest = null;
            foreach (var screen in candidates)
            {
                foreach (var templatePath in screen.Templates)
                {
                    var template = await LoadTemplateCachedAsync(
                            UraScenarioResourceResolver.Resolve(pack, templatePath),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (template is null)
                        continue;
                    var match = TemplateMatcher.Find(
                        frame,
                        template,
                        screen.Recognition.Roi,
                        screen.Recognition.TemplateThreshold,
                        pack.ScreenProfile.ReferenceWidth,
                        pack.ScreenProfile.ReferenceHeight);
                    if (match.Found
                        && (frameBest is null || match.Score > frameBest.Score))
                    {
                        frameBest = new(screen.ScreenId, match.Score);
                    }
                    if (frameBest is { Score: >= EarlyRecognitionThreshold })
                        break;
                }
                if (frameBest is { Score: >= EarlyRecognitionThreshold })
                    break;
            }
            if (frameBest is not null && (best is null || frameBest.Score > best.Score))
                best = frameBest;
        }
        return best;
    }

    private Task<GrayImage?> LoadTemplateCachedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lazy = _templateCache.GetOrAdd(
            path,
            key => new Lazy<Task<GrayImage?>>(
                () => _visualRuntime.LoadTemplateAsync(key, string.Empty, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private string? ResolveSupportTemplate(UraScenarioPack pack, int supportCardId)
    {
        var directory = _umaDatabase.GetSupportCardTemplateDirectory(supportCardId);
        if (Directory.Exists(directory))
        {
            var file = Directory.EnumerateFiles(directory)
                .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (file is not null)
                return file;
        }
        var fallback = Path.Combine(
            pack.RootDirectory,
            "screens",
            "templates",
            "support_cards",
            supportCardId.ToString(CultureInfo.InvariantCulture) + ".png");
        return File.Exists(fallback) ? fallback : null;
    }

    private static int GetPriority(string screenId) => screenId switch
    {
        "career_continue" => 0,
        "home" => 1,
        "scenario_select" => 2,
        "trainee_select" => 3,
        "legacy_select" => 4,
        "support_autofill_confirmation" => 5,
        "support_ready" => 6,
        "support_select" => 7,
        FinalConfirmationScreenId => 8,
        _ => 20,
    };

    private static CareerEntryNavigationResult Failure(
        string message,
        CareerEntryNavigationState state) =>
        new(false, message, state.LastScreenId, state.ActionsCompleted);

    private sealed record EntryObservation(string ScreenId, double Score);
}
