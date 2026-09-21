using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Handles the reusable checkpoint shared by Career race pages.
///
/// The runner label is deliberately its own screen marker. It lets a run
/// resume from the race-details page without coupling the first race-page
/// probe to a particular fan-race or URA objective.
/// </summary>
internal sealed class CareerRaceRunnerCheckpointHandler
{
    public const string ScreenId = "race_runner";

    private readonly ICareerFlowActionRunner _actions;

    public CareerRaceRunnerCheckpointHandler(ICareerFlowActionRunner actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(
                context.Observation.ScreenId,
                ScreenId,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        context.State.HasPendingRace = true;
        context.State.LastScreenId = ScreenId;

        if (context.State.RaceStrategyConfigured)
        {
            context.LogSink?.Add(
                "Career Training",
                "Race strategy is configured; running the Race path. View Results is temporarily disabled.",
                LogEntryKind.Info);

            var entryResult = await _actions.RunAsync(
                    context,
                    ScreenId,
                    "entry.start")
                .ConfigureAwait(false);
            if (entryResult is not null)
                return entryResult;

            context.LogSink?.Add(
                "Career Training",
                "Race path completed through the playback and result Next checkpoints.",
                LogEntryKind.Info);
            return CareerRuntimeResults.Failure(
                "Race entry started. Paused before the race/result flow is added.",
                ScreenId);
        }

        if (!CareerStrategyCatalog.TryGetLineupStrategyUiMapping(
                context.LineupStrategy,
                out var targetText))
        {
            return CareerRuntimeResults.Failure(
                $"Normal Career lineup strategy '{context.LineupStrategy}' is invalid.",
                ScreenId);
        }

        var strategyKey = context.LineupStrategy.Trim().ToLowerInvariant();
        var result = await _actions.RunAsync(
                context,
                ScreenId,
                $"strategy.apply.{strategyKey}")
            .ConfigureAwait(false);
        if (result is not null)
            return result;

        context.State.RaceStrategyConfigured = true;
        context.LogSink?.Add(
            "Career Training",
            $"Race runner page recognized; requested strategy '{targetText}' is configured.",
            LogEntryKind.Info);

        // Re-observe after the JSON task chain. The next visit to this same
        // checkpoint pauses safely until the next reusable race step is added.
        return null;
    }
}
