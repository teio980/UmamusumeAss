using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Umamusume.CoreBridge;

public sealed class UmaService : IUmaService
{
    private const uint SupportedCoreApiVersion = 11;
    private const int MaxFramePngBytes = 64 * 1024 * 1024;
    private const int MaxNativeJsonBytes = 32 * 1024 * 1024;
    private const int NativeJsonReadAttempts = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _lifecycleLock = new();
    private readonly object _operationLock = new();
    private readonly object _frameLock = new();
    private readonly object _queueControlLock = new();
    private readonly SemaphoreSlim _queueMutationLock = new(1, 1);
    private readonly Dictionary<ulong, DeviceFrame> _frames = [];
    private readonly IUmaNativeApi _native;
    private readonly IEventDispatcher _dispatcher;
    private readonly UmaApiCallback _nativeCallback;
    private TimeSpan _shutdownTimeout;
    private readonly TimeProvider _timeProvider;
    private SafeUmaHandle? _handle;
    private OperationState? _startingOperation;
    private OperationState? _activeOperation;
    private ulong? _sessionGeneration;
    private DeviceSessionIdentity? _activeDeviceIdentity;
    private bool _initializing;
    private bool _disposing;
    private bool _disposed;
    private int _activeRecognitionCount;
    private TaskCompletionSource? _recognitionsDrained;
    private Task? _disposeTask;
    private DeviceInstanceOptions _deviceOptions = new();

    public UmaService(IEventDispatcher dispatcher)
        : this(new UmaCoreBridgeNative(), dispatcher, TimeSpan.FromSeconds(10), TimeProvider.System)
    {
    }

    internal UmaService(IUmaNativeApi native, IEventDispatcher dispatcher)
        : this(native, dispatcher, TimeSpan.FromSeconds(10), TimeProvider.System)
    {
    }

    internal UmaService(
        IUmaNativeApi native,
        IEventDispatcher dispatcher,
        TimeSpan shutdownTimeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(shutdownTimeout, TimeSpan.Zero);
        _native = native;
        _dispatcher = dispatcher;
        _shutdownTimeout = shutdownTimeout;
        _timeProvider = timeProvider;
        _nativeCallback = OnNativeCallback;
    }

    public string? CoreVersion { get; private set; }
    public uint? CoreApiVersion { get; private set; }
    public string? ResourcePath { get; private set; }

    public DeviceSessionIdentity? ActiveDeviceIdentity
    {
        get
        {
            lock (_operationLock)
            {
                return _activeDeviceIdentity;
            }
        }
    }

    public event Action<ConnectionEvent>? ConnectionEventReceived;
    public event Action<BridgeDiagnostic>? DiagnosticReceived;

    public Task InitializeAsync(
        string appBaseDir,
        string appDataDir,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalBaseDir = ValidateBaseDirectory(appBaseDir);
        string canonicalAppDataDir = ValidateAppDataDirectory(appDataDir);

        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_initializing || _handle is not null)
            {
                throw new InvalidOperationException("The native bridge is already initialized.");
            }

