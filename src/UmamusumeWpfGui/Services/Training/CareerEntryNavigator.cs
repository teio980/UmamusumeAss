using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public enum CareerEntryNavigationStep
{
    Home = 0,
    Continue = 1,
    Scenario = 2,
    Trainee = 3,
    Legacy = 4,
    Support = 5,
    FinalConfirmation = 6,
    Career = 7,
}

public sealed class CareerEntryNavigationState
{
    // This state belongs to one navigation invocation. RetryCount and
    // ActionsCompleted are runtime counters.
    public CareerEntryNavigationStep Step { get; set; } = CareerEntryNavigationStep.Home;
    public string LastScreenId { get; set; } = "unknown";
    public int RetryCount { get; set; }
    public int ActionsCompleted { get; set; }
    // The Resume action may enter an existing Career directly. This is an
    // entry-target hint, not a frontend training-mode setting.
    public bool ResumeDirectlyToCareer { get; set; }
}

public sealed record CareerEntryNavigationResult(
    bool Succeeded,
    string Message,
    string LastScreenId,
    int ActionsCompleted)
{
    public CareerObservation? ResumeObservation { get; init; }
}

/// <summary>
/// Lightweight Home -> Career -> Final Confirmation navigation. Both
/// pipelines may use it, but it deliberately stops before any turn engine.
/// </summary>
public sealed class CareerEntryNavigator
{
    private const string FinalConfirmationScreenId = "career_final_confirmation";
    private const int MaxStableScreenRecognitionRetries = 30;

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
            "career_main",
            "career_races_ready",
            FinalConfirmationScreenId,
        };

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly UraTraineeSelector _traineeSelector;
    private readonly UraLegacySelector _legacySelector;
    private readonly ICareerActionExecutor _actions;
    private readonly CareerScreenObserver _resumeObserver;
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
        _resumeObserver = new CareerScreenObserver(visualRuntime);
    }

    public async Task<CareerEntryNavigationResult> NavigateAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        ICareerEntrySelectionSettings settings,
        CareerEntryNavigationState state,
        IGrassTaskLogSink? logSink,
        Func<CareerEntryNavigationState, Task>? progressCallback = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);

        if (_actions is ICareerTaskLogAware taskLogAware)
            taskLogAware.SetTaskLogSink(taskLogSink);

        RestoreStepFromLastScreen(state);
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
            // Resume may reopen an objective banner, race result, or event.
            // Use the runtime observer before a new session has any action
            // history, rather than requiring Career Main underneath an overlay.
            var resumingCareer = state.ResumeDirectlyToCareer
                && state.Step == CareerEntryNavigationStep.Career;
            var resumedCareer = resumingCareer
                ? await _resumeObserver.ObserveAsync(
                        connection, pack, new UraCareerSessionState(),
                        careerStartTransitionExpected: false,
                        cancellationToken: cancellationToken,
                        careerOnly: true,
                        includeMainDetails: false)
                    .ConfigureAwait(false)
                : null;
            var observation = resumingCareer
                ? resumedCareer is null
                    ? null
                    : new EntryObservation(resumedCareer.ScreenId, resumedCareer.Score)
                : await ObserveAsync(connection, pack, state, cancellationToken)
                    .ConfigureAwait(false);
            if (observation is null)
            {
                state.RetryCount++;
                if (state.RetryCount >= MaxStableScreenRecognitionRetries)
                    return Failure("Could not recognize a stable Career entry screen.", state);
                await _visualRuntime.DelayAsync(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            state.RetryCount = 0;
            state.LastScreenId = observation.ScreenId;
            if (resumedCareer is not null)
            {
                return new(
                    true,
                    "Career entry reached the existing Career.",
                    resumedCareer.ScreenId,
                    state.ActionsCompleted)
                {
                    ResumeObservation = resumedCareer,
                };
            }
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

    private static void RestoreStepFromLastScreen(CareerEntryNavigationState state)
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
            "career_main" or "career_races_ready" => CareerEntryNavigationStep.Career,
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
                var restartingAfterCareerDelete =
                    state.Step == CareerEntryNavigationStep.Scenario;
                CareerActionExecutionResult homeResult;
                if (restartingAfterCareerDelete)
                {
                    // Deleting the existing Career returns the game to Home.
                    // Reuse the profile's normal Home -> Career task graph so
                    // the new Career starts at scenario selection without
                    // duplicating entry logic in this shared navigator.
                    logSink?.Add(
                        "Career Training",
                        "Career data deleted; reopening Career to select a scenario.");
                    homeResult = await _actions.RunTaskAsync(
                            connection,
                            pack,
                            home.EntryTask,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    homeResult = await _actions.RunAsync(
                            connection,
                            pack,
                            "home",
                            "home.career",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    // Some older profiles put the entry semantic action on
                    // the EntryTask only. Fall back to it without inventing a tap.
                    if (!homeResult.Succeeded && home.EntryTask is { Length: > 0 })
                    {
                        homeResult = await _actions.RunTaskAsync(
                                connection,
                                pack,
                                home.EntryTask,
                                logSink,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                if (!homeResult.Succeeded)
                    return Failure(homeResult.Message, state);
                state.Step = restartingAfterCareerDelete
                    ? CareerEntryNavigationStep.Scenario
                    : CareerEntryNavigationStep.Continue;
                return null;

            case "career_continue":
                var continueResult = await _actions.RunAsync(
                        connection,
                        pack,
                        "career_continue",
                        settings.ContinueExistingCareer
                            ? "career.continue.resume"
                            : "career.continue.delete",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!continueResult.Succeeded)
                    return Failure(continueResult.Message, state);

                if (!settings.ContinueExistingCareer)
                {
                    // Delete Data returns the game to Home, but that Home
                    // frame is not guaranteed to match the returned-home
                    // recognition template (the close dialog can still be
                    // fading out).  Restart the complete entry chain
                    // explicitly instead of waiting for a fragile Home
                    // observation: home selected/unselected -> Career ->
                    // Scenario Select.
                    var restartHomeScreen = pack.ScreenProfile.Find("home");
                    if (restartHomeScreen is null
                        || string.IsNullOrWhiteSpace(restartHomeScreen.EntryTask))
                        return Failure(
                            "Home entryTask is missing from screen_profile.json.",
                            state);

                    logSink?.Add(
                        "Career Training",
                        "Career data deleted; restarting Home -> Career entry before selecting a scenario.");
                    var restartHome = await _actions.RunTaskAsync(
                            connection,
                            pack,
                            restartHomeScreen.EntryTask,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!restartHome.Succeeded)
                        return Failure(restartHome.Message, state);

                    // The Home entry task already clicked Career.  Ignore
                    // transient Home frames and wait only for Scenario Select
                    // so the entry task cannot be repeated in a loop.
                    state.Step = CareerEntryNavigationStep.Scenario;
                    state.LastScreenId = "career_entry_transition";
                    return null;
                }

                state.Step = state.ResumeDirectlyToCareer
                    ? CareerEntryNavigationStep.Career
                    : CareerEntryNavigationStep.Scenario;
                if (state.ResumeDirectlyToCareer)
                    state.LastScreenId = "career_resume_transition";
                return null;

            case "career_main":
            case "career_races_ready":
                if (state.Step != CareerEntryNavigationStep.Career)
                {
                    return Failure(
                        $"Career screen '{observation.ScreenId}' is not valid during entry step '{state.Step}'.",
                        state);
                }
                state.Step = CareerEntryNavigationStep.Career;
                return new(
                    true,
                    "Career entry reached the existing Career.",
                    observation.ScreenId,
                    state.ActionsCompleted);

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
                        "trainee.pick",
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
                                        pack.VisualResources,
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
                        "support.autofill_ok",
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
                        "support.start",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!readyResult.Succeeded)
                    return Failure(readyResult.Message, state);
                // Start Career has already been clicked.  The formation page
                // can remain visible for a few frames while the game opens
                // Final Confirmation; switch the navigation stage now so a
                // stale support_ready match cannot click Start Career twice.
                state.Step = CareerEntryNavigationStep.FinalConfirmation;
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
                "legacy.choose",
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
                                pack.VisualResources,
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
                    "support.auto_fill",
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
                connection, pack, "support_select", "support.reset_if_needed", logSink, cancellationToken)
            .ConfigureAwait(false);
        if (!reset.Succeeded)
            return reset;

        // The sixth card ID in older selections represents the guest slot.
        // Keep that input format, but never open it as an owned-card slot.
        var ownCardIds = settings.SupportCardIds.Take(5).ToArray();
        var friendCardId = settings.FriendSupportCardId is > 0
            ? settings.FriendSupportCardId
            : settings.SupportCardIds.Count == 6
                ? settings.SupportCardIds[5]
                : null;
        for (var slotIndex = 0; slotIndex < ownCardIds.Length; slotIndex++)
        {
            var cardId = ownCardIds[slotIndex];
            if (!_umaDatabase.TryGetSupportCard(cardId, out var card)
                || card is null
                || !card.Available)
            {
                return new(false,
                    $"Configured support card {cardId.ToString(CultureInfo.InvariantCulture)} "
                        + "was not found or is unavailable.",
                    "support_select");
            }

            if (!TryResolveSupportCardFilters(card, out var rarityFilter, out var typeFilter))
            {
                return new(false,
                    $"Configured support card {cardId.ToString(CultureInfo.InvariantCulture)} "
                        + $"has unmapped filter metadata (rarity '{card.Rarity}', type '{card.Type}').",
                    "support_select");
            }

            var template = ResolveSupportTemplate(pack, cardId);
            if (template is null)
            {
                return new(false,
                    $"Support card {cardId.ToString(CultureInfo.InvariantCulture)} has no local selection template.",
                    "support_select");
            }

            var open = await OpenSupportSlotAsync(
                    connection, pack, slotIndex, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (!open.Succeeded)
                return AddSupportCardContext(open, cardId);

            var filter = await ConfigureSupportFilterAsync(
                    connection,
                    pack,
                    [rarityFilter],
                    typeFilter,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!filter.Succeeded)
                return AddSupportCardContext(filter, cardId);

            var select = await SelectExactSupportCardAsync(
                    connection,
                    pack,
                    cardId,
                    friendPage: false,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!select.Succeeded)
                return AddSupportCardContext(select, cardId);
        }

        if (friendCardId is > 0)
        {
            if (!_umaDatabase.TryGetSupportCard(friendCardId.Value, out var friendCard)
                || friendCard is null
                || !friendCard.Available)
            {
                return new(false,
                    $"Configured guest support card {friendCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                        + "was not found or is unavailable.",
                    "support_select");
            }

            if (!TryResolveSupportCardFilters(friendCard, out var friendRarityFilter, out var friendTypeFilter))
            {
                return new(false,
                    $"Configured guest support card {friendCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                        + $"has unmapped filter metadata (rarity '{friendCard.Rarity}', type '{friendCard.Type}').",
                    "support_select");
            }

            var friendTemplate = ResolveSupportTemplate(pack, friendCardId.Value);
            if (friendTemplate is null)
            {
                return new(false,
                    $"Guest support card {friendCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                        + "has no local selection template.",
                    "support_select");
            }

            var friend = await OpenFriendSupportSlotAsync(
                    connection, pack, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (!friend.Succeeded)
                return AddSupportCardContext(friend, friendCardId.Value);

            var friendFilter = await ConfigureSupportFilterAsync(
                    connection,
                    pack,
                    [friendRarityFilter],
                    friendTypeFilter,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!friendFilter.Succeeded)
                return AddSupportCardContext(friendFilter, friendCardId.Value);

            var friendSelection = await SelectExactSupportCardAsync(
                    connection,
                    pack,
                    friendCardId.Value,
                    friendPage: true,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!friendSelection.Succeeded)
                return AddSupportCardContext(friendSelection, friendCardId.Value);
        }

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "support.start",
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
        string? friendRarityFilter = null;
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

            if (!TryResolveSupportCardFilters(friendCard, out var rarityFilter, out _))
            {
                return new(false,
                    $"Configured guest support card {friendCard.SupportCardId.ToString(CultureInfo.InvariantCulture)} "
                        + $"has unmapped filter metadata (rarity '{friendCard.Rarity}', type '{friendCard.Type}').",
                    "support_select");
            }

            friendRarityFilter = rarityFilter;
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

        var reset = await _actions.RunAsync(
                connection, pack, "support_select", "support.reset_if_needed", logSink, cancellationToken)
            .ConfigureAwait(false);
        if (!reset.Succeeded)
            return reset;

        var ownSlotIndex = 0;
        foreach (var required in ownRequiredTypes)
        {
            for (var index = 0; index < required.Value; index++)
            {
                var open = await OpenSupportSlotAsync(
                        connection, pack, ownSlotIndex++, logSink, cancellationToken)
                    .ConfigureAwait(false);
                if (!open.Succeeded)
                    return open;

                var filter = await ConfigureSupportFilterAsync(
                        connection,
                        pack,
                        ["sr", "ssr"],
                        required.Key,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!filter.Succeeded)
                    return filter;

                var sort = await ConfigureSupportSortAsync(
                        connection, pack, friendPage: false, logSink, cancellationToken)
                    .ConfigureAwait(false);
                if (!sort.Succeeded)
                    return sort;

                var selected = await SelectHighestSupportCardAsync(
                        connection,
                        pack,
                        required.Key,
                        friendPage: false,
                        cardIndex: index,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!selected.Succeeded)
                    return selected;
            }
        }

        var openGuest = await OpenFriendSupportSlotAsync(
                connection, pack, logSink, cancellationToken)
            .ConfigureAwait(false);
        if (!openGuest.Succeeded)
            return openGuest;

        var guestFilter = await ConfigureSupportFilterAsync(
                connection,
                pack,
                friendRarityFilter is not null ? [friendRarityFilter] : ["sr", "ssr"],
                guestType,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (!guestFilter.Succeeded)
            return guestFilter;

        var guestSort = await ConfigureSupportSortAsync(
                connection, pack, friendPage: true, logSink, cancellationToken)
            .ConfigureAwait(false);
        if (!guestSort.Succeeded)
            return guestSort;

        var guestSelection = friendCard is null
            ? await SelectHighestSupportCardAsync(
                    connection,
                    pack,
                    guestType,
                    friendPage: true,
                    cardIndex: 0,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false)
            : await SelectExactSupportCardAsync(
                    connection,
                    pack,
                    friendCard.SupportCardId,
                    friendPage: true,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!guestSelection.Succeeded)
            return guestSelection;

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                "support.start",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> ConfigureSupportFilterAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IReadOnlyList<string> rarityFilters,
        string supportType,
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

        if (rarityFilters.Count == 0
            || rarityFilters.Any(rarity => rarity is not ("r" or "sr" or "ssr")))
        {
            return new(false,
                "The requested support rarity filter is invalid.",
                "support_select");
        }

        var actions = new List<string>
        {
            "support.ranked.display_settings",
            "support.ranked.filter_tab",
            "support.ranked.filter_reset",
        };
        actions.AddRange(rarityFilters.Select(rarity => $"support.ranked.filter_{rarity}"));
        actions.AddRange(
        [
            $"support.ranked.filter_{filterKey}",
            "support.ranked.filter_apply",
        ]);
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

    private async Task<CareerActionExecutionResult> ConfigureSupportSortAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        bool friendPage,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        string[] actions =
        [
            "support.ranked.display_settings",
            friendPage ? "support.ranked.friend_sort_level" : "support.ranked.sort_level",
            "support.ranked.sort_apply",
            "support.ranked.sort_desc",
        ];
        foreach (var action in actions)
        {
            var result = await _actions.RunAsync(
                    connection, pack, "support_select", action, logSink, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                return result;
        }

        return CareerActionExecutionResult.Success("support_select");
    }

    private Task<CareerActionExecutionResult> OpenSupportSlotAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        int slotIndex,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (slotIndex < 0)
        {
            return Task.FromResult(new CareerActionExecutionResult(
                false,
                $"Support slot {slotIndex + 1} is outside the formation.",
                "support_select"));
        }

        if (pack.VisualResources?.TryGetRegion(
                $"career.entry.support.slot.{slotIndex + 1}",
                out var slotRegion) != true
            || slotRegion?.Roi is not { Length: >= 4 } slotRoi)
        {
            return Task.FromResult(new CareerActionExecutionResult(
                false,
                $"Career visual catalog is missing support slot {slotIndex + 1} ROI.",
                "support_select"));
        }

        var openTask = GetDeclaredTaskName(pack, "support_select", "support.open");

        return _actions.RunAsync(
            connection,
            pack,
            "support_select",
            "support.open",
            logSink,
            cancellationToken,
            new HachimiPipelineRunOptions
            {
                SearchRoiOverrides = new Dictionary<string, IReadOnlyList<int[]>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [openTask] = [(int[])slotRoi.Clone()],
                },
            });
    }

    private async Task<CareerActionExecutionResult> OpenFriendSupportSlotAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var open = await OpenSupportSlotAsync(
                connection, pack, 5, logSink, cancellationToken)
            .ConfigureAwait(false);
        if (open.Succeeded)
            return open;

        // Formation Reset clears owned cards but keeps the borrowed card.
        // A filled Friends slot opens a Borrow Card dialog with a Remove action.
        var friendSlotCenter = GetRequiredRegionCenter(pack, "career.entry.support.friend_slot.open");
        await _visualRuntime.TapAsync(
                connection,
                friendSlotCenter.X,
                friendSlotCenter.Y,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight,
                "support_select_friend_open_selected", cancellationToken)
            .ConfigureAwait(false);
        var removeRegion = RequireVisualRegion(pack, "career.entry.support.friend_remove");
        var removeRoi = GetRequiredRegionRoi(pack, "career.entry.support.friend_remove");
        var remove = await _visualRuntime.WaitForTextAsync(
                connection,
                "Remove",
                removeRoi,
                removeRegion.Threshold
                    ?? throw new InvalidDataException("Support friend Remove threshold is missing."),
                unique: true,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight,
                2_500,
                150,
                "en-US",
                "support_select_friend_remove_probe",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (remove?.Found != true || remove.Match is null)
        {
            return new(false,
                "The Friends slot is occupied, but the Borrow Card Remove action could not be verified.",
                "support_select");
        }

        await _visualRuntime.TapTextAsync(
                connection, remove.Match, null, null,
                "support_select_friend_remove", cancellationToken)
            .ConfigureAwait(false);
        logSink?.Add("Career Training", "Removed the previous borrowed card before selecting a friend card.");
        return await OpenSupportSlotAsync(
                connection, pack, 5, logSink, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryResolveSupportCardFilters(
        UmaSupportCardRecord card,
        out string rarityFilter,
        out string typeFilter)
    {
        rarityFilter = card.Rarity.Trim().ToLowerInvariant() switch
        {
            "1" or "r" => "r",
            "2" or "sr" => "sr",
            "3" or "ssr" => "ssr",
            _ => string.Empty,
        };
        typeFilter = SupportDeckPresetCatalog.GetFilterKey(card.Type) ?? string.Empty;
        return rarityFilter.Length > 0 && typeFilter.Length > 0;
    }

    private static CareerActionExecutionResult AddSupportCardContext(
        CareerActionExecutionResult result,
        int supportCardId) =>
        result.Succeeded
            ? result
            : new(
                false,
                $"Support card {supportCardId.ToString(CultureInfo.InvariantCulture)}: {result.Message}",
                result.LastScreenId);

    private async Task<CareerActionExecutionResult> SelectHighestSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportType,
        bool friendPage,
        int cardIndex,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var filterKey = SupportDeckPresetCatalog.GetFilterKey(supportType);
        var visualResourceId = $"career.entry.support.type.{(friendPage ? "friend." : string.Empty)}{filterKey}";
        var typeTemplate = filterKey is not null && pack.VisualResources is not null
            ? pack.VisualResources.ResolveVisualResource(visualResourceId)
            : null;
        if (typeTemplate is null || !File.Exists(typeTemplate))
        {
            return new(false,
                $"Support type '{supportType}' has no recognition template.",
                "support_select");
        }

        var actionId = friendPage
            ? "support.ranked.select_friend_highest_card"
            : "support.ranked.select_highest_card";
        var templateTaskIds = GetActionTemplateTaskIds(pack, "support_select", actionId);
        if (templateTaskIds.Count == 0)
        {
            return new(false,
                $"Support action '{actionId}' has no declared template tasks.",
                "support_select");
        }
        // Already selected cards remain at the front of the filtered list.
        // Move to the next card of this type when filling another owned slot.
        int[]? topCardRoi = null;
        if (!friendPage)
        {
            var baseCardRegion = RequireVisualRegion(pack, "career.entry.support.card_selection");
            if (baseCardRegion.Roi is not { Length: >= 4 })
            {
                return new(false,
                    "Support card selection region is missing its base ROI.",
                    "support_select");
            }
            topCardRoi = (int[])baseCardRegion.Roi.Clone();
            topCardRoi[0] += GetRegionMetadataInt(baseCardRegion, "horizontalStep") * cardIndex;
        }
        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                actionId,
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    TemplateOverrides = templateTaskIds.ToDictionary(
                        taskId => taskId,
                        _ => typeTemplate,
                        StringComparer.OrdinalIgnoreCase),
                    RoiOverrides = friendPage
                        ? null
                        : templateTaskIds.ToDictionary(
                            taskId => taskId,
                            _ => (int[])topCardRoi!.Clone(),
                            StringComparer.OrdinalIgnoreCase),
                })
            .ConfigureAwait(false);
    }

    private async Task<CareerActionExecutionResult> SelectExactSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        int supportCardId,
        bool friendPage,
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

        var semanticActionId = "support.ranked.select_exact_card";
        var templateTaskIds = GetActionTemplateTaskIds(
            pack, "support_select", semanticActionId);
        if (templateTaskIds.Count == 0)
        {
            return new(false,
                $"Support action '{semanticActionId}' has no declared template tasks.",
                "support_select");
        }
        var friendCardRegion = friendPage
            ? RequireVisualRegion(pack, "career.entry.support.friend_card_list")
            : null;
        var rootTaskId = GetDeclaredTaskName(pack, "support_select", semanticActionId);

        return await _actions.RunAsync(
                connection,
                pack,
                "support_select",
                semanticActionId,
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    RoiOverrides = friendPage
                        ? templateTaskIds.ToDictionary(
                            taskId => taskId,
                            _ => GetRequiredRegionRoi(
                                pack, "career.entry.support.friend_card_list"),
                            StringComparer.OrdinalIgnoreCase)
                        : null,
                    ScaleCandidatesOverrides = friendPage
                        ? new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [rootTaskId] = GetRequiredRegionScaleCandidates(
                                friendCardRegion!, "scaleCandidates"),
                        }
                        : null,
                    TemplateOverrides = templateTaskIds.ToDictionary(
                        taskId => taskId,
                        _ => template,
                        StringComparer.OrdinalIgnoreCase),
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
                    connection, pack, "scenario_select", "scenario.next", logSink, cancellationToken)
                .ConfigureAwait(false);
        }

        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return new(false, "Could not capture the scenario selection screen.", "scenario_select");

        var resources = pack.VisualResources
            ?? throw new InvalidDataException("Career scenario selection visual resources are not loaded.");
        var template = await LoadTemplateCachedAsync(
                resources.ResolveVisualResource("career.entry.scenario.ura_card"),
                cancellationToken)
            .ConfigureAwait(false);
        var best = template is null
            ? null
            : TemplateMatcher.Find(
                frame,
                template,
                selection.Recognition.Roi,
                selection.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);

        var action = best is { Found: true } ? "scenario.next" : "scenario.next_card";
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
                || screen.ScreenId.Equals("scenario_select", StringComparison.OrdinalIgnoreCase)
                // After deleting an existing Career, the game returns to
                // Home before the next Career click opens Scenario Select.
                || screen.ScreenId.Equals("home", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Continue
                || !screen.ScreenId.Equals("home", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Trainee
                || screen.ScreenId.Equals("trainee_select", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Legacy
                || screen.ScreenId.Equals("legacy_select", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.Step != CareerEntryNavigationStep.Support
                || screen.ScreenId is "support_select"
                    or "support_autofill_confirmation"
                    or "support_ready"
                    or FinalConfirmationScreenId)
            .Where(screen => state.Step != CareerEntryNavigationStep.Career
                || screen.ScreenId is "career_main" or "career_races_ready")
            .Where(screen => state.Step == CareerEntryNavigationStep.Career
                || screen.ScreenId is not "career_main" and not "career_races_ready")
            .OrderBy(screen => GetPriority(screen.ScreenId))
            .ToArray();
        var earlyExitThreshold = RequireVisualRegion(
                pack,
                "career.entry.screen.early_exit")
            .Threshold
            ?? throw new InvalidDataException(
                "Career entry early-exit threshold is missing from the visual catalog.");

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
        var mainFrameCount = 0;
        foreach (var frame in frames)
        {
            EntryObservation? frameBest = null;
            foreach (var screen in candidates)
            {
                if (screen.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase))
                {
                    var mainMatch = await CareerMainScreenDetector.MatchAsync(
                            frame, pack, LoadTemplateCachedAsync, cancellationToken)
                        .ConfigureAwait(false);
                    if (mainMatch is not null)
                    {
                        frameBest = new(screen.ScreenId, mainMatch.Score);
                        break;
                    }
                    continue;
                }

                foreach (var templatePath in screen.Templates)
                {
                    var template = await LoadTemplateCachedAsync(
                            ResolveScreenTemplate(pack, screen, templatePath),
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
                    if (frameBest is { } early && early.Score >= earlyExitThreshold)
                        break;
                }
                if (frameBest is { } earlyFrame && earlyFrame.Score >= earlyExitThreshold)
                    break;
            }
            if (frameBest?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true)
                mainFrameCount++;
            if (frameBest is not null && (best is null || frameBest.Score > best.Score))
                best = frameBest;
        }
        if (best?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true
            && mainFrameCount != 2)
        {
            return null;
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

    private static string ResolveScreenTemplate(
        UraScenarioPack pack,
        UraScreenDefinition screen,
        string relativePath) =>
        pack.VisualResources?.ResolveScreenTemplate(screen, relativePath)
            ?? throw new InvalidDataException("Career visual resource package is not loaded.");

    private static CareerVisualRegionDefinition RequireVisualRegion(
        UraScenarioPack pack,
        string regionId)
    {
        if (pack.VisualResources?.TryGetRegion(regionId, out var region) == true
            && region is not null)
        {
            return region;
        }

        throw new InvalidDataException(
            $"Career visual catalog is missing region '{regionId}'.");
    }

    private static int[] GetRequiredRegionRoi(UraScenarioPack pack, string regionId) =>
        RequireVisualRegion(pack, regionId).Roi is { Length: >= 4 } roi
            ? (int[])roi.Clone()
            : throw new InvalidDataException(
                $"Career visual region '{regionId}' is missing its ROI.");

    private static (int X, int Y) GetRequiredRegionCenter(
        UraScenarioPack pack,
        string regionId)
    {
        var roi = GetRequiredRegionRoi(pack, regionId);
        return (roi[0] + roi[2] / 2, roi[1] + roi[3] / 2);
    }

    private static int GetRegionMetadataInt(
        CareerVisualRegionDefinition region,
        string name)
    {
        if (region.Metadata?.TryGetValue(name, out var value) == true
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result))
        {
            return result;
        }

        throw new InvalidDataException(
            $"Career visual region '{region.Id}' is missing integer '{name}' metadata.");
    }

    private static double[] GetRequiredRegionScaleCandidates(
        UraScenarioPack pack,
        string regionId,
        string metadataName) =>
        GetRequiredRegionScaleCandidates(RequireVisualRegion(pack, regionId), metadataName);

    private static double[] GetRequiredRegionScaleCandidates(
        CareerVisualRegionDefinition region,
        string metadataName)
    {
        if (region.Metadata?.TryGetValue(metadataName, out var value) != true
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Career visual region '{region.Id}' is missing '{metadataName}' metadata.");
        }

        var candidates = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Number)
            .Select(item => item.GetDouble())
            .ToArray();
        return candidates.Length > 0
            ? candidates
            : throw new InvalidDataException(
                $"Career visual region '{region.Id}' has empty '{metadataName}' metadata.");
    }

    private static string GetDeclaredTaskName(
        UraScenarioPack pack,
        string screenId,
        string semanticActionId)
    {
        var screen = pack.ScreenProfile.Find(screenId);
        var action = screen?.FindAction(semanticActionId);
        return !string.IsNullOrWhiteSpace(action?.Task)
            ? action.Task
            : throw new InvalidDataException(
                $"Screen '{screenId}' does not declare semantic action '{semanticActionId}'.");
    }

    private static List<string> GetActionTemplateTaskIds(
        UraScenarioPack pack,
        string screenId,
        string semanticActionId)
    {
        var root = GetDeclaredTaskName(pack, screenId, semanticActionId);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateTasks = new List<string>();
        pending.Push(root);
        while (pending.TryPop(out var taskName))
        {
            if (!visited.Add(taskName)
                || !pack.ExecutionDefinition.TryGetTask(taskName, out var task)
                || task is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(task.Template))
                templateTasks.Add(taskName);
            foreach (var next in task.Next
                         .Concat(task.OnErrorNext)
                         .Concat(task.ExceededNext)
                         .Concat(task.Sub)
                         .Concat(task.MonitorTasks)
                         .Concat(task.SuccessTasks)
                         .Concat(task.SuccessTask is null ? Array.Empty<string>() : [task.SuccessTask]))
            {
                if (!string.IsNullOrWhiteSpace(next))
                    pending.Push(next);
            }
        }

        return templateTasks;
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
        "career_main" => 9,
        "career_races_ready" => 10,
        _ => 20,
    };

    private static CareerEntryNavigationResult Failure(
        string message,
        CareerEntryNavigationState state) =>
        new(false, message, state.LastScreenId, state.ActionsCompleted);

    private sealed record EntryObservation(string ScreenId, double Score);
}
