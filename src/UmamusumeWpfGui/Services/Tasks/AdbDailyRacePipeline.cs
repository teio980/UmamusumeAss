using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class AdbDailyRacePipeline : IDailyRacePipeline
{
    private readonly HachimiJsonPipelineRunner _runner;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbDailyRacePipeline(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<DailyRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string mode,
        int raceCount,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
            {
                return new DailyRacePipelineResult(
                    false,
                    0,
                    "A Daily Race run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            var normalizedMode = DailyRaceTaskSettingsViewModel.NormalizeMode(mode);
            var requestedRaces = Math.Clamp(
                raceCount,
                DailyRaceTaskSettingsViewModel.MinimumRaceCount,
                DailyRaceTaskSettingsViewModel.MaximumRaceCount);
            var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["rewardRaceAgain"] = requestedRaces - 1,
            };

            if (string.Equals(
                    normalizedMode,
                    DailyRaceTaskSettingsViewModel.SupportPointMode,
                    StringComparison.OrdinalIgnoreCase))
            {
                // The JSON graph enters Monies first and uses exceededNext to
                // branch to Support Points when this task is skipped.
                overrides["moniesMode"] = 0;
            }

            AddLog(
                logSink,
                $"Starting {requestedRaces} Daily Race(s), mode {normalizedMode}.");
            var result = await _runner.RunAsync(
                    connection,
                    definitionPath,
                    "home",
                    new HachimiPipelineRunOptions
                    {
                        MaxTimesOverrides = overrides,
                    },
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                AddLog(
                    logSink,
                    $"Completed {result.CompletedUnits} Daily Race(s).",
                    LogEntryKind.Success);
                return new DailyRacePipelineResult(
                    true,
                    result.CompletedUnits,
                    $"Completed {result.CompletedUnits} Daily Race(s).");
            }

            AddLog(logSink, result.Message, LogEntryKind.Failure);
            return new DailyRacePipelineResult(false, result.CompletedUnits, result.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Daily Race was stopped.", LogEntryKind.Failure);
            return new DailyRacePipelineResult(false, 0, "Daily Race was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, ex.Message, LogEntryKind.Failure);
            return new DailyRacePipelineResult(false, 0, $"Daily Race failed: {ex.Message}");
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

    public Task<DailyRacePipelineResult> StopAsync(
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
        return Task.FromResult(new DailyRacePipelineResult(true, 0, "Stop requested."));
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add("Daily Race", details, kind);
}
