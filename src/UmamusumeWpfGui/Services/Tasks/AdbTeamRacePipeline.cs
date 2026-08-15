using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Team Race is executed by the shared JSON state-machine runner. The
/// adapter only supplies the requested repeat count and the task entry point;
/// all visual steps, delays and transitions live in team_race.json.
/// </summary>
public sealed class AdbTeamRacePipeline : ITeamRacePipeline
{
    private readonly HachimiJsonPipelineRunner _runner;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbTeamRacePipeline(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<TeamRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        int raceCount,
        bool stopWhenTicketsEmpty,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
                return new TeamRacePipelineResult(false, 0, "A Team Race run is already in progress.");

            _runCancellation = linked;
        }

        try
        {
            var requestedRaces = Math.Clamp(
                raceCount,
                TeamRaceTaskSettingsViewModel.MinimumRaceCount,
                TeamRaceTaskSettingsViewModel.MaximumRaceCount);

            // The optional ticket-stop switch is retained in the task-module
            // contract. If a future JSON definition adds a ticket condition,
            // the generic runner can consume it without restoring C# flow.
            _ = stopWhenTicketsEmpty;

            AddLog(logSink, $"Starting {requestedRaces} race(s).");
            var result = await _runner.RunAsync(
                    connection,
                    definitionPath,
                    "home",
                    new HachimiPipelineRunOptions
                    {
                        MaxTimesOverrides = new Dictionary<string, int>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["raceAdvance"] = requestedRaces - 1,
                        },
                    },
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                AddLog(
                    logSink,
                    $"Completed {result.CompletedUnits} race(s).",
                    LogEntryKind.Success);
                return new TeamRacePipelineResult(
                    true,
                    result.CompletedUnits,
                    $"Completed {result.CompletedUnits} Team Race race(s).");
            }

            AddLog(logSink, result.Message, LogEntryKind.Failure);
            return new TeamRacePipelineResult(false, result.CompletedUnits, result.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Team Race was stopped.", LogEntryKind.Failure);
            return new TeamRacePipelineResult(false, 0, "Team Race was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, ex.Message, LogEntryKind.Failure);
            return new TeamRacePipelineResult(false, 0, $"Team Race failed: {ex.Message}");
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

    public Task<TeamRacePipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        AddLog(logSink, "Stop requested.");
        return Task.FromResult(new TeamRacePipelineResult(true, 0, "Stop requested."));
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add("Team Race", details, kind);
}