            _initializing = true;
        }

        SafeUmaHandle? createdHandle = null;
        bool handleAssigned = false;
        try
        {
            uint apiVersion = _native.GetApiVersion();
            if (apiVersion is not (8 or 9 or 10 or 11))
            {
                throw BridgeFailure($"Unsupported native core API version {apiVersion}; expected API 8, 9, 10, or {SupportedCoreApiVersion}.");
            }

            Directory.CreateDirectory(canonicalAppDataDir);
            RequireSuccess(_native.SetUserDir(canonicalAppDataDir), "UmaSetUserDir");
            RequireSuccess(_native.LoadResource(canonicalBaseDir), "UmaLoadResource");

            string version = _native.GetVersion();
            if (string.IsNullOrWhiteSpace(version))
            {
                throw BridgeFailure("The native core returned an empty version.");
            }

            createdHandle = _native.Create(_nativeCallback, IntPtr.Zero);
            if (createdHandle.IsInvalid)
            {
                createdHandle.Dispose();
                createdHandle = null;
                throw BridgeFailure("UmaCreate returned an invalid handle.");
            }

            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                _handle = createdHandle;
                CoreVersion = version;
                CoreApiVersion = apiVersion;
                ResourcePath = Path.Combine(canonicalBaseDir, "resource");
                handleAssigned = true;
            }

            return Task.CompletedTask;
        }
        finally
        {
            if (!handleAssigned)
            {
                createdHandle?.Dispose();
            }

            lock (_lifecycleLock)
            {
                _initializing = false;
            }
        }
    }

    public Task<ConnectionTerminalEvent> ConnectAsync(
        string adbPath,
        string serial,
        string profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        string normalizedAdbPath = Path.GetFullPath(adbPath);
        return StartOperation(
            OperationKind.Connection,
            handle => _native.Connect(handle, adbPath, serial, profile),
            cancellationToken,
            normalizedAdbPath,
            serial);
    }

    public Task<DeviceOperationEvent> VerifyGameAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return StartDeviceOperation(
            OperationKind.GameVerification,
            handle => _native.VerifyGame(handle, packageId),
            cancellationToken);
    }

    public Task<DeviceOperationEvent> CaptureAsync(CancellationToken cancellationToken = default) =>
        StartDeviceOperation(OperationKind.Capture, _native.Capture, cancellationToken);

    public Task<DeviceOperationEvent> TapAsync(
        DeviceFrame frame,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        RunFrameInputAsync(
            frame,
            (handle, frameId) => _native.Tap(handle, frameId, x, y),
            cancellationToken);

    public Task<DeviceOperationEvent> SwipeAsync(
        DeviceFrame frame,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs,
        bool extraSwipe = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMs);
        return RunFrameInputAsync(
            frame,
            (handle, frameId) => _native.Swipe(handle, frameId, x1, y1, x2, y2, durationMs, extraSwipe),
            cancellationToken);
    }

    public Task<DeviceOperationEvent> TouchDownAsync(
        DeviceFrame frame,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        RunFrameInputAsync(frame,
            (handle, frameId) => _native.Touch(handle, frameId, x, y, 0),
            cancellationToken);

    public Task<DeviceOperationEvent> PrepareTouchAsync(
        DeviceFrame frame,
        CancellationToken cancellationToken = default) =>
        RunFrameInputAsync(frame,
            (handle, frameId) => _native.PrepareTouch(handle, frameId),
            cancellationToken);

    public Task<DeviceOperationEvent> KeyEventAsync(
        DeviceFrame frame,
        string keyCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyCode);
        return RunFrameInputAsync(frame,
            (handle, frameId) => _native.KeyEvent(handle, frameId, keyCode),
            cancellationToken);
    }

    public Task<DeviceOperationEvent> InputTextAsync(
        DeviceFrame frame,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(text);
        return RunFrameInputAsync(frame,
            (handle, frameId) => _native.InputText(handle, frameId, text),
            cancellationToken);
    }

    public Task<DeviceOperationEvent> PackageOperationAsync(
        string packageId,
        string? activity,
        PackageOperationKind operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (operation == PackageOperationKind.StartActivity)
            ArgumentException.ThrowIfNullOrWhiteSpace(activity);
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        return StartDeviceOperation(
            OperationKind.PackageOperation,
            handle => _native.PackageOperation(handle, packageId, activity, operation),
            cancellationToken);
    }

    public Task<DeviceOperationEvent> TouchUpAsync(
        DeviceFrame sourceFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ulong frameId = sourceFrame.GetFrameIdForTouchRelease(this);
        return StartDeviceOperation(OperationKind.Input,
            handle => _native.Touch(handle, frameId, 0, 0, 1), cancellationToken);
    }

    public Task<DeviceOperationEvent> TouchCancelAsync(
        DeviceFrame sourceFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ulong frameId = sourceFrame.GetFrameIdForTouchRelease(this);
        return StartDeviceOperation(OperationKind.Input,
            handle => _native.Touch(handle, frameId, 0, 0, 2), cancellationToken);
    }

    public RecognitionImage? LoadTemplateImage(string path, TemplatePreparation preparation = TemplatePreparation.Original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(preparation)) throw new ArgumentOutOfRangeException(nameof(preparation));
        using RecognitionLease lease = AcquireRecognitionLease();
        return _native.LoadTemplateImage(lease.Handle, path, preparation);
    }

    public TemplateMatchResult MatchTemplate(
        RecognitionImage screen,
        RecognitionImage templateImage,
        TemplateMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(templateImage);
        ArgumentNullException.ThrowIfNull(options);
        using RecognitionLease lease = AcquireRecognitionLease();
        return _native.MatchTemplate(lease.Handle, screen, templateImage, options);
    }

    public TextRecognitionResult RecognizeText(RecognitionImage image, string language)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(language);
        using RecognitionLease lease = AcquireRecognitionLease();
        return _native.RecognizeText(lease.Handle, image, language);
    }

    public bool HasMeaningfulChange(
        RecognitionImage before,
        RecognitionImage after,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(match);
        using RecognitionLease lease = AcquireRecognitionLease();
        return _native.HasMeaningfulChange(lease.Handle, before, after, match);
    }

    public bool HasPersistentChange(
        RecognitionImage before,
        RecognitionImage firstAfter,
        RecognitionImage settledAfter,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(firstAfter);
        ArgumentNullException.ThrowIfNull(settledAfter);
        ArgumentNullException.ThrowIfNull(match);
        using RecognitionLease lease = AcquireRecognitionLease();
        return _native.HasPersistentChange(
            lease.Handle, before, firstAfter, settledAfter, match);
    }

    public string? ValidatePipelineJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int result = _native.ValidatePipelineJson(json, out string? errorMessage);
        return result == 0 ? null : (errorMessage ?? $"Pipeline validation failed with error code {result}");
    }

    public async Task<PipelineRunResult> RunPipelineAsync(
        string pipelineJson,
        string? entryTask = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineJson);
        cancellationToken.ThrowIfCancellationRequested();

        ConnectionTerminalEvent terminal = await StartOperation(
            OperationKind.PipelineRun,
            handle => _native.RunPipelineAsync(handle, pipelineJson, entryTask),
            cancellationToken).ConfigureAwait(false);

        return terminal switch
        {
            PipelineCompletedEvent completed => new PipelineRunResult(
                true, completed.FinalTask, completed.TotalSteps, null),
            PipelineFailedEvent failed => new PipelineRunResult(
                false, failed.FinalTask, failed.TotalSteps, failed.ErrorMessage),
            DeviceOperationFailedEvent devFailed => new PipelineRunResult(
                false, string.Empty, 0, devFailed.Message),
            _ => throw BridgeFailure($"Unexpected terminal event for pipeline run: {terminal.GetType().Name}."),
        };
    }

    public async Task<GameTaskRunResult> RunGameTaskAsync(
        string taskId,
        string settingsJson,
        Action<GameTaskProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetInitializedHandle();
        if (CoreApiVersion is not >= 10)
            throw new NotSupportedException("Native game-task execution requires Core API 10 or newer.");

        ConnectionTerminalEvent terminal;
        try
        {
            terminal = await StartOperation(
                OperationKind.GameTaskRun,
                handle => _native.RunGameTaskAsync(handle, taskId, settingsJson),
                cancellationToken,
                gameTaskProgress: progress,
                expectedTaskId: taskId).ConfigureAwait(false);
        }
        catch (ManagedBridgeException exception) when (
            exception.NativeErrorCode is int nativeErrorCode
            && Enum.IsDefined(typeof(ConnectionErrorCode), nativeErrorCode))
        {
            return new GameTaskRunResult(
                taskId, false, 0, 0, false, (ConnectionErrorCode)nativeErrorCode, exception.Message);
        }

        return terminal switch
        {
            GameTaskCompletedEvent completed => new GameTaskRunResult(
                completed.TaskId, true, completed.CompletedUnits, completed.TotalUnits,
                completed.ProcessDetected, completed.ErrorCode, completed.Message),
            GameTaskFailedEvent failed => new GameTaskRunResult(
                failed.TaskId, false, failed.CompletedUnits, failed.TotalUnits,
                failed.ProcessDetected, failed.ErrorCode, failed.Message),
            _ => throw BridgeFailure($"Unexpected terminal event for game-task run: {terminal.GetType().Name}."),
        };
    }

    public async Task<ulong> AppendTaskAsync(
        string taskId,
        string settingsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ValidateNativeString(taskId, nameof(taskId));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        string normalizedSettings = NormalizeSettingsJson(settingsJson);
        await _queueMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeUmaHandle handle = GetInitializedHandle();
            UmaAppendTaskResult result;
            lock (_queueControlLock)
                result = _native.AppendTask(handle, taskId, normalizedSettings);
            if (result.ErrorCode != 0)
                throw NativeQueueFailure("UmaAppendTask", result.ErrorCode);
            if (result.TaskId == 0)
                throw BridgeFailure("UmaAppendTask returned task ID zero after success.");
            return result.TaskId;
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    public async Task SetTaskParamsAsync(
        ulong taskId,
        string settingsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        string normalizedSettings = NormalizeSettingsJson(settingsJson);
        await _queueMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeUmaHandle handle = GetInitializedHandle();
            int code;
            lock (_queueControlLock)
                code = _native.SetTaskParams(handle, taskId, normalizedSettings);
            if (code != 0)
                throw NativeQueueFailure("UmaSetTaskParams", code);
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    public async Task ClearTasksAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        await _queueMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeUmaHandle handle = GetInitializedHandle();
            int code;
            lock (_queueControlLock)
                code = _native.ClearTasks(handle);
            if (code != 0)
                throw NativeQueueFailure("UmaClearTasks", code);
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    public async Task<QueueRunResult> StartQueueAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        await _queueMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<ConnectionTerminalEvent> completion;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetInitializedHandle();
            completion = StartQueueOperation(cancellationToken);
        }
        finally
        {
            _queueMutationLock.Release();
        }

        return await CompleteQueueRunAsync(completion).ConfigureAwait(false);
    }

    /// <summary>Replaces the native queue with this immutable request snapshot and starts it.</summary>
    public async Task<QueueRunResult> RunQueueAsync(
        IReadOnlyList<QueueTaskRequest> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        QueueTaskRequest[] snapshot = CaptureQueueRequests(tasks);

        await _queueMutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<ConnectionTerminalEvent>? completion = null;
        SafeUmaHandle? handle = null;
        bool queueWasCleared = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            handle = GetInitializedHandle();
            int clearCode;
            lock (_queueControlLock)
                clearCode = _native.ClearTasks(handle);
            if (clearCode != 0)
                throw NativeQueueFailure("UmaClearTasks", clearCode);
            queueWasCleared = true;

            foreach (QueueTaskRequest request in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UmaAppendTaskResult append;
                lock (_queueControlLock)
                    append = _native.AppendTask(handle, request.TaskId, request.SettingsJson);
                if (append.ErrorCode != 0)
                    throw NativeQueueFailure("UmaAppendTask", append.ErrorCode);
                if (append.TaskId == 0)
                    throw BridgeFailure("UmaAppendTask returned task ID zero after success.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            completion = StartQueueOperation(cancellationToken);
            if (completion.IsFaulted)
            {
                try
                {
                    _ = completion.GetAwaiter().GetResult();
                }
                catch
                {
                    TryClearQueueAfterFailedSubmission(handle);
                    queueWasCleared = false;
                    throw;
                }
            }
        }
        catch
        {
            if (queueWasCleared && completion is null && handle is not null)
                TryClearQueueAfterFailedSubmission(handle);
            throw;
        }
        finally
        {
            _queueMutationLock.Release();
        }

        return await CompleteQueueRunAsync(completion!).ConfigureAwait(false);
    }

    public Task<string> GetRunSnapshotJsonAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        SafeUmaHandle handle = GetInitializedHandle();
        string json = ReadNativeJson(
            (byte[]? destination, ulong capacity, out ulong requiredLength) =>
                _native.GetRunSnapshotJson(handle, destination, capacity, out requiredLength),
            "UmaGetRunSnapshotJson",
            cancellationToken);
        return Task.FromResult(json);
    }

    public Task<string> GetCatalogJsonAsync(
        string catalogType = "all",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureQueueApiAvailable();
        ArgumentNullException.ThrowIfNull(catalogType);
        ValidateNativeString(catalogType, nameof(catalogType));
        SafeUmaHandle handle = GetInitializedHandle();
        string json = ReadNativeJson(
            (byte[]? destination, ulong capacity, out ulong requiredLength) =>
                _native.GetCatalogJson(handle, catalogType, destination, capacity, out requiredLength),
            "UmaGetCatalogJson",
            cancellationToken);
        return Task.FromResult(json);
    }

    public async Task<QueueRunSnapshot> GetRunSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        string json = await GetRunSnapshotJsonAsync(cancellationToken).ConfigureAwait(false);
        return ParseQueueRunSnapshot(json);
    }

    public async Task<CoreCatalog> GetCatalogAsync(
        string catalogType = "all",
        CancellationToken cancellationToken = default)
    {
        string json = await GetCatalogJsonAsync(catalogType, cancellationToken).ConfigureAwait(false);
        return ParseCoreCatalog(json, catalogType);
    }

    private Task<ConnectionTerminalEvent> StartQueueOperation(CancellationToken cancellationToken)
    {
        EnsureQueueApiAvailable();
        return StartOperation(
            OperationKind.QueueRun,
            handle => _native.StartQueueAsync(handle),
            cancellationToken);
    }

    private static async Task<QueueRunResult> CompleteQueueRunAsync(Task<ConnectionTerminalEvent> completion)
    {
        try
        {
            ConnectionTerminalEvent terminal = await completion.ConfigureAwait(false);
            return terminal switch
            {
                QueueCompletedEvent completed => new QueueRunResult(
                    true, completed.SucceededTasks, completed.FailedTasks,
                    completed.SkippedTasks, completed.TotalTasks,
                    ConnectionErrorCode.Success, string.Empty),
                QueueFailedEvent failed => new QueueRunResult(
                    false, failed.SucceededTasks, failed.FailedTasks,
                    failed.SkippedTasks, failed.TotalTasks,
                    failed.ErrorCode, failed.Message),
                DeviceOperationFailedEvent failed => new QueueRunResult(
                    false, 0, 0, 0, 0, failed.ErrorCode, failed.Message),
                _ => throw BridgeFailure(
                    $"Unexpected terminal event for queue run: {terminal.GetType().Name}."),
            };
        }
        catch (ManagedBridgeException exception) when (
            exception.NativeErrorCode is int nativeErrorCode
            && Enum.IsDefined(typeof(ConnectionErrorCode), nativeErrorCode))
        {
            return new QueueRunResult(
                false, 0, 0, 0, 0, (ConnectionErrorCode)nativeErrorCode, exception.Message);
        }
    }

    private void TryClearQueueAfterFailedSubmission(SafeUmaHandle handle)
    {
        try
        {
            int code;
            lock (_queueControlLock)
                code = _native.ClearTasks(handle);
            if (code != 0)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    null,
                    $"UmaClearTasks could not remove a partially submitted queue (error {code})."));
            }
        }
        catch (Exception exception)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.NativeContractViolation,
                null,
                $"UmaClearTasks threw while removing a partially submitted queue: {exception.GetType().Name}."));
        }
    }

    private void EnsureQueueApiAvailable()
    {
        if (CoreApiVersion is not uint version || version < SupportedCoreApiVersion)
        {
            throw new NotSupportedException(
                $"Native task queue, run snapshot, and catalog APIs require Core API {SupportedCoreApiVersion} or newer.");
        }
    }

    private static QueueTaskRequest[] CaptureQueueRequests(IReadOnlyList<QueueTaskRequest> tasks)
    {
        if (tasks.Count == 0)
            throw new ArgumentException("A queue must contain at least one task request.", nameof(tasks));
        if (tasks.Count > 100_000)
            throw new ArgumentOutOfRangeException(nameof(tasks), "The queue contains too many task requests.");

        var snapshot = new QueueTaskRequest[tasks.Count];
        for (int index = 0; index < tasks.Count; index++)
        {
            QueueTaskRequest request = tasks[index]
                ?? throw new ArgumentException("The queue contains a null task request.", nameof(tasks));
            ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
            ValidateNativeString(request.TaskId, nameof(request.TaskId));
            snapshot[index] = new QueueTaskRequest(
                request.TaskId,
                NormalizeSettingsJson(request.SettingsJson));
        }
        return snapshot;
    }

    private static string NormalizeSettingsJson(string settingsJson)
    {
        settingsJson ??= "{}";
        ValidateNativeString(settingsJson, nameof(settingsJson));
        return string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
    }

    private static void ValidateNativeString(string value, string parameterName)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Native UTF-8 strings cannot contain a NUL character.", parameterName);
    }

    private static string ReadNativeJson(
        NativeJsonReader reader,
        string operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int code = reader(null, 0, out ulong requiredLength);
        if (code != (int)ConnectionErrorCode.BufferTooSmall
            && code != (int)ConnectionErrorCode.Success)
            throw BridgeFailure($"{operation} failed with error code {code}.");

        ValidateNativeJsonLength(requiredLength, operation);
        int capacity = checked((int)requiredLength);
        for (int attempt = 0; attempt < NativeJsonReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] buffer = new byte[capacity];
            code = reader(buffer, (ulong)buffer.Length, out ulong actualLength);
            if (code == (int)ConnectionErrorCode.BufferTooSmall)
            {
                ValidateNativeJsonLength(actualLength, operation);
                if (actualLength <= (ulong)buffer.Length)
                    throw BridgeFailure($"{operation} reported an invalid buffer-too-small length.");
                capacity = checked((int)actualLength);
                continue;
            }

            if (code != (int)ConnectionErrorCode.Success)
                throw BridgeFailure($"{operation} failed while copying JSON with error code {code}.");
            ValidateNativeJsonLength(actualLength, operation);
            if (actualLength > (ulong)buffer.Length)
                throw BridgeFailure($"{operation} reported a JSON length larger than the supplied buffer.");

            int length = checked((int)actualLength);
            if (buffer[length - 1] != 0)
                throw BridgeFailure($"{operation} did not include the required trailing NUL byte.");
            if (Array.IndexOf(buffer, (byte)0, 0, length - 1) >= 0)
                throw BridgeFailure($"{operation} returned an embedded NUL byte.");
            try
            {
                return StrictUtf8.GetString(buffer, 0, length - 1);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ManagedBridgeException(
                    DiagnosticCategory.NativeContractViolation,
                    null,
                    $"{operation} returned invalid UTF-8 JSON.",
                    exception);
            }
        }

        throw BridgeFailure($"{operation} changed size repeatedly while the JSON buffer was being read.");
    }

    private static void ValidateNativeJsonLength(ulong length, string operation)
    {
        if (length == 0 || length > MaxNativeJsonBytes || length > int.MaxValue)
            throw BridgeFailure($"{operation} reported an invalid or excessive JSON buffer length.");
    }

    private static QueueRunSnapshot ParseQueueRunSnapshot(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Run snapshot root must be an object.");

            ulong? operationId = RequiredOptionalUInt64(root, "operation_id");
            ulong? currentTaskId = RequiredOptionalUInt64(root, "current_task_id");
            string stateName = RequiredJsonString(root, "state");
            QueueRunState state = stateName switch
            {
                "idle" => QueueRunState.Idle,
                "running" => QueueRunState.Running,
                "completed" => QueueRunState.Completed,
                "failed" => QueueRunState.Failed,
                "canceled" => QueueRunState.Canceled,
                _ => throw new FormatException($"Unknown run snapshot state '{stateName}'."),
            };

            if (!root.TryGetProperty("tasks", out JsonElement tasksJson)
                || tasksJson.ValueKind != JsonValueKind.Array)
                throw new FormatException("Run snapshot tasks must be an array.");
            var tasks = new List<QueueTaskSnapshot>();
            var taskIds = new HashSet<ulong>();
            foreach (JsonElement item in tasksJson.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Run snapshot task entries must be objects.");
                string statusName = RequiredJsonString(item, "status");
                QueueTaskStatus status = statusName switch
                {
                    "pending" => QueueTaskStatus.Pending,
                    "running" => QueueTaskStatus.Running,
                    "succeeded" => QueueTaskStatus.Succeeded,
                    "failed" => QueueTaskStatus.Failed,
                    "skipped" => QueueTaskStatus.Skipped,
                    _ => throw new FormatException($"Unknown task status '{statusName}'."),
                };
                ulong taskId = RequiredPositiveJsonUInt64(item, "id");
                if (!taskIds.Add(taskId))
                    throw new FormatException("Run snapshot task IDs must be unique.");
                int errorCode = RequiredJsonInt32(item, "error_code");
                int completedUnits = RequiredJsonNonNegativeInt32(item, "completed_units");
                int totalUnits = RequiredJsonNonNegativeInt32(item, "total_units");
                if (errorCode < 0 || (totalUnits > 0 && completedUnits > totalUnits))
                    throw new FormatException("Run snapshot task result fields are inconsistent.");
                tasks.Add(new QueueTaskSnapshot(
                    taskId,
                    RequiredJsonString(item, "name"),
                    status,
                    errorCode,
                    RequiredJsonStringAllowEmpty(item, "message"),
                    completedUnits,
                    totalUnits));
            }

            int succeededCount = RequiredJsonNonNegativeInt32(root, "succeeded_count");
            int failedCount = RequiredJsonNonNegativeInt32(root, "failed_count");
            int skippedCount = RequiredJsonNonNegativeInt32(root, "skipped_count");
            int currentCompletedUnits = RequiredJsonNonNegativeInt32(root, "current_completed_units");
            int currentTotalUnits = RequiredJsonNonNegativeInt32(root, "current_total_units");
            if (currentTotalUnits > 0 && currentCompletedUnits > currentTotalUnits)
                throw new FormatException("Run snapshot current progress exceeds its total.");
            if (tasks.Count(item => item.Status == QueueTaskStatus.Succeeded) != succeededCount
                || tasks.Count(item => item.Status == QueueTaskStatus.Failed) != failedCount
                || tasks.Count(item => item.Status == QueueTaskStatus.Skipped) != skippedCount)
                throw new FormatException("Run snapshot task statuses do not match the run counters.");

            return new QueueRunSnapshot(
                operationId,
                state,
                currentTaskId,
                RequiredJsonStringAllowEmpty(root, "current_task_name"),
                currentCompletedUnits,
                currentTotalUnits,
                succeededCount,
                failedCount,
                skippedCount,
                tasks.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new ManagedBridgeException(
                DiagnosticCategory.NativeContractViolation,
                null,
                "The native run snapshot did not match the queue snapshot contract.",
                exception);
        }
    }

    private static CoreCatalog ParseCoreCatalog(string json, string catalogType)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            var categories = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.ValueKind == JsonValueKind.Array)
            {
                categories[NormalizeCatalogKey(catalogType)] = root.Clone();
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                string requestedCategory = NormalizeCatalogKey(catalogType);
                if (!string.Equals(catalogType, "all", StringComparison.Ordinal)
                    && (IsObjectCatalogCategory(requestedCategory) || !LooksLikeCatalogMap(root)))
                {
                    categories[requestedCategory] = root.Clone();
                }
                else
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                        categories[property.Name] = property.Value.Clone();
                }
            }
            else
            {
                throw new FormatException("Catalog root must be an object or array.");
            }

            IReadOnlyList<JsonElement> Rows(params string[] keys)
            {
                foreach (string key in keys)
                {
                    if (!categories.TryGetValue(key, out JsonElement value))
                        continue;
                    if (value.ValueKind != JsonValueKind.Array)
                        throw new FormatException($"Catalog category '{key}' must be an array.");
                    return CloneCatalogRows(value);
                }
                return Array.Empty<JsonElement>();
            }

            var knownRowCategories = new HashSet<string>(StringComparer.Ordinal)
            {
                "characters", "base_characters", "races", "independent_races", "skills",
                "cards", "support_cards", "events", "strategies",
            };
            var additional = categories
                .Where(pair => !knownRowCategories.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new CoreCatalog(
                Rows("characters"), Rows("base_characters"), Rows("races"),
                Rows("independent_races"), Rows("skills"),
                Rows("cards", "support_cards"), Rows("events"), Rows("strategies"),
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(additional));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new ManagedBridgeException(
                DiagnosticCategory.NativeContractViolation,
                null,
                "The native catalog did not match the catalog response contract.",
                exception);
        }
    }

    private static string NormalizeCatalogKey(string catalogType) => catalogType switch
    {
        "card" or "cards" => "cards",
        "support_cards" => "support_cards",
        _ => catalogType,
    };

    private static bool IsObjectCatalogCategory(string catalogType) => catalogType is
        "database_meta" or "independent_race_metadata" or "skill_metadata" or "event_metadata";

    private static bool LooksLikeCatalogMap(JsonElement root)
    {
        var knownCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "database_meta", "base_characters", "characters", "races", "independent_races",
            "independent_race_metadata", "skills", "skill_metadata", "cards", "support_cards",
            "events", "event_metadata", "strategies",
        };
        return root.EnumerateObject().Any(property => knownCategories.Contains(property.Name));
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<JsonElement> CloneCatalogRows(JsonElement rows)
    {
        var values = new List<JsonElement>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new FormatException("Catalog entries must be JSON objects.");
            values.Add(row.Clone());
        }
        return values.AsReadOnly();
    }

    private static ulong? RequiredOptionalUInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            throw new FormatException($"'{name}' is missing.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong parsed))
            throw new FormatException($"'{name}' must be an unsigned integer.");
        return parsed == 0 ? null : parsed;
    }

    private static ulong RequiredJsonUInt64Value(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetUInt64(out ulong parsed))
            throw new FormatException($"'{name}' must be an unsigned integer.");
        return parsed;
    }

    private static ulong RequiredPositiveJsonUInt64(JsonElement parent, string name)
    {
        ulong value = RequiredJsonUInt64Value(parent, name);
        if (value == 0)
            throw new FormatException($"'{name}' must be positive.");
        return value;
    }

    private static int RequiredJsonInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
            throw new FormatException($"'{name}' must be an integer.");
        return parsed;
    }

    private static int RequiredJsonNonNegativeInt32(JsonElement parent, string name)
    {
        int value = RequiredJsonInt32(parent, name);
        if (value < 0) throw new FormatException($"'{name}' must not be negative.");
        return value;
    }

    private static string RequiredJsonString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"'{name}' must be a nonempty string.");
        return value.GetString()!;
    }

    private static string RequiredJsonStringAllowEmpty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"'{name}' must be a string.");
        return value.GetString() ?? string.Empty;
    }

    private delegate int NativeJsonReader(byte[]? destination, ulong capacity, out ulong requiredLength);

    private async Task<DeviceOperationEvent> RunFrameInputAsync(
        DeviceFrame frame,
        Func<SafeUmaHandle, ulong, UmaStartResult> startNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        DeviceFrame.FrameUse use = frame.Acquire(this);
        try
        {
            bool registered;
            lock (_frameLock)
            {
                registered = _frames.TryGetValue(use.FrameId, out DeviceFrame? current)
                    && ReferenceEquals(current, frame);
            }
            ObjectDisposedException.ThrowIf(!registered, frame);

            return await StartDeviceOperation(
                OperationKind.Input,
                handle => startNative(handle, use.FrameId),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            use.Dispose();
        }
    }

    private Task<ConnectionTerminalEvent> StartOperation(
        OperationKind kind,
        Func<SafeUmaHandle, UmaStartResult> startNative,
        CancellationToken cancellationToken,
        string? adbPath = null,
        string? serial = null,
        Action<GameTaskProgressEvent>? gameTaskProgress = null,
        string? expectedTaskId = null)
    {
        SafeUmaHandle handle = GetInitializedHandle();
        cancellationToken.ThrowIfCancellationRequested();

        OperationState? operation = null;
        operation = new OperationState(
            kind, raw => RouteCallback(operation!, raw), adbPath, serial,
            gameTaskProgress, expectedTaskId);
        lock (_operationLock)
        {
            if (_startingOperation is not null || _activeOperation is not null)
            {
                return Task.FromException<ConnectionTerminalEvent>(
                    BridgeFailure("A native operation is already active."));
            }

            _startingOperation = operation;
            operation.SessionGeneration = kind == OperationKind.Connection
                ? null
                : _sessionGeneration;
        }

        if (cancellationToken.CanBeCanceled)
        {
            operation.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var value = ((UmaService Service, OperationState Operation))state!;
                    value.Service.RequestCancellation(value.Operation);
                },
                (this, operation));
        }

        UmaStartResult start;
        try
        {
            lock (_queueControlLock)
                start = startNative(handle);
        }
        catch (Exception exception)
        {
            ResolveStartFailure(operation, "The native operation threw before returning a start result.", exception);
            return operation.Completion.Task;
        }

        lock (_operationLock)
        {
            if (operation.Terminal)
            {
                return operation.Completion.Task;
            }
        }

        if (start.ErrorCode != (int)ConnectionErrorCode.Success)
        {
            string message = start.OperationId == 0
                ? $"The native operation rejected the start with error code {start.ErrorCode}."
                : "The native operation returned a nonzero operation ID with an error.";
            ResolveRejectedStart(operation, message,
                start.ErrorCode == (int)ConnectionErrorCode.Success
                    ? (int)ConnectionErrorCode.InvalidDeviceResponse
                    : start.ErrorCode);
            return operation.Completion.Task;
        }

        if (start.OperationId == 0)
        {
            ResolveRejectedStart(operation, "The native operation accepted a start with operation ID zero.",
                (int)ConnectionErrorCode.InvalidDeviceResponse);
            return operation.Completion.Task;
        }

        lock (_operationLock)
        {
            operation.OperationId = start.OperationId;
            if (kind == OperationKind.Connection)
            {
                // Native accepted a new connection and has reset/replaced the
                // old device session. Stop associating callbacks and frame
                // leases with that session before buffered connect callbacks
                // are replayed.
                _sessionGeneration = null;
                _activeDeviceIdentity = null;
            }
            _activeOperation = operation;
        }

        if (kind == OperationKind.Connection)
        {
            DetachAllFrames();
        }

        try
        {
            operation.Buffer.Bind(start.OperationId);
        }
        catch (Exception exception)
        {
            FailOperation(
                operation,
                new ManagedBridgeException(
                    DiagnosticCategory.NativeContractViolation,
                    operation.OperationId,
                    "The native callback buffer could not be bound.",
                    exception));
            return operation.Completion.Task;
        }
        lock (_operationLock)
        {
            if (ReferenceEquals(_startingOperation, operation))
            {
                _startingOperation = null;
            }
        }

        TryIssueCancellation(operation);
        return operation.Completion.Task;
    }

    private async Task<DeviceOperationEvent> StartDeviceOperation(
        OperationKind kind,
        Func<SafeUmaHandle, UmaStartResult> startNative,
        CancellationToken cancellationToken)
    {
        ConnectionTerminalEvent result = await StartOperation(kind, startNative, cancellationToken)
            .ConfigureAwait(false);
        return result as DeviceOperationEvent
            ?? throw BridgeFailure("A device operation returned a connection event.");
    }

    public Task ReloadResourceAsync(
        string compositeBaseDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonical = ValidateBaseDirectory(compositeBaseDirectory);
        _ = GetInitializedHandle();
        lock (_operationLock)
        {
            if (_startingOperation is not null || _activeOperation is not null)
                throw new InvalidOperationException("Native operations must be idle before reloading resources.");
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_activeRecognitionCount != 0)
                    throw new InvalidOperationException("Recognition must be idle before reloading resources.");
                var handle = _handle ?? throw new InvalidOperationException("The native bridge is not initialized.");
                // Hold both admission gates until the handle pins the new view.
                RequireSuccess(_native.LoadResource(canonical), "UmaLoadResource");
                RequireSuccess(_native.RefreshResourceSnapshot(handle), "UmaRefreshResourceSnapshot");
                ResourcePath = Path.Combine(canonical, "resource");
            }
        }
        return Task.CompletedTask;
    }

    public Task SetInstanceOptionsAsync(
        DeviceInstanceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.DisplayId is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Display ID cannot be negative.");
        if (options.DisplayId is > 0 && string.IsNullOrWhiteSpace(options.SurfaceDisplayId))
            throw new ArgumentException(
                "A secondary logical display needs its SurfaceFlinger display ID mapping.", nameof(options));
        if (options.SurfaceDisplayId is not null && options.DisplayId is null)
            throw new ArgumentException(
                "SurfaceFlinger display ID must be paired with a logical display ID.", nameof(options));
        if (!double.IsFinite(options.SwipeDurationMultiplier)
            || options.SwipeDurationMultiplier is < 0.01 or > 100.0)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Swipe duration multiplier must be finite and between 0.01 and 100.");
        if (options.ExtraSwipeDistance is < 0 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Extra swipe distance must be between 0 and 100000.");
        if (options.ExtraSwipeDurationMilliseconds is < 0 or > 60000)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Extra swipe duration must be between 0 and 60000 ms.");
        if (!Enum.IsDefined(options.TouchBackend))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown touch input backend.");
        if (options.TouchBackend == TouchInputBackend.MaaTouch && options.DisplayId is > 0)
            throw new ArgumentException("MaaTouch currently supports only logical display 0.", nameof(options));
        if (options.TouchBackend == TouchInputBackend.Minitouch
            && options.DisplayId is > 0 && string.IsNullOrWhiteSpace(options.TouchEventId))
            throw new ArgumentException(
                "Minitouch on a secondary display needs a verified input event ID.", nameof(options));
        string? normalizedTouchEventId = null;
        if (options.TouchEventId is { } touchEventId)
        {
            if (touchEventId.Length is 0 or > 10
                || touchEventId.Any(character => character is < '0' or > '9')
                || !uint.TryParse(touchEventId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint parsedTouchEventId))
                throw new ArgumentException(
                    "Touch event ID must be an unsigned decimal 32-bit value.", nameof(options));
            normalizedTouchEventId = parsedTouchEventId.ToString(CultureInfo.InvariantCulture);
        }
        string touchRemotePath = options.TouchRemotePath;
        if (!IsValidTouchRemotePath(touchRemotePath))
            throw new ArgumentException("Touch helper remote path is invalid.", nameof(options));
        if (options.TouchBackend != TouchInputBackend.Adb
            && string.IsNullOrWhiteSpace(options.TouchBinaryPath))
            throw new ArgumentException(
                "The selected touch backend needs a local helper binary path.", nameof(options));
        if (options.TouchBackend == TouchInputBackend.MaaTouch
            && touchRemotePath.Any(char.IsWhiteSpace))
            throw new ArgumentException("MaaTouch helper path cannot contain whitespace.", nameof(options));
        string? normalizedSurfaceDisplayId = null;
        if (options.SurfaceDisplayId is { } surfaceDisplayId)
        {
            if (surfaceDisplayId.Length is 0 or > 20
                || surfaceDisplayId.Any(character => character is < '0' or > '9')
                || !ulong.TryParse(surfaceDisplayId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong parsedSurfaceDisplayId))
                throw new ArgumentException(
                    "SurfaceFlinger display ID must be an unsigned decimal 64-bit value of at most 20 digits.",
                    nameof(options));
            normalizedSurfaceDisplayId = parsedSurfaceDisplayId.ToString(CultureInfo.InvariantCulture);
        }
        DeviceInstanceOptions normalizedOptions = options with
        {
            SurfaceDisplayId = normalizedSurfaceDisplayId,
            TouchEventId = normalizedTouchEventId,
            TouchRemotePath = touchRemotePath,
        };

        string method = options.ScreenshotMethod switch
        {
            ScreenshotCaptureMethod.Auto => "auto",
            ScreenshotCaptureMethod.Raw => "raw",
            ScreenshotCaptureMethod.Gzip => "gzip",
            ScreenshotCaptureMethod.Png => "png",
            ScreenshotCaptureMethod.ShellPng => "shell_png",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown screenshot capture method."),
        };
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            display_id = normalizedOptions.DisplayId,
            surface_display_id = normalizedOptions.SurfaceDisplayId,
            screenshot_method = method,
            swipe_duration_multiplier = normalizedOptions.SwipeDurationMultiplier,
            extra_swipe_distance = normalizedOptions.ExtraSwipeDistance,
            extra_swipe_duration_ms = normalizedOptions.ExtraSwipeDurationMilliseconds,
            touch_backend = normalizedOptions.TouchBackend switch
            {
                TouchInputBackend.Adb => "adb",
                TouchInputBackend.Minitouch => "minitouch",
                TouchInputBackend.MaaTouch => "maatouch",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            },
            touch_binary_path = normalizedOptions.TouchBinaryPath,
            touch_remote_path = normalizedOptions.TouchRemotePath,
            touch_event_id = normalizedOptions.TouchEventId,
        });
        SafeUmaHandle handle = GetInitializedHandle();
        lock (_operationLock)
        {
            if (_startingOperation is not null || _activeOperation is not null)
                throw new InvalidOperationException("Native operations must be idle before changing device options.");
            RequireSuccess(_native.SetInstanceOptions(handle, json), "UmaSetInstanceOptions");
            bool displayChanged = _deviceOptions.DisplayId != normalizedOptions.DisplayId
                || _deviceOptions.SurfaceDisplayId != normalizedOptions.SurfaceDisplayId;
            if (displayChanged)
            {
                DetachAllFrames();
            }
            _deviceOptions = normalizedOptions;
        }
        return Task.CompletedTask;
    }

    public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default)
    {
        _ = GetInitializedHandle();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfZero(operationId);

        OperationState operation;
        lock (_operationLock)
        {
            operation = _activeOperation is { } active && active.OperationId == operationId
                ? active
                : throw new ArgumentException("The operation ID is not active on this handle.", nameof(operationId));
        }

        RequestCancellation(operation);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
        => DisposeAsync(_shutdownTimeout);

    public ValueTask DisposeAsync(TimeSpan shutdownTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shutdownTimeout, TimeSpan.Zero);
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
            {
                if (shutdownTimeout < _shutdownTimeout)
                    _shutdownTimeout = shutdownTimeout;
                return new ValueTask(_disposeTask);
            }

            _shutdownTimeout = shutdownTimeout;
            _disposing = true;
            _disposeTask = DisposeCoreAsync(_handle);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(SafeUmaHandle? handle)
    {
        await Task.Yield();
        long startedAt = _timeProvider.GetTimestamp();
        OperationState? operation;
        lock (_operationLock)
        {
            operation = _startingOperation ?? _activeOperation;
        }

        if (operation is not null && handle is not null)
        {
            RequestCancellation(operation, handle);
            try
            {
                await operation.Completion.Task
                    .WaitAsync(RemainingShutdownTime(startedAt), _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                DetachAllFrames();
                IntPtr abandoned = handle.Abandon();
                AbandonedNativeHandleRegistry.RetainAbandoned(abandoned, _nativeCallback);
                var timeoutException = new ManagedBridgeException(
                    DiagnosticCategory.FatalShutdownTimeout,
                    operation.OperationId,
                    "The native operation did not terminate before shutdown timeout.");
                if (RequiresOrderedCallbackDrain(operation.Kind))
                {
                    FailGameTaskCallbacksAtShutdownTimeout(operation, timeoutException);
                }
                else
                {
                    FailOperation(operation, timeoutException);
                }

                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.FatalShutdownTimeout,
                    operation.OperationId,
                    "The native handle was retained because shutdown timed out."));
                CompleteDisposal();
                return;
            }
            catch (Exception exception)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    operation.OperationId,
                    $"The active operation faulted during shutdown: {exception.GetType().Name}."));
            }
        }

        Task recognitionDrain;
        lock (_lifecycleLock)
        {
            recognitionDrain = _activeRecognitionCount == 0
                ? Task.CompletedTask
                : _recognitionsDrained!.Task;
        }
        if (handle is not null && !recognitionDrain.IsCompleted)
        {
            try
            {
                await recognitionDrain
                    .WaitAsync(RemainingShutdownTime(startedAt), _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                DetachAllFrames();
                IntPtr abandoned = handle.Abandon();
                AbandonedNativeHandleRegistry.RetainAbandoned(abandoned, _nativeCallback);
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.FatalShutdownTimeout,
                    operation?.OperationId,
                    "The native handle was retained because recognition was still running at shutdown."));
                CompleteDisposal();
                return;
            }
        }

        if (handle is not null)
        {
            ReleaseAllFrames(handle);
            Task destroyTask = Task.Run(handle.Dispose);
            try
            {
                await destroyTask
                    .WaitAsync(RemainingShutdownTime(startedAt), _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AbandonedNativeHandleRegistry.RetainUntilDestroyCompletes(destroyTask, _nativeCallback);
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.FatalShutdownTimeout,
                    operation?.OperationId,
                    "UmaDestroy remained in progress after shutdown timeout."));
            }
        }

        CompleteDisposal();
    }

    private TimeSpan RemainingShutdownTime(long startedAt)
    {
        TimeSpan remaining = _shutdownTimeout - _timeProvider.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void CompleteDisposal()
    {
        lock (_operationLock)
        {
            _sessionGeneration = null;
            _activeDeviceIdentity = null;
        }
        lock (_lifecycleLock)
        {
            _handle = null;
            _disposed = true;
            _disposing = false;
        }
    }

    private void OnNativeCallback(int message, IntPtr detailsJson, IntPtr customArg)
    {
        try
        {
            if (detailsJson == IntPtr.Zero)
            {
                FailCurrentOperation(
                    DiagnosticCategory.MalformedCallback,
                    "The native callback JSON pointer was null.");
                return;
            }

            string? json = Marshal.PtrToStringUTF8(detailsJson);
            if (json is null || Encoding.UTF8.GetByteCount(json) > CallbackParser.MaxCallbackJsonBytes)
            {
                FailCurrentOperation(
                    DiagnosticCategory.MalformedCallback,
                    "The native callback JSON was null or oversized.");
                return;
            }

            var raw = new RawCallback(message, json);
            if (message == 10)
            {
                RouteDeviceDisconnected(raw);
                return;
            }
            if (message == 12)
            {
                RouteDeviceReconnected(raw);
                return;
            }

            StartOperationBuffer? buffer;
            lock (_operationLock)
            {
                buffer = _startingOperation?.Buffer ?? _activeOperation?.Buffer;
            }

            if (buffer is null)
            {
                PublishDiagnostic(new BridgeDiagnostic(
                    DiagnosticCategory.LateEvent,
                    DiagnosticSeverity.Warning,
                    null,
                    "A callback arrived without an active operation."));
                return;
            }

            buffer.Accept(raw);
        }
        catch (Exception exception)
        {
            TryPublishCallbackFailure(exception);
        }
    }

    private void TryPublishCallbackFailure(Exception exception)
    {
        try
        {
            PublishDiagnostic(new BridgeDiagnostic(
                DiagnosticCategory.MalformedCallback,
                DiagnosticSeverity.Error,
                null,
                "The managed callback handler failed."));
        }
        catch (Exception dispatchException)
        {
            Debug.WriteLine(exception);
            Debug.WriteLine(dispatchException);
        }
    }

    private void PublishDiagnostic(BridgeDiagnostic diagnostic) =>
        _dispatcher.Post(() => DiagnosticReceived?.Invoke(diagnostic));

    private void PublishConnectionEvent(ConnectionEvent connectionEvent) =>
        _dispatcher.Post(() => ConnectionEventReceived?.Invoke(connectionEvent));

    private void RouteCallback(OperationState operation, RawCallback raw)
    {
        ConnectionEvent connectionEvent;
        ulong? callbackOperationId;
        try
        {
            connectionEvent = CallbackParser.Parse(raw);
            callbackOperationId = CorrelationOperationId(connectionEvent);
        }
        catch (CallbackProtocolException exception)
        {
            FailOperation(
                operation,
                new ManagedBridgeException(exception.Category, exception.OperationId, exception.Message, exception));
            return;
        }

        bool capturedFrame = connectionEvent is FrameCapturedEvent;
        CancellationTokenRegistration registration = default;
        bool terminal = false;
        BridgeDiagnostic? rejected = null;
        lock (_operationLock)
        {
            rejected = ValidateOperationEvent(operation, connectionEvent, callbackOperationId);
            if (rejected is null && !capturedFrame)
            {
                MarkOperationEvent(operation, connectionEvent, out terminal, out registration);
                if (TryGetRuntimeSequence(connectionEvent, out ulong runtimeSequence))
                    operation.LastRuntimeSequence = runtimeSequence;
                if (connectionEvent is ConnectionSucceededEvent succeeded)
                {
                    _sessionGeneration = succeeded.OperationId;
                    _activeDeviceIdentity = operation.AdbPath is { } connectedAdbPath
                        && operation.Serial is not null
                            ? new DeviceSessionIdentity(
                                connectedAdbPath,
                                succeeded.Serial,
                                succeeded.OperationId)
                            : null;
                }
            }
        }

        if (rejected is not null)
        {
            if (connectionEvent is FrameCapturedEvent rejectedFrame)
            {
                ReleaseUnclaimedFrame(rejectedFrame.Frame.FrameId);
            }
            HandleRejectedEvent(operation, callbackOperationId, rejected);
            return;
        }

        if (connectionEvent is FrameCapturedEvent frameCaptured)
        {
            try
            {
                connectionEvent = CopyCapturedFrame(frameCaptured);
            }
            catch (Exception exception)
            {
                FailOperation(operation, ToOperationFailure(exception, callbackOperationId));
                return;
            }

            lock (_operationLock)
            {
                rejected = ValidateOperationEvent(operation, connectionEvent, callbackOperationId);
                if (rejected is null)
                {
                    MarkOperationEvent(operation, connectionEvent, out terminal, out registration);
                    if (TryGetRuntimeSequence(connectionEvent, out ulong runtimeSequence))
                        operation.LastRuntimeSequence = runtimeSequence;
                }
            }

            if (rejected is not null)
            {
                ((FrameCapturedEvent)connectionEvent).FrameLease?.Dispose();
                HandleRejectedEvent(operation, callbackOperationId, rejected);
                return;
            }
        }

        if (terminal)
        {
            registration.Dispose();
            if (!RequiresOrderedCallbackDrain(operation.Kind))
            {
                CleanupOperation(operation);
            }
        }

        if (ShouldQueueOperationCallback(operation.Kind, connectionEvent))
        {
            QueueGameTaskCallback(operation, connectionEvent);
            return;
        }

        try
        {
            PublishConnectionEvent(connectionEvent);
        }
        catch (Exception exception)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.DispatcherFailure,
                callbackOperationId,
                $"Connection event dispatch failed: {exception.GetType().Name}."));
        }

        if (terminal)
        {
            operation.Completion.TrySetResult((ConnectionTerminalEvent)connectionEvent);
        }
    }

    private void QueueGameTaskCallback(OperationState operation, ConnectionEvent connectionEvent)
    {
        bool scheduleDispatch;
        lock (_operationLock)
        {
            if (!operation.GameTaskCallbackDeliveryAllowed)
            {
                return;
            }

            operation.GameTaskCallbacks.Enqueue(connectionEvent);
            scheduleDispatch = !operation.GameTaskDispatchScheduled;
            operation.GameTaskDispatchScheduled = true;
        }

        if (!scheduleDispatch)
        {
            return;
        }

        try
        {
            _dispatcher.Post(() => DrainGameTaskCallbacks(operation));
        }
        catch (Exception exception)
        {
            FailGameTaskDispatch(operation, connectionEvent, exception);
        }
    }

    private void DrainGameTaskCallbacks(OperationState operation)
    {
        while (true)
        {
            ConnectionEvent connectionEvent;
            lock (_operationLock)
            {
                if (!operation.GameTaskCallbackDeliveryAllowed)
                {
                    operation.GameTaskCallbacks.Clear();
                    operation.GameTaskDispatchScheduled = false;
                    return;
                }

                if (operation.GameTaskCallbacks.Count == 0)
                {
                    operation.GameTaskDispatchScheduled = false;
                    return;
                }

                connectionEvent = operation.GameTaskCallbacks.Dequeue();
            }

            if (operation.Kind == OperationKind.GameTaskRun
                && connectionEvent is GameTaskProgressEvent progress)
            {
                try
                {
                    operation.GameTaskProgress?.Invoke(progress);
                }
                catch (Exception exception)
                {
                    SafePublishDiagnostic(Diagnostic(
                        DiagnosticCategory.DispatcherFailure,
                        progress.OperationId,
                        $"Game-task progress handler failed: {exception.GetType().Name}."));
                }

                continue;
            }

            if (connectionEvent is RuntimeDiagnosticEvent diagnostic)
            {
                try
                {
                    ConnectionEventReceived?.Invoke(diagnostic);
                }
                catch (Exception exception)
                {
                    SafePublishDiagnostic(Diagnostic(
                        DiagnosticCategory.DispatcherFailure,
                        diagnostic.OperationId,
                        $"Runtime diagnostic handler failed: {exception.GetType().Name}."));
                }

                continue;
            }

            bool isQueuedTerminal = connectionEvent is ConnectionTerminalEvent
                && (operation.Kind == OperationKind.QueueRun
                    || connectionEvent is GameTaskCompletedEvent or GameTaskFailedEvent);
            if (!isQueuedTerminal)
            {
                try
                {
                    ConnectionEventReceived?.Invoke(connectionEvent);
                }
                catch (Exception exception)
                {
                    SafePublishDiagnostic(Diagnostic(
                        DiagnosticCategory.DispatcherFailure,
                        CorrelationOperationId(connectionEvent),
                        $"Connection event handler failed: {exception.GetType().Name}."));
                }
                continue;
            }

            try
            {
                ConnectionEventReceived?.Invoke(connectionEvent);
            }
            catch (Exception exception)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.DispatcherFailure,
                    ((ConnectionTerminalEvent)connectionEvent).OperationId,
                    $"Connection event handler failed: {exception.GetType().Name}."));
            }

            CleanupOperation(operation);
            operation.Completion.TrySetResult((ConnectionTerminalEvent)connectionEvent);
        }
    }

    private void FailGameTaskDispatch(
        OperationState operation,
        ConnectionEvent rejectedEvent,
        Exception exception)
    {
        RequestCancellation(operation);
        lock (_operationLock)
        {
            operation.GameTaskCallbackDeliveryAllowed = false;
            operation.GameTaskCallbacks.Clear();
            operation.GameTaskDispatchScheduled = false;
            operation.Terminal = true;
        }

        SafePublishDiagnostic(Diagnostic(
            DiagnosticCategory.DispatcherFailure,
            CorrelationOperationId(rejectedEvent),
            $"Game-task callback dispatch failed: {exception.GetType().Name}."));

        CleanupOperation(operation);
        operation.Completion.TrySetException(new ManagedBridgeException(
            DiagnosticCategory.DispatcherFailure,
            CorrelationOperationId(rejectedEvent),
            "Game-task callbacks could not be dispatched.",
            exception));
    }

    private void FailGameTaskCallbacksAtShutdownTimeout(
        OperationState operation,
        ManagedBridgeException exception)
    {
        CancellationTokenRegistration registration;
        lock (_operationLock)
        {
            operation.GameTaskCallbackDeliveryAllowed = false;
            operation.GameTaskCallbacks.Clear();
            operation.GameTaskDispatchScheduled = false;
            operation.Terminal = true;
            registration = operation.CancellationRegistration;
        }

        registration.Dispose();
        SafePublishDiagnostic(Diagnostic(exception.Category, exception.OperationId, exception.Message));
        CleanupOperation(operation);
        operation.Completion.TrySetException(exception);
    }

    private static BridgeDiagnostic? ValidateOperationEvent(
        OperationState operation,
        ConnectionEvent connectionEvent,
        ulong? callbackOperationId)
    {
        if (operation.Terminal)
        {
            return Diagnostic(DiagnosticCategory.LateEvent, callbackOperationId,
                "A callback arrived after the operation was terminal.");
        }

        if (callbackOperationId is not ulong operationId || operation.OperationId != operationId)
        {
            return Diagnostic(DiagnosticCategory.UnknownEvent, callbackOperationId,
                "The callback operation ID does not match the active operation.");
        }

        if (TryGetRuntimeSequence(connectionEvent, out ulong sequence)
            && sequence <= operation.LastRuntimeSequence)
        {
            return Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                "Task callback sequence numbers must increase strictly.");
        }

        if (connectionEvent is ConnectionStartedEvent)
        {
            return operation.Kind == OperationKind.Connection && !operation.Started
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "ConnectionStarted was emitted unexpectedly or more than once.");
        }

        if (connectionEvent is ConnectionProgressEvent)
        {
            return operation.Kind == OperationKind.Connection && operation.Started
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Connection progress arrived before ConnectionStarted or for a device operation.");
        }

        if (connectionEvent is TaskProgressEvent)
        {
            return operation.Kind == OperationKind.PipelineRun
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Task progress arrived for an operation that is not a pipeline run.");
        }

        if (connectionEvent is TaskStartedEvent or TaskDecisionEvent or TaskActionConfirmedEvent
            or TaskCompletedEvent or TaskFailedEvent)
        {
            return operation.Kind == OperationKind.QueueRun
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Task event arrived for an operation that is not a queue run.");
        }

        if (connectionEvent is GameTaskProgressEvent gameTaskProgress)
        {
            if (operation.Kind == OperationKind.QueueRun)
                return gameTaskProgress.TaskInstanceId != 0
                    ? null
                    : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                        "Queued game-task progress must identify its queue task instance.");
            return operation.Kind == OperationKind.GameTaskRun
                && operation.ExpectedTaskId is { } expectedTaskId
                && string.Equals(gameTaskProgress.TaskId, expectedTaskId, StringComparison.OrdinalIgnoreCase)
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Game-task progress has the wrong task ID or operation kind.");
        }

        if (connectionEvent is RuntimeDiagnosticEvent diagnostic)
        {
            bool validTask = operation.Kind switch
            {
                OperationKind.PipelineRun => diagnostic.TaskId.Length == 0,
                OperationKind.GameTaskRun => operation.ExpectedTaskId is { } expectedTaskId
                    && string.Equals(diagnostic.TaskId, expectedTaskId, StringComparison.OrdinalIgnoreCase),
                OperationKind.QueueRun => true,
                _ => false,
            };
            return validTask
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Runtime diagnostic has the wrong task ID or operation kind.");
        }

        if (connectionEvent is GameTaskCompletedEvent gameTaskCompleted)
        {
            if (operation.Kind == OperationKind.QueueRun) return null;
            return operation.Kind == OperationKind.GameTaskRun
                && operation.ExpectedTaskId is { } expectedTaskId
                && string.Equals(gameTaskCompleted.TaskId, expectedTaskId, StringComparison.OrdinalIgnoreCase)
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Game-task completion has the wrong task ID or operation kind.");
        }

        if (connectionEvent is GameTaskFailedEvent gameTaskFailed)
        {
            if (operation.Kind == OperationKind.QueueRun) return null;
            return operation.Kind == OperationKind.GameTaskRun
                && operation.ExpectedTaskId is { } expectedTaskId
                && string.Equals(gameTaskFailed.TaskId, expectedTaskId, StringComparison.OrdinalIgnoreCase)
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "Game-task failure has the wrong task ID or operation kind.");
        }

        if (connectionEvent is ConnectionTerminalEvent terminalEvent)
        {
            return IsExpectedTerminal(operation.Kind, terminalEvent)
                && (operation.Kind != OperationKind.Connection || operation.Started)
                ? null
                : Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
                    "The operation returned an event that does not match its active operation kind.");
        }

        return Diagnostic(DiagnosticCategory.NativeContractViolation, callbackOperationId,
            "The callback is not valid for an active operation.");
    }

    private static void MarkOperationEvent(
        OperationState operation,
        ConnectionEvent connectionEvent,
        out bool terminal,
        out CancellationTokenRegistration registration)
    {
        terminal = false;
        registration = default;
        if (connectionEvent is ConnectionStartedEvent)
        {
            operation.Started = true;
        }
        else if (connectionEvent is ConnectionTerminalEvent)
        {
            operation.Terminal = true;
            terminal = true;
            registration = operation.CancellationRegistration;
        }
    }

    private void HandleRejectedEvent(
        OperationState operation,
        ulong? callbackOperationId,
        BridgeDiagnostic rejected)
    {
        if (rejected.Category == DiagnosticCategory.NativeContractViolation
            && operation.OperationId == callbackOperationId)
        {
            FailOperation(
                operation,
                new ManagedBridgeException(rejected.Category, rejected.OperationId, rejected.Message));
        }
        else
        {
            SafePublishDiagnostic(rejected);
        }
    }

    private static ManagedBridgeException ToOperationFailure(Exception exception, ulong? operationId) => exception switch
    {
        CallbackProtocolException protocol => new ManagedBridgeException(
            protocol.Category, protocol.OperationId, protocol.Message, protocol),
        ManagedBridgeException bridge => bridge,
        _ => new ManagedBridgeException(
            DiagnosticCategory.NativeContractViolation,
            operationId,
            "The native device operation callback could not be completed.",
            exception),
    };

    private void RouteDeviceDisconnected(RawCallback raw)
    {
        try
        {
            if (CallbackParser.Parse(raw) is not DeviceDisconnectedEvent disconnected)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    null,
                    "The native disconnect message did not contain a disconnect event."));
                return;
            }

            OperationState? operation;
            bool currentSession;
            lock (_operationLock)
            {
                currentSession = _sessionGeneration == disconnected.SessionGeneration;
                if (!currentSession)
                {
                    operation = null;
                }
                else
                {
                    _sessionGeneration = null;
                    _activeDeviceIdentity = null;
                    OperationState? candidate = _startingOperation ?? _activeOperation;
                    operation = candidate is not null
                        && candidate.Kind != OperationKind.Connection
                        && candidate.SessionGeneration == disconnected.SessionGeneration
                            ? candidate
                            : null;
                }
            }

            if (currentSession)
                DetachAllFrames();
            // State and leases are updated before synchronous listeners can re-enter.
            PublishConnectionEvent(disconnected);
            if (operation is not null)
            {
                RequestCancellation(operation);
            }
        }
        catch (CallbackProtocolException exception)
        {
            SafePublishDiagnostic(Diagnostic(exception.Category, null, exception.Message));
        }
        catch (Exception exception)
        {
            TryPublishCallbackFailure(exception);
        }
    }

    private void RouteDeviceReconnected(RawCallback raw)
    {
        try
        {
            if (CallbackParser.Parse(raw) is not DeviceReconnectedEvent reconnected)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    null,
                    "The native reconnect message did not contain a reconnect event."));
                return;
            }

            bool currentSession;
            lock (_operationLock)
            {
                bool connecting = _startingOperation?.Kind == OperationKind.Connection
                    || _activeOperation?.Kind == OperationKind.Connection;
                currentSession = _sessionGeneration == reconnected.PreviousSessionGeneration
                    && !connecting;
                if (currentSession)
                {
                    _sessionGeneration = reconnected.SessionGeneration;
                    if (_activeDeviceIdentity is { } identity)
                    {
                        _activeDeviceIdentity = identity with
                        {
                            Serial = reconnected.Serial,
                            SessionGeneration = reconnected.SessionGeneration,
                        };
                    }
                }
            }

            if (currentSession)
                DetachAllFrames();
            PublishConnectionEvent(reconnected);
        }
        catch (CallbackProtocolException exception)
        {
            SafePublishDiagnostic(Diagnostic(exception.Category, null, exception.Message));
        }
        catch (Exception exception)
        {
            TryPublishCallbackFailure(exception);
        }
    }

    private FrameCapturedEvent CopyCapturedFrame(FrameCapturedEvent captured)
    {
        SafeUmaHandle handle;
        lock (_lifecycleLock)
        {
            handle = _handle ?? throw new ObjectDisposedException(nameof(UmaService));
        }

        bool retained = false;
        bool releaseOnFailure = true;
        try
        {
            lock (_frameLock)
            {
                if (_frames.ContainsKey(captured.Frame.FrameId))
                {
                    releaseOnFailure = false;
                    throw BridgeFailure("The native core reported a frame ID that is already leased.");
                }
            }

            int sizeResult = _native.GetFramePngSize(handle, captured.Frame.FrameId, out ulong size);
            RequireNativeSuccess(sizeResult, "UmaGetFramePngSize");
            if (size == 0 || size > MaxFramePngBytes || size > int.MaxValue || size != captured.Frame.ByteCount)
            {
                throw BridgeFailure("The native frame size is invalid or does not match its callback metadata.");
            }

            byte[] bytes = new byte[(int)size];
            RequireNativeSuccess(
                _native.CopyFramePng(handle, captured.Frame.FrameId, bytes),
                "UmaCopyFramePng");
            var frame = new DeviceFrame(this, captured.Frame, bytes, ReleaseFrameLease);
            lock (_frameLock)
            {
                if (!_frames.TryAdd(frame.FrameId, frame))
                {
                    releaseOnFailure = false;
                    throw BridgeFailure("The native core reused a frame ID while its previous frame was still leased.");
                }
            }

            retained = true;
            return captured with { PngBytes = bytes, FrameLease = frame };
        }
        finally
        {
            if (!retained && releaseOnFailure)
            {
                try
                {
                    _ = _native.ReleaseFrame(handle, captured.Frame.FrameId);
                }
                catch (Exception exception)
                {
                    SafePublishDiagnostic(Diagnostic(
                        DiagnosticCategory.NativeContractViolation,
                        captured.OperationId,
                        $"UmaReleaseFrame failed after a capture callback error: {exception.GetType().Name}."));
                }
            }
        }
    }

    private void ReleaseUnclaimedFrame(ulong frameId)
    {
        lock (_frameLock)
        {
            if (_frames.ContainsKey(frameId))
            {
                return;
            }
        }

        SafeUmaHandle? handle;
        lock (_lifecycleLock)
        {
            handle = _handle;
        }

        if (handle is null)
        {
            return;
        }

        try
        {
            int result = _native.ReleaseFrame(handle, frameId);
            if (result != (int)ConnectionErrorCode.Success)
            {
                SafePublishDiagnostic(new BridgeDiagnostic(
                    DiagnosticCategory.LateEvent,
                    DiagnosticSeverity.Warning,
                    null,
                    $"A rejected frame callback could not be released (error {result})."));
            }
        }
        catch (Exception exception)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.NativeContractViolation,
                null,
                $"UmaReleaseFrame failed for a rejected callback: {exception.GetType().Name}."));
        }
    }

    private void ReleaseFrameLease(DeviceFrame frame)
    {
        bool registered;
        SafeUmaHandle? handle;
        lock (_frameLock)
        {
            registered = _frames.TryGetValue(frame.FrameId, out DeviceFrame? current)
                && ReferenceEquals(current, frame);
            if (registered)
            {
                _frames.Remove(frame.FrameId);
            }
        }

        if (!registered)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            handle = _handle;
        }

        if (handle is null)
        {
            return;
        }

        int result;
        try
        {
            result = _native.ReleaseFrame(handle, frame.FrameId);
        }
        catch (Exception exception)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.NativeContractViolation,
                null,
                $"UmaReleaseFrame failed: {exception.GetType().Name}."));
            return;
        }

        if (result != (int)ConnectionErrorCode.Success)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.NativeContractViolation,
                null,
                $"UmaReleaseFrame returned error code {result}."));
        }
    }

    private void ReleaseAllFrames(SafeUmaHandle handle)
    {
        DeviceFrame[] frames;
        lock (_frameLock)
        {
            frames = _frames.Values.ToArray();
            _frames.Clear();
            foreach (DeviceFrame frame in frames)
            {
                frame.DetachFromOwner();
            }
        }

        foreach (DeviceFrame frame in frames)
        {
            try
            {
                int result = _native.ReleaseFrame(handle, frame.FrameId);
                if (result != (int)ConnectionErrorCode.Success)
                {
                    SafePublishDiagnostic(Diagnostic(
                        DiagnosticCategory.NativeContractViolation,
                        null,
                        $"UmaReleaseFrame returned error code {result} during shutdown."));
                }
            }
            catch (Exception exception)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    null,
                    $"UmaReleaseFrame failed during shutdown: {exception.GetType().Name}."));
            }
        }
    }

    private void DetachAllFrames()
    {
        lock (_frameLock)
        {
            foreach (DeviceFrame frame in _frames.Values)
            {
                frame.DetachFromOwner();
            }

            _frames.Clear();
        }
    }

    private static bool IsExpectedTerminal(OperationKind kind, ConnectionTerminalEvent terminalEvent) => kind switch
    {
        OperationKind.Connection => terminalEvent is ConnectionSucceededEvent or ConnectionFailedEvent,
        OperationKind.GameVerification => terminalEvent is GameVerifiedEvent or GameVerificationFailedEvent or DeviceOperationFailedEvent,
        OperationKind.Capture => terminalEvent is FrameCapturedEvent or DeviceOperationFailedEvent,
        OperationKind.Input => terminalEvent is InputSucceededEvent or InputFailedEvent or DeviceOperationFailedEvent,
        OperationKind.PackageOperation => terminalEvent is PackageOperationCompletedEvent or DeviceOperationFailedEvent,
        OperationKind.PipelineRun => terminalEvent is PipelineCompletedEvent or PipelineFailedEvent or DeviceOperationFailedEvent,
        OperationKind.GameTaskRun => terminalEvent is GameTaskCompletedEvent or GameTaskFailedEvent,
        OperationKind.QueueRun => terminalEvent is QueueCompletedEvent or QueueFailedEvent or DeviceOperationFailedEvent,
        _ => false,
    };

    private static bool RequiresOrderedCallbackDrain(OperationKind kind) =>
        kind is OperationKind.GameTaskRun or OperationKind.QueueRun;

    private static bool ShouldQueueOperationCallback(OperationKind kind, ConnectionEvent connectionEvent) => kind switch
    {
        OperationKind.GameTaskRun => connectionEvent is GameTaskProgressEvent or RuntimeDiagnosticEvent
            or GameTaskCompletedEvent or GameTaskFailedEvent,
        OperationKind.QueueRun => connectionEvent is TaskStartedEvent or TaskDecisionEvent
            or TaskActionConfirmedEvent or TaskCompletedEvent or TaskFailedEvent
            or GameTaskProgressEvent or RuntimeDiagnosticEvent or ConnectionTerminalEvent,
        _ => false,
    };

    private static ulong? CorrelationOperationId(ConnectionEvent connectionEvent) => connectionEvent switch
    {
        ConnectionStartedEvent started => started.OperationId,
        ConnectionProgressEvent progress => progress.OperationId,
        TaskProgressEvent taskProgress => taskProgress.OperationId,
        GameTaskProgressEvent gameTaskProgress => gameTaskProgress.OperationId,
        RuntimeDiagnosticEvent diagnostic => diagnostic.OperationId,
        TaskStartedEvent started => started.OperationId,
        TaskDecisionEvent decision => decision.OperationId,
        TaskActionConfirmedEvent action => action.OperationId,
        TaskCompletedEvent completed => completed.OperationId,
        TaskFailedEvent failed => failed.OperationId,
        ConnectionTerminalEvent terminal => terminal.OperationId,
        _ => null,
    };

    private static bool TryGetRuntimeSequence(ConnectionEvent connectionEvent, out ulong sequence)
    {
        sequence = connectionEvent switch
        {
            TaskProgressEvent progress => progress.Sequence,
            PipelineCompletedEvent completed => completed.Sequence,
            PipelineFailedEvent failed => failed.Sequence,
            DeviceOperationFailedEvent operationFailed => operationFailed.Sequence,
            GameTaskProgressEvent progress => progress.Sequence,
            RuntimeDiagnosticEvent diagnostic => diagnostic.Sequence,
            GameTaskCompletedEvent completed => completed.Sequence,
            GameTaskFailedEvent failed => failed.Sequence,
            TaskStartedEvent started => started.Sequence,
            TaskDecisionEvent decision => decision.Sequence,
            TaskActionConfirmedEvent confirmed => confirmed.Sequence,
            TaskCompletedEvent taskCompleted => taskCompleted.Sequence,
            TaskFailedEvent taskFailed => taskFailed.Sequence,
            QueueCompletedEvent queueCompleted => queueCompleted.Sequence,
            QueueFailedEvent queueFailed => queueFailed.Sequence,
            _ => 0,
        };
        return sequence != 0;
    }

    private static void RequireNativeSuccess(int errorCode, string operation)
    {
        if (errorCode != (int)ConnectionErrorCode.Success)
        {
            throw BridgeFailure($"{operation} failed with error code {errorCode}.");
        }
    }

    private void ResolveRejectedStart(OperationState operation, string message, int? nativeErrorCode = null)
    {
        IReadOnlyList<RawCallback> illegalCallbacks = operation.Buffer.Reject();
        lock (_operationLock)
        {
            operation.Terminal = true;
            if (ReferenceEquals(_startingOperation, operation))
            {
                _startingOperation = null;
            }
        }

        operation.CancellationRegistration.Dispose();
        if (illegalCallbacks.Count > 0)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.NativeContractViolation,
                null,
                "A synchronously rejected start emitted a callback."));
        }

        operation.Completion.TrySetException(new ManagedBridgeException(
            DiagnosticCategory.NativeContractViolation,
            null,
            message,
            nativeErrorCode: nativeErrorCode));
    }

    private void ResolveStartFailure(OperationState operation, string message, Exception exception)
    {
        _ = operation.Buffer.Reject();
        lock (_operationLock)
        {
            operation.Terminal = true;
            if (ReferenceEquals(_startingOperation, operation))
            {
                _startingOperation = null;
            }
        }

        operation.CancellationRegistration.Dispose();
        operation.Completion.TrySetException(new ManagedBridgeException(
            DiagnosticCategory.NativeContractViolation,
            null,
            message,
            exception));
    }

    private void FailOperation(OperationState operation, ManagedBridgeException exception)
    {
        CancellationTokenRegistration registration;
        bool alreadyTerminal;
        lock (_operationLock)
        {
            alreadyTerminal = operation.Terminal;
            registration = operation.CancellationRegistration;
            if (!alreadyTerminal)
            {
                operation.Terminal = true;
            }
        }

        if (alreadyTerminal)
        {
            SafePublishDiagnostic(Diagnostic(
                DiagnosticCategory.LateEvent,
                exception.OperationId,
                "An invalid callback arrived after terminal completion."));
            return;
        }

        registration.Dispose();
        SafePublishDiagnostic(Diagnostic(exception.Category, exception.OperationId, exception.Message));
        operation.Completion.TrySetException(exception);
        CleanupOperation(operation);
    }

    private void RequestCancellation(OperationState operation, SafeUmaHandle? handle = null)
    {
        lock (_operationLock)
        {
            if (operation.Terminal)
            {
                return;
            }

            operation.CancellationRequested = true;
        }

        TryIssueCancellation(operation, handle);
    }

    private void TryIssueCancellation(OperationState operation, SafeUmaHandle? handleOverride = null)
    {
        SafeUmaHandle handle;
        if (handleOverride is not null)
        {
            handle = handleOverride;
        }
        else
        {
            try
            {
                handle = GetInitializedHandle();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        ulong operationId;
        lock (_operationLock)
        {
            if (!operation.CancellationRequested
                || operation.CancellationSent
                || operation.Terminal
                || operation.OperationId is not ulong boundOperationId)
            {
                return;
            }

            operation.CancellationSent = true;
            operationId = boundOperationId;
        }

        int result = _native.CancelOperation(handle, operationId);
        if (result != (int)ConnectionErrorCode.Success)
        {
            bool terminal;
            lock (_operationLock)
            {
                terminal = operation.Terminal;
            }

            if (!terminal)
            {
                SafePublishDiagnostic(Diagnostic(
                    DiagnosticCategory.CancellationFailure,
                    operationId,
                    $"UmaCancelOperation failed with error code {result}."));
            }
        }
    }

    private void SafePublishDiagnostic(BridgeDiagnostic diagnostic)
    {
        try
        {
            PublishDiagnostic(diagnostic);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void FailCurrentOperation(DiagnosticCategory category, string message)
    {
        OperationState? operation;
        lock (_operationLock)
        {
            operation = _startingOperation ?? _activeOperation;
        }

        if (operation is null)
        {
            SafePublishDiagnostic(Diagnostic(category, null, message));
            return;
        }

        FailOperation(
            operation,
            new ManagedBridgeException(category, operation.OperationId, message));
    }

    private void CleanupOperation(OperationState operation)
    {
        lock (_operationLock)
        {
            if (ReferenceEquals(_startingOperation, operation))
            {
                _startingOperation = null;
            }

            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
            }
        }
    }

    private static BridgeDiagnostic Diagnostic(
        DiagnosticCategory category,
        ulong? operationId,
        string message) =>
        new(category, DiagnosticSeverity.Error, operationId, message);

    private SafeUmaHandle GetInitializedHandle()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            return _handle ?? throw new InvalidOperationException("The native bridge is not initialized.");
        }
    }

    private RecognitionLease AcquireRecognitionLease()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            SafeUmaHandle handle = _handle
                ?? throw new InvalidOperationException("The native bridge is not initialized.");
            if (_activeRecognitionCount == 0)
                _recognitionsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            checked { _activeRecognitionCount++; }
            return new RecognitionLease(this, handle);
        }
    }

    private void ReleaseRecognitionLease()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleLock)
        {
            if (_activeRecognitionCount > 0)
                _activeRecognitionCount--;
            if (_activeRecognitionCount == 0)
                drained = _recognitionsDrained;
        }
        drained?.TrySetResult();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
    }

    private static string ValidateBaseDirectory(string path)
    {
        string canonicalPath = ValidateAbsolutePath(path, nameof(path));
        return Directory.Exists(canonicalPath)
            ? canonicalPath
            : throw new DirectoryNotFoundException($"Application base directory does not exist: {canonicalPath}");
    }

    private static bool IsValidTouchRemotePath(string? path)
    {
        const string prefix = "/data/local/tmp/";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 192
            || !path.StartsWith(prefix, StringComparison.Ordinal)
            || path.EndsWith('/')
            || path.Contains("..", StringComparison.Ordinal)
            || path.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or '/' or '_' or '-' or '.')))
            return false;
        return path[prefix.Length..].Split('/').All(segment =>
            segment.Length > 0 && segment != "." && segment != "..");
    }

    private static string ValidateAppDataDirectory(string path) =>
        ValidateAbsolutePath(path, nameof(path));

    private static string ValidateAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Path must be fully qualified.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static void RequireSuccess(int errorCode, string operation)
    {
        if (errorCode != (int)ConnectionErrorCode.Success)
        {
            throw BridgeFailure($"{operation} failed with error code {errorCode}.");
        }
    }

    private static ManagedBridgeException BridgeFailure(string message) =>
        new(DiagnosticCategory.NativeContractViolation, null, message);

    private static ManagedBridgeException NativeQueueFailure(string operation, int errorCode) =>
        new(
            DiagnosticCategory.NativeContractViolation,
            null,
            $"{operation} failed with native error code {errorCode}.",
            nativeErrorCode: errorCode);

    private enum OperationKind
    {
        Connection,
        GameVerification,
        Capture,
        Input,
        PackageOperation,
        PipelineRun,
        GameTaskRun,
        QueueRun,
    }

    private sealed class OperationState
    {
        internal OperationState(
            OperationKind kind,
            Action<RawCallback> route,
            string? adbPath,
            string? serial,
            Action<GameTaskProgressEvent>? gameTaskProgress,
            string? expectedTaskId)
        {
            Kind = kind;
            AdbPath = adbPath;
            Serial = serial;
            GameTaskProgress = gameTaskProgress;
            ExpectedTaskId = expectedTaskId;
            GameTaskCallbackDeliveryAllowed = true;
            Buffer = new StartOperationBuffer(route);
            Completion = new TaskCompletionSource<ConnectionTerminalEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal StartOperationBuffer Buffer { get; }
        internal OperationKind Kind { get; }
        internal string? AdbPath { get; }
        internal string? Serial { get; }
        internal Action<GameTaskProgressEvent>? GameTaskProgress { get; }
        internal string? ExpectedTaskId { get; }
        internal Queue<ConnectionEvent> GameTaskCallbacks { get; } = new();
        internal bool GameTaskDispatchScheduled { get; set; }
        internal bool GameTaskCallbackDeliveryAllowed { get; set; }
        internal TaskCompletionSource<ConnectionTerminalEvent> Completion { get; }
        internal CancellationTokenRegistration CancellationRegistration { get; set; }
        internal ulong? OperationId { get; set; }
        internal bool Started { get; set; }
        internal bool Terminal { get; set; }
        internal bool CancellationRequested { get; set; }
        internal bool CancellationSent { get; set; }
        internal ulong? SessionGeneration { get; set; }
        internal ulong LastRuntimeSequence { get; set; }
    }

    private sealed class RecognitionLease(UmaService owner, SafeUmaHandle handle) : IDisposable
    {
        private UmaService? _owner = owner;

        internal SafeUmaHandle Handle { get; } = handle;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseRecognitionLease();
        }
    }
}
