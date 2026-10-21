namespace Umamusume.CoreBridge;

public interface IUmaService : IAsyncDisposable
{
    string? CoreVersion { get; }

    /// <summary>Current native session identity, even when the consumer subscribed after connection.</summary>
    DeviceSessionIdentity? ActiveDeviceIdentity => null;

    string? ResourcePath => null;
    event Action<ConnectionEvent>? ConnectionEventReceived;
    event Action<BridgeDiagnostic>? DiagnosticReceived;
    Task InitializeAsync(
        string appBaseDir,
        string appDataDir,
        CancellationToken cancellationToken = default);
    Task ReloadResourceAsync(
        string compositeBaseDirectory,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetInstanceOptionsAsync(
        DeviceInstanceOptions options,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Device instance options are not supported by this service."));
    Task<ConnectionTerminalEvent> ConnectAsync(
        string adbPath,
        string serial,
        string profile,
        CancellationToken cancellationToken = default);
    Task<DeviceOperationEvent> VerifyGameAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Device operations are not supported by this service."));
    Task<DeviceOperationEvent> CaptureAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Device operations are not supported by this service."));
    Task<DeviceOperationEvent> TapAsync(
        DeviceFrame frame,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Device operations are not supported by this service."));
    Task<DeviceOperationEvent> SwipeAsync(
        DeviceFrame frame,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs,
        bool extraSwipe = false,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Device operations are not supported by this service."));
    Task<DeviceOperationEvent> TouchDownAsync(
        DeviceFrame frame,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Held touch is not supported by this service."));
    Task<DeviceOperationEvent> PrepareTouchAsync(
        DeviceFrame frame,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Held touch preparation is not supported by this service."));
    Task<DeviceOperationEvent> KeyEventAsync(
        DeviceFrame frame,
        string keyCode,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Key events are not supported by this service."));
    Task<DeviceOperationEvent> InputTextAsync(
        DeviceFrame frame,
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Text input is not supported by this service."));
    Task<DeviceOperationEvent> PackageOperationAsync(
        string packageId,
        string? activity,
        PackageOperationKind operation,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Package operations are not supported by this service."));
    Task<DeviceOperationEvent> TouchUpAsync(
        DeviceFrame sourceFrame,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Held touch is not supported by this service."));
    Task<DeviceOperationEvent> TouchCancelAsync(
        DeviceFrame sourceFrame,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DeviceOperationEvent>(new NotSupportedException("Held touch is not supported by this service."));
    RecognitionImage? LoadTemplateImage(string path, TemplatePreparation preparation = TemplatePreparation.Original) =>
        throw new NotSupportedException("Native template resources are unavailable.");
    TemplateMatchResult MatchTemplate(
        RecognitionImage screen,
        RecognitionImage templateImage,
        TemplateMatchOptions options) =>
        throw new NotSupportedException("Native template matching is not supported by this service.");
    TextRecognitionResult RecognizeText(RecognitionImage image, string language) =>
        throw new NotSupportedException("Native text recognition is not supported by this service.");
    bool HasMeaningfulChange(
        RecognitionImage before,
        RecognitionImage after,
        TemplateMatchResult match) =>
        throw new NotSupportedException("Native image comparison is not supported by this service.");
    bool HasPersistentChange(
        RecognitionImage before,
        RecognitionImage firstAfter,
        RecognitionImage settledAfter,
        TemplateMatchResult match) =>
        throw new NotSupportedException("Native image comparison is not supported by this service.");
    string? ValidatePipelineJson(string json) => null;
    Task<PipelineRunResult> RunPipelineAsync(string pipelineJson, string? entryTask = null, CancellationToken cancellationToken = default) =>
        Task.FromException<PipelineRunResult>(new NotSupportedException("Native pipeline running is not supported by this service."));
    Task<GameTaskRunResult> RunGameTaskAsync(
        string taskId,
        string settingsJson,
        Action<GameTaskProgressEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GameTaskRunResult>(new NotSupportedException("Native game-task execution is not supported by this service."));
    Task<ulong> AppendTaskAsync(
        string taskId,
        string settingsJson,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ulong>(new NotSupportedException("Task queue is not supported by this service."));
    Task<QueueRunResult> RunQueueAsync(
        IReadOnlyList<QueueTaskRequest> tasks,
        CancellationToken cancellationToken = default) =>
        Task.FromException<QueueRunResult>(new NotSupportedException("Task queue is not supported by this service."));
    Task SetTaskParamsAsync(
        ulong taskId,
        string settingsJson,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Task queue is not supported by this service."));
    Task ClearTasksAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Task queue is not supported by this service."));
    Task<QueueRunResult> StartQueueAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<QueueRunResult>(new NotSupportedException("Task queue is not supported by this service."));
    Task<string> GetRunSnapshotJsonAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException("Run snapshot is not supported by this service."));
    Task<QueueRunSnapshot> GetRunSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<QueueRunSnapshot>(new NotSupportedException("Run snapshot is not supported by this service."));
    Task<string> GetCatalogJsonAsync(
        string catalogType = "all",
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException("Catalog query is not supported by this service."));
    Task<CoreCatalog> GetCatalogAsync(
        string catalogType = "all",
        CancellationToken cancellationToken = default) =>
        Task.FromException<CoreCatalog>(new NotSupportedException("Catalog query is not supported by this service."));
    Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default);

    /// <summary>Disposes within the remaining application shutdown budget when supported.</summary>
    ValueTask DisposeAsync(TimeSpan shutdownTimeout) => DisposeAsync();
}
