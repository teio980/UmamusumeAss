using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Mail collection is a JSON state graph. This adapter only supplies the
/// task entry point and exposes the UI module's start/stop contract.
/// </summary>
public sealed class AdbMailCollectionPipeline : IMailCollectionPipeline
{
    private readonly HachimiJsonPipelineRunner _runner;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbMailCollectionPipeline(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<MailCollectionPipelineResult> RunAsync(
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
                return new MailCollectionPipelineResult(
                    false,
                    "An email collection run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            AddLog(logSink, "Starting email collection.");
            var result = await _runner.RunAsync(
                    connection,
                    definitionPath,
                    "home",
                    logSink: logSink,
                    cancellationToken: linked.Token,
                    options: new HachimiPipelineRunOptions
                    {
                        TaskLogSink = taskLogSink,
                        SemanticProfile = HachimiTaskLogProfile.MailCollection,
                    })
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                AddLog(logSink, "Email collection completed.", LogEntryKind.Success);
                taskLogSink?.Add("Result", "Mailbox rewards collected.", HachimiTaskLogEventKind.Success);
                return new MailCollectionPipelineResult(
                    true,
                    "Email collection completed.");
            }

            AddLog(logSink, result.Message, LogEntryKind.Failure);
            return new MailCollectionPipelineResult(false, result.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Email collection was stopped.", LogEntryKind.Failure);
            taskLogSink?.Add("Result", "Mailbox collection was stopped.", HachimiTaskLogEventKind.Warning);
            return new MailCollectionPipelineResult(false, "Email collection was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, ex.Message, LogEntryKind.Failure);
            taskLogSink?.Add(
                "Result",
                HachimiTaskLogSemantics.ToUserFacingFailure(ex.Message),
                HachimiTaskLogEventKind.Failure);
            return new MailCollectionPipelineResult(false, $"Email collection failed: {ex.Message}");
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

    public Task<MailCollectionPipelineResult> StopAsync(
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
        return Task.FromResult(new MailCollectionPipelineResult(true, "Stop requested."));
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add("Mail Collection", details, kind);
}
