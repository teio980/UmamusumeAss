using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class AdbDailyRacePipeline : IDailyRacePipeline
{
    private readonly HachimiJsonPipelineRunner _runner;
    private readonly DailyRaceRunnerSelector _runnerSelector;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbDailyRacePipeline(
        HachimiJsonPipelineRunner runner,
        DailyRaceRunnerSelector runnerSelector)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(runnerSelector);
        _runner = runner;
        _runnerSelector = runnerSelector;
    }

    public Task<DailyRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string mode,
        string difficulty,
        int raceCount,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default) =>
        RunWithTraineeAsync(
            connection,
            definitionPath,
            mode,
            difficulty,
            raceCount,
            null,
            logSink,
            cancellationToken);

    public async Task<DailyRacePipelineResult> RunWithTraineeAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string mode,
        string difficulty,
        int raceCount,
        int? traineeId = null,
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
            var normalizedDifficulty = DailyRaceTaskSettingsViewModel.NormalizeDifficulty(difficulty);
            var requestedRaces = Math.Clamp(
                raceCount,
                DailyRaceTaskSettingsViewModel.MinimumRaceCount,
                DailyRaceTaskSettingsViewModel.MaximumRaceCount);
            var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["multiRaceModeGate"] = requestedRaces - 1,
                ["multiRaceTicketGate"] = requestedRaces - 1,
                ["multiRaceTicketPlus"] = requestedRaces - 1,
                ["moniesDifficultyScroll"] = string.Equals(
                    normalizedDifficulty,
                    DailyRaceTaskSettingsViewModel.EasyDifficulty,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                ["supportPointDifficultyScroll"] = string.Equals(
                    normalizedDifficulty,
                    DailyRaceTaskSettingsViewModel.EasyDifficulty,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0,
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
                        RoiOverrides = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["moniesDifficulty"] = GetDifficultyRoi(normalizedDifficulty),
                            ["supportPointDifficulty"] = GetDifficultyRoi(normalizedDifficulty),
                        },
                        CustomActionExecutor = async (
                                actionConnection,
                                definition,
                                taskName,
                                task,
                                actionLogSink,
                                actionCancellationToken) =>
                            await _runnerSelector.SelectAsync(
                                    actionConnection,
                                    definition,
                                    taskName,
                                    task,
                                    traineeId,
                                    actionLogSink,
                                    actionCancellationToken)
                                .ConfigureAwait(false),
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

    private static int[] GetDifficultyRoi(string difficulty) =>
        DailyRaceTaskSettingsViewModel.NormalizeDifficulty(difficulty) switch
        {
            DailyRaceTaskSettingsViewModel.HardDifficulty => [520, 975, 360, 145],
            DailyRaceTaskSettingsViewModel.NormalDifficulty => [520, 1145, 360, 145],
            DailyRaceTaskSettingsViewModel.EasyDifficulty => [520, 1060, 360, 170],
            _ => [520, 800, 360, 145],
        };
}
