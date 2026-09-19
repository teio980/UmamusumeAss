using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

// This file contains only the existing Normal startup/setup sequence.  The
// action ids and their order intentionally stay in the same place conceptually
// as the original pipeline; this split is source organization, not a new flow.
public sealed partial class AdbNormalCareerTrainingPipeline
{
    private sealed record StartupPageStage(
        string RecognitionScreenId,
        string ResumeScreenId,
        NormalCareerSetupStage SetupStage,
        CareerEntryNavigationStep? EntryStep = null);

    private static readonly StartupPageStage[] StartupPageStages =
    [
        new(
            "normal_quick_mode_settings",
            "normal_quick_mode_settings",
            NormalCareerSetupStage.ConfigureQuickMode),
        new(
            "normal_scenario_select_startup",
            "scenario_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Scenario),
        new(
            "normal_trainee_select_startup",
            "trainee_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Trainee),
        new(
            "normal_legacy_select_startup",
            "legacy_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Legacy),
        new(
            "normal_support_select_startup",
            "support_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Support),
        new(
            "normal_final_confirmation_startup",
            "career_final_confirmation",
            NormalCareerSetupStage.ConfigureMode),
    ];

    private static void NormalizeSetupStage(UraCareerSessionState state)
    {
        if (state.CareerStarted)
        {
            state.NormalSetupStage = NormalCareerSetupStage.InCareer;
            return;
        }

        if (state.NormalSetupStage != NormalCareerSetupStage.EnterCareer)
            return;

        state.NormalSetupStage = state.LastScreenId.Trim().ToLowerInvariant() switch
        {
            CareerFinalConfirmationScreenId => NormalCareerSetupStage.ConfigureMode,
            CareerStartTransitionScreenId => NormalCareerSetupStage.AwaitCareerMain,
            "career_main" when state.TurnIndex > 0 => NormalCareerSetupStage.InCareer,
            _ => NormalCareerSetupStage.EnterCareer,
        };
    }

    private static bool IsPendingNormalSetupStage(NormalCareerSetupStage stage) => stage is
        NormalCareerSetupStage.ConfigureMode
            or NormalCareerSetupStage.ConfigureStrategy
            or NormalCareerSetupStage.StartCareer
            or NormalCareerSetupStage.ConfirmStart
            or NormalCareerSetupStage.SkipIntro
            or NormalCareerSetupStage.ConfigureQuickMode
            or NormalCareerSetupStage.SetQuickMode
            or NormalCareerSetupStage.ConfirmQuickMode;

    private async Task<StartupPageStage?> DetectNormalStartupPageAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CancellationToken cancellationToken)
    {
        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return null;

        foreach (var candidate in StartupPageStages)
        {
            var screen = pack.ScreenProfile.Find(candidate.RecognitionScreenId);
            if (screen is null || screen.Templates.Count != 1)
                continue;

            var template = await LoadTemplateCachedAsync(
                    ResolveCapture(pack, screen.Templates[0]),
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
            if (match.Found)
                return candidate;
        }

        return null;
    }

    private async Task<CareerTrainingResult?> ConfigureNormalCareerAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerTrainingSettings settings,
        UraCareerSessionState state,
        UraCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureMode)
        {
            var mode = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.select_mode",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (mode is not null)
                return mode;

            state.NormalSetupStage = NormalCareerSetupStage.ConfigureStrategy;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureStrategy)
        {
            if (!CareerStrategyCatalog.TryGetLineupStrategySemanticAction(
                    settings.LineupStrategy,
                    "normal",
                    out var strategyAction,
                    out _))
            {
                return Failure(
                    $"Normal Career lineup strategy '{settings.LineupStrategy}' is invalid.",
                    state.LastScreenId);
            }

            foreach (var action in new[]
            {
                CareerStrategyCatalog.StrategyChangeSemanticAction("normal"),
                strategyAction,
                CareerStrategyCatalog.StrategySaveSemanticAction("normal"),
                CareerStrategyCatalog.StrategyReturnSemanticAction("normal"),
            })
            {
                var result = await RunNormalSetupActionAsync(
                        connection,
                        pack,
                        action,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }

            state.NormalSetupStage = NormalCareerSetupStage.StartCareer;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        var startIssuedThisRun = false;
        if (state.NormalSetupStage == NormalCareerSetupStage.StartCareer)
        {
            // Persist the stage before the tap so an interrupted run can
            // inspect the actual page before deciding whether to tap Start.
            state.NormalSetupStage = NormalCareerSetupStage.ConfirmStart;
            state.LastScreenId = CareerStartTransitionScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            var start = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.start",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (start is not null)
                return start;
            startIssuedThisRun = true;
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfirmStart)
        {
            // ConfirmStart was persisted before the tap so an interrupted run
            // would not normally click Start twice.  Older checkpoints can,
            // however, reach this stage after the tap already succeeded.  If
            // the game is still on Final Confirmation, retry the pending tap;
            // otherwise treat the checkpoint as already inside the transition.
            if (!startIssuedThisRun)
            {
                var currentStartPage = await DetectNormalStartupPageAsync(
                        connection,
                        pack,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (currentStartPage?.RecognitionScreenId == "normal_final_confirmation_startup")
                {
                    logSink?.Add(
                        "Career Training",
                        "Final Confirmation is still visible; retrying the pending Normal Career start.");
                    var retryStart = await RunNormalSetupActionAsync(
                            connection,
                            pack,
                            "normal.start",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (retryStart is not null)
                        return retryStart;
                    startIssuedThisRun = true;
                }
            }

            // Normal Career does not show the support-deck confirmation used
            // by the Independent flow.  After the final Start tap the game
            // either opens the Tazuna intro or lands directly on a Career
            // screen.  The old code unconditionally clicked the Independent
            // confirmation template here, which made a valid start fail
            // after the game had already entered Career.
            var postStart = await ObserveAsync(
                    connection,
                    pack,
                    state,
                    careerStartTransitionExpected: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (postStart?.ScreenId is "career_main" or "career_races_ready")
            {
                state.NormalSetupStage = NormalCareerSetupStage.AwaitCareerMain;
                state.LastScreenId = CareerStartTransitionScreenId;
                logSink?.Add(
                    "Career Training",
                    $"Normal Career start reached {postStart.ScreenId}; skipping opening setup.");
            }
            else
            {
                state.NormalSetupStage = NormalCareerSetupStage.SkipIntro;
                state.LastScreenId = "career_intro_event";
            }
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.SkipIntro)
        {
            // The first post-start setup step is the skip button on the
            // opening Tazuna introduction. Persist this stage before the tap
            // so an interrupted run resumes here instead of replaying Start.
            var skipIntro = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.post_start.skip",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (skipIntro is not null)
                return skipIntro;

            state.NormalSetupStage = NormalCareerSetupStage.ConfigureQuickMode;
            state.LastScreenId = "normal_quick_mode_settings";
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureQuickMode)
        {
            var shortenEvents = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.shorten",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (shortenEvents is not null)
                return shortenEvents;

            state.NormalSetupStage = NormalCareerSetupStage.SetQuickMode;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.SetQuickMode)
        {
            var skipMode = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.skip",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (skipMode is not null)
                return skipMode;

            state.NormalSetupStage = NormalCareerSetupStage.ConfirmQuickMode;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfirmQuickMode)
        {
            var confirmQuickMode = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.confirm",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmQuickMode is not null)
                return confirmQuickMode;

            state.NormalSetupStage = NormalCareerSetupStage.AwaitCareerMain;
            state.LastScreenId = CareerStartTransitionScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<CareerTrainingResult?> RunNormalSetupActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await RunScreenActionAsync(
                connection,
                pack,
                CareerFinalConfirmationScreenId,
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            logSink?.Add("Career Training", $"Normal setup action completed: {actionId}.");
        }
        return result;
    }

    private async Task<CareerTrainingResult?> RunNormalQuickModeActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await RunScreenActionAsync(
                connection,
                pack,
                "normal_quick_mode_settings",
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            logSink?.Add("Career Training", $"Normal Quick Mode action completed: {actionId}.");
        }
        return result;
    }
}
