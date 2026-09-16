using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class AdbMissionCollectionPipeline : IMissionCollectionPipeline
{
    private readonly HachimiJsonPipelineRunner _runner;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbMissionCollectionPipeline(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<MissionCollectionPipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
            {
                return new MissionCollectionPipelineResult(
                    false,
                    "A mission collection run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            AddLog(logSink, "Starting mission collection.");
            var result = await _runner.RunAsync(
                    connection,
                    definitionPath,
                    "home",
                    logSink: logSink,
                    cancellationToken: linked.Token,
                    options: new HachimiPipelineRunOptions
                    {
                        TaskLogSink = taskLogSink,
                        SemanticProfile = HachimiTaskLogProfile.MissionCollection,
                    })
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                AddLog(logSink, "Mission collection completed.", LogEntryKind.Success);
                taskLogSink?.Add("Result", "Mission rewards collected.", HachimiTaskLogEventKind.Success);
                return new MissionCollectionPipelineResult(
                    true,
                    "Mission collection completed.");
            }

            AddLog(logSink, result.Message, LogEntryKind.Failure);
            return new MissionCollectionPipelineResult(false, result.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Mission collection was stopped.", LogEntryKind.Failure);
            taskLogSink?.Add("Result", "Mission collection was stopped.", HachimiTaskLogEventKind.Warning);
            return new MissionCollectionPipelineResult(false, "Mission collection was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, ex.Message, LogEntryKind.Failure);
            taskLogSink?.Add(
                "Result",
                HachimiTaskLogSemantics.ToUserFacingFailure(ex.Message),
                HachimiTaskLogEventKind.Failure);
            return new MissionCollectionPipelineResult(false, $"Mission collection failed: {ex.Message}");
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

    public Task<MissionCollectionPipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        AddLog(logSink, "Stop requested.");
        taskLogSink?.Add("Stop", "Stop requested.", HachimiTaskLogEventKind.Warning);
        return Task.FromResult(new MissionCollectionPipelineResult(true, "Stop requested."));
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add("Mission Collection", details, kind);
}
