using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

public enum GrassTaskQueueEventKind
{
    Started,
    Completed,
    Failed,
    Canceled,
    Skipped,
}

public enum GrassTaskQueueStopReason
{
    None,
    Canceled,
    DeviceDisconnected,
}

/// <summary>A settings snapshot captured before Core receives the queue.</summary>
public sealed record GrassTaskRunRequest(string TaskId, string SettingsJson);

public sealed record GrassTaskQueueEvent(
    int TaskIndex,
    GrassTaskQueueEventKind Kind,
    GrassTaskExecutionResult? Result = null,
    string? Message = null,
    GrassTaskExecutionProgress? Progress = null);

public sealed record GrassTaskQueueRunResult(
    bool Succeeded,
    GrassTaskQueueStopReason StopReason);

public interface IGrassTaskExecutor
{
    IReadOnlyList<GrassTaskRunRequest> Capture(IReadOnlyList<IGrassTaskModule> modules);
}

public interface IGrassTaskExecutionService
{
    IReadOnlyList<GrassTaskRunRequest> Capture(IReadOnlyList<IGrassTaskModule> modules);

    Task<GrassTaskQueueRunResult> RunAsync(
        IReadOnlyList<GrassTaskRunRequest> requests,
        Func<int, GrassTaskExecutionContext> contextFactory,
        Action<GrassTaskQueueEvent> report,
        CancellationToken cancellationToken = default);

    Task<string> GetRunSnapshotJsonAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("{}");
}

