using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Collects all presents from the game's mailbox and closes the result page.
/// The business sequence remains here; visual operations are delegated to the
/// shared runtime so other ordinary pipelines use the same implementation.
/// </summary>
public sealed class AdbMailCollectionPipeline : IMailCollectionPipeline
{
    private readonly IAdbRuntime _adbRuntime;
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbMailCollectionPipeline(
        IAdbRuntime adbRuntime,
        IVisualPipelineRuntime visualRuntime)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(visualRuntime);
        _adbRuntime = adbRuntime;
        _visualRuntime = visualRuntime;
    }

    public async Task<MailCollectionPipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
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
                return new MailCollectionPipelineResult(
                    false,
                    "An email collection run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            var definition = await HachimiPipelineDefinitionLoader.LoadAsync(
                    definitionPath,
                    linked.Token)
                .ConfigureAwait(false);
            if (definition is null)
                return Fail(logSink, "The email collection definition could not be loaded.");

            AddLog(logSink, "Mail Collection", "Starting email collection.");

            var homeMatch = await WaitForHomeAsync(
                    connection,
                    definition,
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            if (homeMatch is null)
                return Fail(logSink, "Timed out waiting for the game home tab.");

            await _visualRuntime.TapMatchAsync(
                    connection,
                    homeMatch,
                    "Game home",
                    linked.Token)
                .ConfigureAwait(false);
            AddLog(logSink, "Mail Collection", "Returned to the game home tab.", LogEntryKind.Success);
            await _visualRuntime.DelayAsync(
                    definition.Timing.NavigationMilliseconds,
                    linked.Token)
                .ConfigureAwait(false);

            await ClickTemplateTaskAsync(
                    connection,
                    definition,
                    definition.GetTask("GiftBox"),
                    "Gift box",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(
                    definition.Timing.MailboxLoadMilliseconds,
                    linked.Token)
                .ConfigureAwait(false);

            await ClickTemplateTaskAsync(
                    connection,
                    definition,
                    definition.GetTask("CollectAll"),
                    "Collect All",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(
                    definition.Timing.CollectionSettleMilliseconds,
                    linked.Token)
                .ConfigureAwait(false);

            await ClickTemplateTaskAsync(
                    connection,
                    definition,
                    definition.GetTask("Close"),
                    "Close",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(
                    definition.Timing.NavigationMilliseconds,
                    linked.Token)
                .ConfigureAwait(false);

            if (!await IsHomeVisibleAsync(
                    connection,
                    definition,
                    definition.Timing.HomeVerifyTimeoutMilliseconds,
                    linked.Token)
                .ConfigureAwait(false))
            {
                return Fail(logSink, "Mailbox was closed, but the game home tab was not detected.");
            }

            AddLog(logSink, "Mail Collection", "Email collection completed.", LogEntryKind.Success);
            return new MailCollectionPipelineResult(true, "Email collection completed.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Mail Collection", "Email collection was stopped.", LogEntryKind.Failure);
            return new MailCollectionPipelineResult(false, "Email collection was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, "Mail Collection", ex.Message, LogEntryKind.Failure);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        AddLog(logSink, "Mail Collection", "Stop requested.");
        return Task.FromResult(new MailCollectionPipelineResult(true, "Stop requested."));
    }

    private async Task<TemplateMatchResult?> WaitForHomeAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var home = definition.GetTask("Home");
        var match = await WaitForTaskAsync(
                connection,
                definition,
                home,
                definition.Timing.HomeTimeoutMilliseconds,
                "Game home",
                cancellationToken)
            .ConfigureAwait(false);
        if (match is not null)
            return match;

        // A previous task may have left a modal or a sub-page open. Back out
        // a few times, checking the real home-tab template after each step.
        for (var attempt = 0; attempt < definition.Timing.BackAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var back = await _adbRuntime.BackAsync(
                    connection.AdbPath,
                    connection.Serial,
                    cancellationToken)
                .ConfigureAwait(false);
            if (back.Error is not null || back.TimedOut || back.ExitCode != 0)
            {
                AddLog(logSink, "Mail Collection", $"ADB Back failed: {back.Stderr}");
                break;
            }

            await _visualRuntime.DelayAsync(
                    definition.Timing.BackSettleMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            match = await WaitForTaskAsync(
                    connection,
                    definition,
                    home,
                    definition.Timing.HomeRetryTimeoutMilliseconds,
                    "Game home",
                    cancellationToken)
                .ConfigureAwait(false);
            if (match is not null)
                return match;
        }

        return null;
    }

    private async Task<bool> IsHomeVisibleAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var match = await WaitForTaskAsync(
                connection,
                definition,
                definition.GetTask("Home"),
                timeoutMilliseconds,
                "Game home",
                cancellationToken)
            .ConfigureAwait(false);
        return match is not null;
    }

    private async Task<TemplateMatchResult?> WaitForTaskAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        HachimiPipelineTask task,
        int timeoutMilliseconds,
        string taskName,
        CancellationToken cancellationToken)
    {
        return await _visualRuntime.WaitForMatchAsync(
                connection,
                task.Template,
                task.Roi,
                task.TemplateThreshold,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                timeoutMilliseconds,
                task.PollIntervalMilliseconds > 0
                    ? task.PollIntervalMilliseconds
                    : definition.Timing.PollIntervalMilliseconds,
                taskName,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ClickTemplateTaskAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        HachimiPipelineTask task,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForTaskAsync(
                connection,
                definition,
                task,
                task.TimeoutMilliseconds,
                taskName,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for the '{taskName}' template (threshold {task.TemplateThreshold:0.000}).");
        }

        await _visualRuntime.TapMatchAsync(
                connection,
                match,
                taskName,
                cancellationToken)
            .ConfigureAwait(false);
        AddLog(
            logSink,
            "Mail Collection",
            $"Clicked {taskName} by template match at ({match.CenterX},{match.CenterY}), score {match.Score:0.000}.",
            LogEntryKind.Success);
    }

    private static MailCollectionPipelineResult Fail(
        IGrassTaskLogSink? logSink,
        string message)
    {
        AddLog(logSink, "Mail Collection", message, LogEntryKind.Failure);
        return new MailCollectionPipelineResult(false, message);
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string type,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add(type, details, kind);
}
