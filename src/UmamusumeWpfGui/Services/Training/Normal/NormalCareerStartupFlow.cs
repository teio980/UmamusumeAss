using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class NormalCareerStartupFlow
{
    private const string CareerFinalConfirmationScreenId = "career_final_confirmation";
    private readonly Func<
        LastVerifiedConnection,
        UraScenarioPack,
        string,
        string,
        IGrassTaskLogSink?,
        CancellationToken,
        Task<CareerTrainingResult?>> _runScreenAction;
    private readonly Func<
        LastVerifiedConnection,
        UraScenarioPack,
        UraCareerSessionState,
        bool,
        CancellationToken,
        Task<CareerObservation?>> _observe;
    private readonly Func<
        LastVerifiedConnection,
        UraScenarioPack,
        CancellationToken,
        Task<CareerStartupRecoveryTarget?>> _detectStartup;

    public NormalCareerStartupFlow(
        Func<
            LastVerifiedConnection,
            UraScenarioPack,
            string,
            string,
            IGrassTaskLogSink?,
            CancellationToken,
            Task<CareerTrainingResult?>> runScreenAction,
        Func<
            LastVerifiedConnection,
            UraScenarioPack,
            UraCareerSessionState,
            bool,
            CancellationToken,
            Task<CareerObservation?>> observe,
        Func<
            LastVerifiedConnection,
            UraScenarioPack,
            CancellationToken,
            Task<CareerStartupRecoveryTarget?>> detectStartup)
    {
        _runScreenAction = runScreenAction ?? throw new ArgumentNullException(nameof(runScreenAction));
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _detectStartup = detectStartup ?? throw new ArgumentNullException(nameof(detectStartup));
    }

    public async Task<CareerTrainingResult?> ConfigureAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerTrainingSettings settings,
        UraCareerSessionState state,
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
        }

        var startIssuedThisRun = false;
        if (state.NormalSetupStage == NormalCareerSetupStage.StartCareer)
        {
            state.NormalSetupStage = NormalCareerSetupStage.ConfirmStart;
            state.LastScreenId = "career_start_transition";

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
            if (!startIssuedThisRun)
            {
                var currentStartPage = await _detectStartup(
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

            var postStart = await _observe(
                    connection,
                    pack,
                    state,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (postStart?.ScreenId is "career_main" or "career_races_ready")
            {
                state.NormalSetupStage = NormalCareerSetupStage.AwaitCareerMain;
                state.LastScreenId = "career_start_transition";
                logSink?.Add(
                    "Career Training",
                    $"Normal Career start reached {postStart.ScreenId}; skipping opening setup.");
            }
            else
            {
                state.NormalSetupStage = NormalCareerSetupStage.SkipIntro;
                state.LastScreenId = "career_intro_event";
            }
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.SkipIntro)
        {
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
            state.LastScreenId = "career_start_transition";
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
        var result = await _runScreenAction(
                connection,
                pack,
                CareerFinalConfirmationScreenId,
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            logSink?.Add("Career Training", $"Normal setup action completed: {actionId}.");
        return result;
    }

    private async Task<CareerTrainingResult?> RunNormalQuickModeActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await _runScreenAction(
                connection,
                pack,
                "normal_quick_mode_settings",
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            logSink?.Add("Career Training", $"Normal Quick Mode action completed: {actionId}.");
        return result;
    }

    private static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);
}