/// <summary>Submits the entire immutable queue to Core and only mirrors its events to the UI.</summary>
public sealed class GrassTaskExecutionService(
    IGrassTaskExecutor? executor = null,
    IConnectionStateService? connectionState = null,
    ILocalizationService? localizationService = null) : IGrassTaskExecutionService
{
    private readonly IGrassTaskExecutor? _executor = executor;
    private readonly IConnectionStateService? _connectionState = connectionState;
    private readonly ILocalizationService? _localizationService = localizationService;
    private readonly IUmaService? _umaService = (executor as NativeGrassTaskExecutor)?.UmaService;
    private int _running;

    public Task<string> GetRunSnapshotJsonAsync(CancellationToken cancellationToken = default) =>
        _umaService?.GetRunSnapshotJsonAsync(cancellationToken) ?? Task.FromResult("{}");

    public IReadOnlyList<GrassTaskRunRequest> Capture(IReadOnlyList<IGrassTaskModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (_executor is not null)
            return _executor.Capture(modules);
        return modules.Select(module => new GrassTaskRunRequest(
            module.Definition.Id, module.ExportSettings().ToJsonString())).ToArray();
    }

    public async Task<GrassTaskQueueRunResult> RunAsync(
        IReadOnlyList<GrassTaskRunRequest> requests,
        Func<int, GrassTaskExecutionContext> contextFactory,
        Action<GrassTaskQueueEvent> report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(report);
        if (requests.Count == 0)
            return new(true, GrassTaskQueueStopReason.None);
        var umaService = _umaService
            ?? throw new NotSupportedException("Task execution requires the native Core service.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A task queue is already running.");

        var terminalIndices = new HashSet<int>();
        var taskIndexesByInstance = new Dictionary<ulong, int>();
        var taskIndexesByDefinition = requests
            .Select((request, index) => (request.TaskId, index))
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray(), StringComparer.Ordinal);
        ulong? runOperationId = null;
        ulong? currentTaskInstanceId = null;
        var deviceLost = 0;
        var nativeDeviceFailure = 0;

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionToken = executionCancellation.Token;
        if (_connectionState is not null)
        {
            _connectionState.StateChanged += OnConnectionChanged;
            OnConnectionChanged(null, EventArgs.Empty);
        }

        void OnConnectionChanged(object? sender, EventArgs args)
        {
            if (_connectionState?.State == ConnectionState.Connected)
                return;
            Interlocked.Exchange(ref deviceLost, 1);
            try { executionCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        bool AcceptOperation(ulong operationId, bool establish = false)
        {
            if (runOperationId is null)
            {
                if (!establish)
                    return false;
                runOperationId = operationId;
            }
            return runOperationId == operationId;
        }

        bool TryGetTaskIndexByInstance(ulong taskInstanceId, ulong operationId, out int taskIndex)
        {
            taskIndex = -1;
            return AcceptOperation(operationId)
                && taskInstanceId != 0
                && taskIndexesByInstance.TryGetValue(taskInstanceId, out taskIndex)
                && taskIndex >= 0
                && taskIndex < requests.Count;
        }

        bool TryGetTaskIndexByDefinition(string taskDefinitionId, ulong operationId, out int taskIndex)
        {
            taskIndex = -1;
            if (!AcceptOperation(operationId)
                || !taskIndexesByDefinition.TryGetValue(taskDefinitionId, out var candidates))
                return false;

            var runningCandidates = candidates.Where(index => !terminalIndices.Contains(index)).ToArray();
            if (runningCandidates.Length != 1)
                return false;
            taskIndex = runningCandidates[0];
            return true;
        }

        void OnCoreEvent(ConnectionEvent connectionEvent)
        {
            switch (connectionEvent)
            {
                case TaskStartedEvent started when started.Index >= 0 && started.Index < requests.Count:
                    if (!AcceptOperation(started.OperationId, establish: true))
                        break;
                    taskIndexesByInstance[started.TaskId] = started.Index;
                    currentTaskInstanceId = started.TaskId;
                    report(new(started.Index, GrassTaskQueueEventKind.Started));
                    break;

                case GameTaskProgressEvent gameProgress:
                    if (TryGetTaskIndexByInstance(gameProgress.TaskInstanceId, gameProgress.OperationId, out var gameIndex))
                        MirrorGameTaskProgress(gameIndex, gameProgress);
                    else if (gameProgress.TaskInstanceId == 0
                        && TryGetTaskIndexByDefinition(gameProgress.TaskId, gameProgress.OperationId, out gameIndex))
                        MirrorGameTaskProgress(gameIndex, gameProgress);
                    break;

                case TaskProgressEvent progress:
                    if (TryGetTaskIndexByInstance(progress.TaskInstanceId, progress.OperationId, out var progressIndex))
                    {
                        var context = contextFactory(progressIndex);
                        context.ReportProgress?.Invoke(new(
                            progressIndex + 1,
                            requests.Count,
                            requests[progressIndex].TaskId,
                            progress.OperationId,
                            progress.StepIndex,
                            progress.TaskName));
                        context.TaskLogSink?.Add(
                            "Action",
                            progress.Message,
                            progress.Succeeded ? HachimiTaskLogEventKind.Info : HachimiTaskLogEventKind.Warning);
                    }
                    break;

                case TaskDecisionEvent decision when TryGetTaskIndexByInstance(decision.TaskId, decision.OperationId, out var decisionIndex):
                    contextFactory(decisionIndex).TaskLogSink?.Add(
                        "Decision", decision.Details, HachimiTaskLogEventKind.Info);
                    break;

                case TaskActionConfirmedEvent confirmed when TryGetTaskIndexByInstance(confirmed.TaskId, confirmed.OperationId, out var confirmedIndex):
                    contextFactory(confirmedIndex).TaskLogSink?.Add(
                        "Action",
                        confirmed.Confirmed ? $"{confirmed.ActionName} confirmed." : $"{confirmed.ActionName} was not confirmed.",
                        confirmed.Confirmed ? HachimiTaskLogEventKind.Success : HachimiTaskLogEventKind.Failure);
                    break;

                case RuntimeDiagnosticEvent diagnostic when AcceptOperation(diagnostic.OperationId):
                    if (TryResolveCurrentTaskIndex(out var diagnosticIndex))
                        contextFactory(diagnosticIndex).LogSink?.Add(
                            diagnostic.Level.ToString(), diagnostic.Message,
                            diagnostic.Level == RuntimeDiagnosticLevel.Error ? LogEntryKind.Failure : LogEntryKind.Info);
                    break;

                case TaskCompletedEvent completed when TryGetTaskIndexByInstance(completed.TaskId, completed.OperationId, out var completedIndex):
                    terminalIndices.Add(completedIndex);
                    report(new(completedIndex, GrassTaskQueueEventKind.Completed,
                        new(true, false, completed.Message)));
                    break;

                case TaskFailedEvent failed when TryGetTaskIndexByInstance(failed.TaskId, failed.OperationId, out var failedIndex):
                    terminalIndices.Add(failedIndex);
                    var disconnected = IsDeviceError(failed.ErrorCode)
                        || Volatile.Read(ref deviceLost) != 0;
                    if (IsDeviceError(failed.ErrorCode))
                        Interlocked.Exchange(ref nativeDeviceFailure, 1);
                    var canceled = failed.ErrorCode == ConnectionErrorCode.Canceled && !disconnected;
                    report(new(failedIndex,
                        disconnected ? GrassTaskQueueEventKind.Failed
                            : canceled ? GrassTaskQueueEventKind.Canceled : GrassTaskQueueEventKind.Failed,
                        new(false, canceled, failed.Message)
                        {
                            ErrorCode = disconnected && failed.ErrorCode == ConnectionErrorCode.Canceled
                                ? ConnectionErrorCode.DeviceDisconnected
                                : failed.ErrorCode,
                        }));
                    break;
            }
        }

        void MirrorGameTaskProgress(int taskIndex, GameTaskProgressEvent progress)
        {
            var context = contextFactory(taskIndex);
            context.ReportProgress?.Invoke(new(
                progress.CompletedUnits,
                progress.TotalUnits,
                requests[taskIndex].TaskId,
                progress.OperationId,
                progress.StepIndex,
                progress.TaskName));

            var profile = GetLogProfile(requests[taskIndex].TaskId);
            if (profile == HachimiTaskLogProfile.None
                || !HachimiTaskLogSemantics.TryDescribe(profile, progress.TaskName, out var semantic))
            {
                if (progress.EventType is GameTaskEventType.ActionExecuted or GameTaskEventType.TaskFailed)
                    context.TaskLogSink?.Add(
                        "Action", progress.Message,
                        progress.Succeeded ? HachimiTaskLogEventKind.Info : HachimiTaskLogEventKind.Warning);
                return;
            }

            semantic = HachimiTaskLogSemantics.Localize(semantic, _localizationService);
            switch (progress.EventType)
            {
                case GameTaskEventType.TaskEntering:
                    context.TaskLogSink?.Add("Step", semantic.StartMessage, HachimiTaskLogEventKind.Info);
                    break;
                case GameTaskEventType.ActionExecuting when semantic.SentMessage is not null:
                    context.TaskLogSink?.Add("Action", semantic.SentMessage, HachimiTaskLogEventKind.Action);
                    break;
                case GameTaskEventType.TaskCompleted:
                    context.TaskLogSink?.Add("Step", semantic.CompletedMessage, HachimiTaskLogEventKind.Success);
                    break;
                case GameTaskEventType.TaskFailed:
                    context.TaskLogSink?.Add(
                        "Step",
                        semantic.FailedMessage,
                        HachimiTaskLogEventKind.Failure);
                    break;
            }
        }

        bool TryResolveCurrentTaskIndex(out int index)
        {
            index = -1;
            return currentTaskInstanceId is { } taskId
                && taskIndexesByInstance.TryGetValue(taskId, out index);
        }

        try
        {
            umaService.ConnectionEventReceived += OnCoreEvent;
            var queueResult = await umaService.RunQueueAsync(
                    requests.Select(request => new QueueTaskRequest(request.TaskId, request.SettingsJson)).ToArray(),
                    executionToken)
                .ConfigureAwait(false);

            var latestSnapshot = await umaService.GetRunSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = runOperationId is not null && latestSnapshot.OperationId == runOperationId
                ? latestSnapshot
                : null;
            var stopReason = Volatile.Read(ref deviceLost) != 0
                || Volatile.Read(ref nativeDeviceFailure) != 0
                || IsDeviceError(queueResult.ErrorCode)
                ? GrassTaskQueueStopReason.DeviceDisconnected
                : queueResult.ErrorCode == ConnectionErrorCode.Canceled
                    || snapshot?.State == QueueRunState.Canceled
                    ? GrassTaskQueueStopReason.Canceled
                    : GrassTaskQueueStopReason.None;
            if (snapshot is not null)
                ReconcileSnapshot(snapshot, stopReason);

            if (stopReason != GrassTaskQueueStopReason.None)
            {
                var currentIndex = ResolveCurrentIndex(snapshot);
                if (currentIndex >= 0 && !terminalIndices.Contains(currentIndex))
                {
                    terminalIndices.Add(currentIndex);
                    if (stopReason == GrassTaskQueueStopReason.DeviceDisconnected)
                    {
                        report(new(currentIndex, GrassTaskQueueEventKind.Failed,
                            new(false, false, queueResult.Message)
                            {
                                ErrorCode = queueResult.ErrorCode == ConnectionErrorCode.Success
                                    ? ConnectionErrorCode.DeviceDisconnected : queueResult.ErrorCode,
                            }));
                    }
                    else
                    {
                        report(new(currentIndex, GrassTaskQueueEventKind.Canceled,
                            new(false, true, queueResult.Message) { ErrorCode = ConnectionErrorCode.Canceled }));
                    }
                }

                var skipFrom = Math.Max(0, currentIndex + 1);
                for (var index = skipFrom; index < requests.Count; index++)
                {
                    if (!terminalIndices.Add(index)) continue;
                    report(new(index, GrassTaskQueueEventKind.Skipped,
                        Message: stopReason == GrassTaskQueueStopReason.Canceled ? "Canceled" : "DeviceDisconnected"));
                }
            }

            return new(
                queueResult.Succeeded
                    && queueResult.FailedTasks == 0
                    && queueResult.SkippedTasks == 0,
                stopReason);

            void ReconcileSnapshot(QueueRunSnapshot finalSnapshot, GrassTaskQueueStopReason effectiveStopReason)
            {
                var count = Math.Min(requests.Count, finalSnapshot.Tasks.Count);
                for (var index = 0; index < count; index++)
                {
                    taskIndexesByInstance[finalSnapshot.Tasks[index].Id] = index;
                    if (terminalIndices.Contains(index)) continue;
                    var task = finalSnapshot.Tasks[index];
                    switch (task.Status)
                    {
                        case QueueTaskStatus.Succeeded:
                            terminalIndices.Add(index);
                            report(new(index, GrassTaskQueueEventKind.Completed,
                                new(true, false, task.Message), Progress: new(
                                    task.CompletedUnits, task.TotalUnits, requests[index].TaskId)));
                            break;
                        case QueueTaskStatus.Failed:
                            terminalIndices.Add(index);
                            var errorCode = Enum.IsDefined(typeof(ConnectionErrorCode), task.ErrorCode)
                                ? (ConnectionErrorCode)task.ErrorCode : ConnectionErrorCode.CommandFailed;
                            var isCurrentTask = finalSnapshot.CurrentTaskId == task.Id;
                            var disconnected = IsDeviceError(errorCode)
                                || effectiveStopReason == GrassTaskQueueStopReason.DeviceDisconnected && isCurrentTask;
                            var canceled = !disconnected
                                && (errorCode == ConnectionErrorCode.Canceled
                                    || effectiveStopReason == GrassTaskQueueStopReason.Canceled && isCurrentTask);
                            if (disconnected && errorCode == ConnectionErrorCode.Canceled)
                                errorCode = ConnectionErrorCode.DeviceDisconnected;
                            report(new(index,
                                disconnected ? GrassTaskQueueEventKind.Failed
                                    : canceled ? GrassTaskQueueEventKind.Canceled : GrassTaskQueueEventKind.Failed,
                                new(false, canceled, task.Message) { ErrorCode = errorCode }));
                            break;
                        case QueueTaskStatus.Skipped:
                            terminalIndices.Add(index);
                            report(new(index, GrassTaskQueueEventKind.Skipped,
                                Message: effectiveStopReason == GrassTaskQueueStopReason.Canceled
                                    || effectiveStopReason == GrassTaskQueueStopReason.None
                                        && finalSnapshot.State == QueueRunState.Canceled
                                    ? "Canceled" : "DeviceDisconnected"));
                            break;
                    }
                }
            }

            int ResolveCurrentIndex(QueueRunSnapshot? finalSnapshot)
            {
                if (finalSnapshot?.CurrentTaskId is { } snapshotTaskId)
                {
                    var snapshotIndex = finalSnapshot.Tasks.ToList().FindIndex(task => task.Id == snapshotTaskId);
                    if (snapshotIndex >= 0)
                        return snapshotIndex;
                }
                if (currentTaskInstanceId is { } activeTaskId
                    && taskIndexesByInstance.TryGetValue(activeTaskId, out var activeIndex))
                    return activeIndex;
                return -1;
            }
        }
        finally
        {
            umaService.ConnectionEventReceived -= OnCoreEvent;
            if (_connectionState is not null)
                _connectionState.StateChanged -= OnConnectionChanged;
            Volatile.Write(ref _running, 0);
        }
    }

    private static HachimiTaskLogProfile GetLogProfile(string taskId) => taskId switch
    {
        "team-race" => HachimiTaskLogProfile.TeamRace,
        "daily-race" => HachimiTaskLogProfile.DailyRace,
        "mail-collection" => HachimiTaskLogProfile.MailCollection,
        "mission-collection" => HachimiTaskLogProfile.MissionCollection,
        "shop-purchase" => HachimiTaskLogProfile.Shop,
        "career-training" => HachimiTaskLogProfile.Career,
        _ => HachimiTaskLogProfile.None,
    };

    private static bool IsDeviceError(ConnectionErrorCode errorCode) => errorCode is
        ConnectionErrorCode.DeviceUnauthorized or ConnectionErrorCode.DeviceDisconnected
        or ConnectionErrorCode.DeviceUnavailable or ConnectionErrorCode.DeviceOffline;
}
