using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.CoreBridge;

namespace Umamusume.AppCore.Services;

public sealed record CapturedScreenshot(
    byte[] PngBytes,
    byte[]? RgbaPixels,
    int Width,
    int Height,
    DateTime CapturedAt);

public interface IUmaAutomationService : IAsyncDisposable
{
    bool IsInitialized { get; }
    bool IsConnected { get; }
    bool IsBusy { get; }
    bool RequiresRestartForMaintenance { get; }
    string? NativeCoreVersion { get; }
    string? ResourcePath { get; }
    ResourceWorkspace? ActiveResourceWorkspace { get; }
    DeviceSessionIdentity? ActiveDeviceIdentity { get; }
    IUmaService CoreService { get; }
    string CurrentStatus { get; }
    ConnectedDeviceInfo? CurrentDevice { get; }

    event Action<string>? StatusChanged;
    event Action<bool>? BusyChanged;
    event Action<CapturedScreenshot>? ScreenshotCaptured;
    event Action<string>? ResourcesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    string ResolveActiveResourcePath(string sourcePath);
    Task<bool> SaveResourceTextAsync(string path, string text, CancellationToken cancellationToken = default);
    Task<bool> SaveResourceBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task<bool> ConnectAsync(string adbPath, string serial, string profile = "General", CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<CapturedScreenshot?> CaptureScreenshotAsync(CancellationToken cancellationToken = default);
    Task<TextRecognitionResult?> RecognizeOcrAsync(CapturedScreenshot? screenshot = null, string language = "eng", CancellationToken cancellationToken = default);
    Task<bool> StartGameAndRecognizeHomeAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs an arbitrary native operation under the same device-operation gate as task queues and screenshots.</summary>
    Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    Task<T> RunMaintenanceWhenIdleAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    void ActivateResourceWorkspace(ResourceWorkspace workspace);
    void RequireRestartAfterMaintenanceFailure(string reason);
    Task<UmaTaskQueueRunResult> RunTaskQueueAsync(
        IReadOnlyList<UmaTaskRequest> requests,
        Action<UmaTaskQueueEvent>? progress = null,
        CancellationToken cancellationToken = default);
    void CancelCurrentOperation();
}

public sealed class ImmediateEventDispatcher : IEventDispatcher
{
    public void Post(Action action) => action();
}

public sealed class UmaAutomationService : IUmaAutomationService
{
    private readonly IUmaService _umaService;
    private readonly ILogService _logger;
    private readonly IConnectionStateService _connectionState;
    private readonly ISettingsService? _settingsService;
    private readonly object _stateLock = new();
    private ResourceWorkspace? _resourceWorkspace;

    private bool _isInitialized;
    private int _isBusy;
    private string _currentStatus = "Idle";
    private ConnectedDeviceInfo? _currentDevice;
    private CancellationTokenSource? _activeOperationCts;
    private TaskCompletionSource _activeOperationDrained = CompletedSignal();
    private bool _maintenanceReserved;
    private string? _pendingResourcesChanged;
    private string? _maintenanceFailureReason;
    private readonly IResourceSourceProvider? _resourceSourceProvider;
    private CapturedScreenshot? _lastScreenshot;
    private DeviceSessionIdentity? _activeDeviceIdentity;

    public bool IsInitialized => _isInitialized;
    public bool IsConnected => _currentDevice != null && _connectionState.State == ConnectionState.Connected;
    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;
    public bool RequiresRestartForMaintenance => _maintenanceFailureReason is not null;
    public string? NativeCoreVersion => _umaService.CoreVersion;
    public string? ResourcePath => _resourceWorkspace?.ActiveResourceRoot
        ?? _umaService.ResourcePath
        ?? PlatformPaths.GetDefaultResourceDirectory();
    public ResourceWorkspace? ActiveResourceWorkspace => _resourceWorkspace;
    public string ResolveActiveResourcePath(string sourcePath) =>
        _resourceWorkspace?.ResolveActivePath(sourcePath) ?? PlatformPaths.ResolveActiveResourcePath(sourcePath);
    public DeviceSessionIdentity? ActiveDeviceIdentity => _activeDeviceIdentity;
    public IUmaService CoreService => _umaService;
    public string CurrentStatus => _currentStatus;
    public ConnectedDeviceInfo? CurrentDevice => _currentDevice;

    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;
    public event Action<CapturedScreenshot>? ScreenshotCaptured;
    public event Action<string>? ResourcesChanged;

    public UmaAutomationService(
        ILogService logger,
        IConnectionStateService connectionState,
        IUmaService? umaService = null,
        ISettingsService? settingsService = null,
        ResourceWorkspace? resourceWorkspace = null,
        IResourceSourceProvider? resourceSourceProvider = null)
    {
        _logger = logger;
        _connectionState = connectionState;
        _settingsService = settingsService;
        _resourceWorkspace = resourceWorkspace;
        _resourceSourceProvider = resourceSourceProvider;
        _umaService = umaService ?? new UmaService(new ImmediateEventDispatcher());

        _umaService.DiagnosticReceived += diag =>
        {
            _logger.Log($"[CoreDiagnostic] {diag.Category}: {diag.Message}", LogEntryKind.Normal, diag.Category.ToString());
        };
        _umaService.ConnectionEventReceived += OnCoreConnectionEvent;
    }

    private void SetStatus(string status)
    {
        _currentStatus = status;
        _logger.Log(status, LogEntryKind.System);
        StatusChanged?.Invoke(status);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        string appDataDir = PlatformPaths.GetDefaultUserDataDirectory();
        string resourceDir = PlatformPaths.GetDefaultResourceDirectory();
        string appBaseDir = PlatformPaths.GetDefaultAppBaseDirectory();

        if (_resourceWorkspace is not null || _umaService is UmaService)
        {
            var sourceResourceRoot = _resourceSourceProvider is null
                ? resourceDir
                : await _resourceSourceProvider.GetPreferredResourceSourceRootAsync(
                    resourceDir, appDataDir, cancellationToken).ConfigureAwait(false) ?? resourceDir;
            _resourceWorkspace ??= await ResourceWorkspace.PrepareAsync(sourceResourceRoot, appDataDir, cancellationToken)
                .ConfigureAwait(false);
            PlatformPaths.SetActiveResourceDirectory(
                _resourceWorkspace.ActiveResourceRoot,
                _resourceWorkspace.SourceResourceRoot);
            appBaseDir = _resourceWorkspace.ActiveBaseDirectory;
        }

        _logger.Log($"Initializing native core. BaseDir: {appBaseDir}, ResourceDir: {Path.Combine(appBaseDir, "resource")}", LogEntryKind.System);
        SetStatus("Initializing core engine...");

        await _umaService.InitializeAsync(appBaseDir, appDataDir, cancellationToken).ConfigureAwait(false);
        _isInitialized = true;
        SetStatus("Core engine initialized.");
    }

    public Task<bool> SaveResourceTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        return RunExclusiveAsync(async token =>
        {
            if (!_isInitialized)
                await InitializeAsync(token).ConfigureAwait(false);

            if (_resourceWorkspace is null)
            {
                await ResourceWorkspace.SaveFileAtomicallyAsync(path, text, token).ConfigureAwait(false);
                return false;
            }

            var activePath = ResolveActiveResourcePath(path);
            return await _resourceWorkspace.SaveTextAsync(activePath, text, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<bool> SaveResourceBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = bytes.ToArray();
        return RunExclusiveAsync(async token =>
        {
            if (!_isInitialized)
                await InitializeAsync(token).ConfigureAwait(false);

            if (_resourceWorkspace is null)
            {
                await ResourceWorkspace.SaveFileBytesAtomicallyAsync(path, payload, token).ConfigureAwait(false);
                return false;
            }

            var activePath = ResolveActiveResourcePath(path);
            return await _resourceWorkspace.SaveBytesAsync(activePath, payload, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<bool> ConnectAsync(
        string adbPath,
        string serial,
        string profile = "General",
        CancellationToken cancellationToken = default)
    {
        var linkedCts = BeginBusyOperation(cancellationToken);

        try
        {
            await InitializeAsync(linkedCts.Token).ConfigureAwait(false);

            _connectionState.SetState(ConnectionState.Connecting);
            SetStatus($"Connecting to {serial} via {adbPath}...");

            var terminalEvent = await _umaService.ConnectAsync(adbPath, serial, profile, linkedCts.Token).ConfigureAwait(false);

            if (terminalEvent is ConnectionSucceededEvent succeeded)
            {
                _activeDeviceIdentity = _umaService.ActiveDeviceIdentity;
                _currentDevice = new ConnectedDeviceInfo(
                    succeeded.Serial,
                    succeeded.AndroidId,
                    succeeded.AndroidVersion,
                    succeeded.Width,
                    succeeded.Height,
                    succeeded.PhysicalWidth,
                    succeeded.PhysicalHeight);

                _connectionState.SetConnected(
                    succeeded.Serial,
                    succeeded.Width,
                    succeeded.Height);

                SetStatus($"Connected to {succeeded.Serial} ({succeeded.Width}x{succeeded.Height})");
                _logger.Log($"Device connected: {succeeded.Serial}, Physical: {succeeded.PhysicalWidth}x{succeeded.PhysicalHeight}", LogEntryKind.Success);
                return true;
            }
            else if (terminalEvent is ConnectionFailedEvent failed)
            {
                _activeDeviceIdentity = null;
                _currentDevice = null;
                _connectionState.SetDisconnected();
                SetStatus($"Connection failed: {failed.Message} ({failed.Phase})");
                _logger.Log($"Connection failed: [{failed.ErrorCode}] {failed.Message} in {failed.Phase}", LogEntryKind.Error);
                return false;
            }
            else
            {
                _activeDeviceIdentity = null;
                _connectionState.SetDisconnected();
                SetStatus("Connection stopped or canceled.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _activeDeviceIdentity = null;
            _connectionState.SetDisconnected();
            SetStatus($"Connection exception: {ex.Message}");
            _logger.Log($"Exception connecting: {ex}", LogEntryKind.Error);
            return false;
        }
        finally
        {
            EndBusyOperation(linkedCts);
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            CancelCurrentOperation();
        SetStatus("Disconnecting...");
        _activeDeviceIdentity = null;
        _currentDevice = null;
        _connectionState.SetDisconnected();
        SetStatus("Disconnected.");
        return Task.CompletedTask;
    }

    public async Task<CapturedScreenshot?> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            SetStatus("Cannot capture: Not connected.");
            return null;
        }

        var linkedCts = BeginBusyOperation(cancellationToken);
        try
        {
            return await CaptureScreenshotCoreAsync(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            EndBusyOperation(linkedCts);
        }
    }

    private async Task<CapturedScreenshot?> CaptureScreenshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            SetStatus("Cannot capture: Not connected.");
            return null;
        }

        try
        {
            SetStatus("Capturing screenshot...");
            var opEvent = await _umaService.CaptureAsync(cancellationToken).ConfigureAwait(false);
            _logger.Log($"Capture returned event: {opEvent?.GetType().Name} ({opEvent})", LogEntryKind.System);
            if (opEvent is FrameCapturedEvent frameEvent && frameEvent.PngBytes.Length > 0)
            {
                byte[]? rgba = null;
                int width = frameEvent.Frame.Width;
                int height = frameEvent.Frame.Height;

                try
                {
                    var decoded = RecognitionImageDecoder.DecodeBytes(frameEvent.PngBytes);
                    if (decoded != null)
                    {
                        rgba = decoded.RgbaPixels;
                        width = decoded.Width;
                        height = decoded.Height;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Warning: Could not decode RGBA from PNG: {ex.Message}", LogEntryKind.Warning);
                }

                frameEvent.FrameLease?.Dispose();

                var captured = new CapturedScreenshot(
                    frameEvent.PngBytes,
                    rgba,
                    width,
                    height,
                    DateTime.Now);

                _lastScreenshot = captured;
                SetStatus($"Screenshot captured ({captured.Width}x{captured.Height}, {frameEvent.PngBytes.Length} PNG bytes).");
                ScreenshotCaptured?.Invoke(captured);
                return captured;
            }

            SetStatus("Screenshot capture returned empty frame.");
            return null;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            SetStatus($"Screenshot capture error: {ex.Message}");
            _logger.Log($"Screenshot error: {ex}", LogEntryKind.Error);
            return null;
        }
    }

    public async Task<TextRecognitionResult?> RecognizeOcrAsync(
        CapturedScreenshot? screenshot = null,
        string language = "eng",
        CancellationToken cancellationToken = default)
    {
        var linkedCts = BeginBusyOperation(cancellationToken);

        try
        {
            var target = screenshot ?? _lastScreenshot;
            if (target is null)
            {
                target = await CaptureScreenshotCoreAsync(linkedCts.Token).ConfigureAwait(false);
                if (target is null) return null;
            }

            SetStatus("Running OCR recognition...");
            RecognitionImage? recImage = null;
            if (target.RgbaPixels != null && target.RgbaPixels.Length == target.Width * target.Height * 4)
            {
                recImage = new RecognitionImage(target.Width, target.Height, null, target.RgbaPixels);
            }
            else if (target.PngBytes.Length > 0)
            {
                recImage = RecognitionImageDecoder.DecodeBytes(target.PngBytes);
            }

            if (recImage is null)
            {
                SetStatus("Failed to decode screenshot for OCR.");
                return null;
            }

            var ocrResult = _umaService.RecognizeText(recImage, language);

            SetStatus($"OCR complete: {ocrResult.Detections.Count} detections (language: {ocrResult.Language}).");
            _logger.Log($"OCR found {ocrResult.Detections.Count} text items. Detections: {string.Join(", ", ocrResult.Detections.Select(d => d.Text))}", LogEntryKind.Success);
            return ocrResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus($"OCR error: {ex.Message}");
            _logger.Log($"OCR recognition failed: {ex}", LogEntryKind.Error);
            return null;
        }
        finally
        {
            EndBusyOperation(linkedCts);
        }
    }

    public async Task<bool> StartGameAndRecognizeHomeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            SetStatus("Cannot start game: Device not connected.");
            return false;
        }

        var linkedCts = BeginBusyOperation(cancellationToken);

        try
        {
            var settings = _settingsService?.Load() ?? new ConnectionSettings();
            var configuredPackageId = settings.TargetPackageIds
                .FirstOrDefault(package => !string.IsNullOrWhiteSpace(package))
                ?? "com.cygames.umamusume";
            var configuredActivity = string.IsNullOrWhiteSpace(settings.TargetActivityName)
                ? "jp.co.cygames.umamusume_activity.UmamusumeActivity"
                : settings.TargetActivityName;
            var taskSettings = UmaTaskSettingsBuilder.Build(
                settings,
                "start-game",
                new JsonObject
                {
                    ["packageId"] = configuredPackageId,
                    ["activityName"] = configuredActivity,
                });
            configuredPackageId = taskSettings["packageId"]!.GetValue<string>();

            SetStatus($"Launching game package {configuredPackageId}...");
            _logger.Log($"Launching game {configuredPackageId}...", LogEntryKind.Normal);

            try
            {
                await _umaService.PackageOperationAsync(
                    configuredPackageId,
                    null,
                    PackageOperationKind.StartPackage,
                    linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"Notice: Package start issued: {ex.Message}", LogEntryKind.Warning);
            }

            SetStatus("Starting game and recognizing home screen...");
            _logger.Log("Executing native start-game task...", LogEntryKind.Normal);
            var result = await _umaService.RunGameTaskAsync(
                "start-game",
                taskSettings.ToJsonString(),
                progress => _logger.Log($"StartGame progress: {progress.TaskName}", LogEntryKind.Normal),
                linkedCts.Token).ConfigureAwait(false);

            if (result.Succeeded)
            {
                SetStatus("Game home screen recognized successfully!");
                _logger.Log("StartGame task succeeded: Home screen detected.", LogEntryKind.Success);
                await CaptureScreenshotCoreAsync(linkedCts.Token).ConfigureAwait(false);
                return true;
            }
            else
            {
                SetStatus($"StartGame task ended: Succeeded={result.Succeeded}");
                _logger.Log($"StartGame task ended: Succeeded={result.Succeeded}, Message={result.Message}", LogEntryKind.Normal);
                await CaptureScreenshotCoreAsync(linkedCts.Token).ConfigureAwait(false);
                return result.Succeeded;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Start game task canceled by user.");
            _logger.Log("Start game task canceled.", LogEntryKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus($"Start game error: {ex.Message}");
            _logger.Log($"Start game exception: {ex}", LogEntryKind.Error);
            return false;
        }
        finally
        {
            EndBusyOperation(linkedCts);
        }
    }

    /// <summary>
    /// Executes a native operation while holding the shared device-operation gate.
    /// Use this for diagnostic pipelines and other native work that must not race a
    /// task queue, screenshot, OCR, or connection operation.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var linkedCts = BeginBusyOperation(cancellationToken);
        try
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            return await operation(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            EndBusyOperation(linkedCts);
        }
    }

    public async Task<T> RunMaintenanceWhenIdleAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task drained;
        bool notifyBusy;
        lock (_stateLock)
        {
            if (_maintenanceReserved)
                throw new InvalidOperationException("Another maintenance operation is already reserved.");
            _maintenanceReserved = true;
            drained = _activeOperationCts is null ? Task.CompletedTask : _activeOperationDrained.Task;
            notifyBusy = _isBusy == 0;
            Volatile.Write(ref _isBusy, 1);
        }

        if (notifyBusy)
            RaiseBusyChanged(true);

        try
        {
            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool stateChanged;
            bool remainsBusy;
            string? resourcesChanged;
            lock (_stateLock)
            {
                _maintenanceReserved = false;
                remainsBusy = _activeOperationCts is not null;
                int wasBusy = Interlocked.Exchange(ref _isBusy, remainsBusy ? 1 : 0);
                stateChanged = wasBusy != (remainsBusy ? 1 : 0);
                resourcesChanged = _pendingResourcesChanged;
                _pendingResourcesChanged = null;
            }
            if (stateChanged)
                RaiseBusyChanged(remainsBusy);
            if (resourcesChanged is not null)
                NotifyResourcesChanged(resourcesChanged);
        }
    }

    public void ActivateResourceWorkspace(ResourceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_stateLock)
        {
            if (!_maintenanceReserved)
                throw new InvalidOperationException("A resource workspace can only change during reserved maintenance.");
            _resourceWorkspace = workspace;
            PlatformPaths.SetActiveResourceDirectory(workspace.ActiveResourceRoot, workspace.SourceResourceRoot);
            _pendingResourcesChanged = workspace.ActiveResourceRoot;
        }
    }

    public void RequireRestartAfterMaintenanceFailure(string reason)
    {
        _maintenanceFailureReason = string.IsNullOrWhiteSpace(reason) ? "Resource recovery failed." : reason;
        SetStatus($"Resource recovery failed. Restart the application before using the native core. {_maintenanceFailureReason}");
    }

    public async Task<UmaTaskQueueRunResult> RunTaskQueueAsync(
        IReadOnlyList<UmaTaskRequest> requests,
        Action<UmaTaskQueueEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0)
            return new(true, false, 0, 0, 0, string.Empty);

        var settings = _settingsService?.Load() ?? new ConnectionSettings();
        var capturedRequests = requests.Select(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var definition = UmaTaskCatalog.Get(request.TaskId);
            var profileSettings = UmaTaskSettingsSerializer.Deserialize(request.SettingsJson);
            var taskSettings = UmaTaskSettingsBuilder.Build(settings, definition.Id, profileSettings);
            return new QueueTaskRequest(definition.Id, taskSettings.ToJsonString());
        }).ToArray();

        var linkedCts = BeginBusyOperation(cancellationToken);
        var indicesByTaskInstance = new Dictionary<ulong, int>();
        var terminalIndices = new HashSet<int>();
        var succeededIndices = new HashSet<int>();
        var failedIndices = new HashSet<int>();
        var canceledIndices = new HashSet<int>();
        var skippedIndices = new HashSet<int>();
        ulong? runOperationId = null;
        var currentTaskIndex = -1;

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

        bool TryResolveTaskIndex(ulong taskInstanceId, ulong operationId, out int index)
        {
            index = -1;
            return AcceptOperation(operationId)
                && taskInstanceId != 0
                && indicesByTaskInstance.TryGetValue(taskInstanceId, out index)
                && index >= 0
                && index < requests.Count;
        }

        void Report(UmaTaskQueueEvent update)
        {
            if (progress is null)
                return;
            try
            {
                progress(update);
            }
            catch (Exception exception)
            {
                _logger.Log($"Task progress observer failed: {exception.Message}", LogEntryKind.Warning);
            }
        }

        void OnCoreEvent(ConnectionEvent connectionEvent)
        {
            switch (connectionEvent)
            {
                case TaskStartedEvent started when started.Index >= 0 && started.Index < requests.Count:
                    if (!AcceptOperation(started.OperationId, establish: true))
                        return;
                    indicesByTaskInstance[started.TaskId] = started.Index;
                    currentTaskIndex = started.Index;
                    Report(new(
                        started.Index,
                        requests[started.Index].TaskId,
                        UmaTaskQueueEventKind.Started,
                        TaskName: started.Name));
                    break;

                case GameTaskProgressEvent gameTaskProgress
                    when TryResolveTaskIndex(gameTaskProgress.TaskInstanceId, gameTaskProgress.OperationId, out var gameIndex):
                    if (gameTaskProgress.EventType is GameTaskEventType.ActionExecuted or GameTaskEventType.TaskFailed)
                        _logger.Log(gameTaskProgress.Message,
                            gameTaskProgress.Succeeded ? LogEntryKind.Normal : LogEntryKind.Error, "TaskRuntime");
                    Report(new(
                        gameIndex,
                        requests[gameIndex].TaskId,
                        UmaTaskQueueEventKind.Progress,
                        gameTaskProgress.CompletedUnits,
                        gameTaskProgress.TotalUnits,
                        gameTaskProgress.TaskName,
                        gameTaskProgress.Message,
                        gameTaskProgress.Succeeded,
                        EventType: gameTaskProgress.EventType));
                    break;

                case TaskProgressEvent taskProgress
                    when TryResolveTaskIndex(taskProgress.TaskInstanceId, taskProgress.OperationId, out var progressIndex):
                    _logger.Log(taskProgress.Message,
                        taskProgress.Succeeded ? LogEntryKind.Normal : LogEntryKind.Error, "TaskRuntime");
                    Report(new(
                        progressIndex,
                        requests[progressIndex].TaskId,
                        UmaTaskQueueEventKind.Progress,
                        taskProgress.StepIndex,
                        null,
                        taskProgress.TaskName,
                        taskProgress.Message,
                        taskProgress.Succeeded));
                    break;

                case TaskDecisionEvent decision
                    when TryResolveTaskIndex(decision.TaskId, decision.OperationId, out var decisionIndex):
                    Report(new(decisionIndex, requests[decisionIndex].TaskId, UmaTaskQueueEventKind.Decision,
                        TaskName: decision.DecisionType, Message: decision.Details));
                    break;

                case TaskActionConfirmedEvent confirmed
                    when TryResolveTaskIndex(confirmed.TaskId, confirmed.OperationId, out var confirmedIndex):
                    Report(new(confirmedIndex, requests[confirmedIndex].TaskId, UmaTaskQueueEventKind.ActionConfirmed,
                        TaskName: confirmed.ActionName, Succeeded: confirmed.Confirmed));
                    break;

                case RuntimeDiagnosticEvent diagnostic when AcceptOperation(diagnostic.OperationId):
                    var diagnosticKind = diagnostic.Level switch
                    {
                        RuntimeDiagnosticLevel.Warning => LogEntryKind.Warning,
                        RuntimeDiagnosticLevel.Error => LogEntryKind.Error,
                        _ => LogEntryKind.Normal,
                    };
                    var taskLabel = string.IsNullOrWhiteSpace(diagnostic.TaskId)
                        ? string.Empty
                        : $" [{diagnostic.TaskId}]";
                    _logger.Log(
                        $"[{diagnostic.Level.ToString().ToUpperInvariant()}]{taskLabel} {diagnostic.Message}",
                        diagnosticKind,
                        "TaskRuntime");
                    break;

                case TaskCompletedEvent completed
                    when TryResolveTaskIndex(completed.TaskId, completed.OperationId, out var completedIndex):
                    terminalIndices.Add(completedIndex);
                    succeededIndices.Add(completedIndex);
                    Report(new(
                        completedIndex,
                        requests[completedIndex].TaskId,
                        UmaTaskQueueEventKind.Completed,
                        completed.CompletedUnits,
                        completed.TotalUnits,
                        completed.Name,
                        completed.Message,
                        true));
                    break;

                case TaskFailedEvent failed
                    when TryResolveTaskIndex(failed.TaskId, failed.OperationId, out var failedIndex):
                    terminalIndices.Add(failedIndex);
                    var wasCanceled = failed.ErrorCode == ConnectionErrorCode.Canceled;
                    if (wasCanceled)
                        canceledIndices.Add(failedIndex);
                    else
                        failedIndices.Add(failedIndex);
                    Report(new(
                        failedIndex,
                        requests[failedIndex].TaskId,
                        wasCanceled
                            ? UmaTaskQueueEventKind.Canceled
                            : UmaTaskQueueEventKind.Failed,
                        TaskName: failed.Name,
                        Message: failed.Message,
                        Succeeded: false,
                        ErrorCode: failed.ErrorCode));
                    break;
            }
        }

        QueueRunResult? queueResult = null;
        QueueRunSnapshot? snapshot = null;
        string message = string.Empty;
        var failedByException = false;
        var canceledByException = false;
        _umaService.ConnectionEventReceived += OnCoreEvent;
        try
        {
            SetStatus($"Running task queue ({requests.Count} tasks)...");
            queueResult = await _umaService.RunQueueAsync(capturedRequests, linkedCts.Token).ConfigureAwait(false);
            try
            {
                var latestSnapshot = await _umaService.GetRunSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                if (runOperationId is null || latestSnapshot.OperationId == runOperationId)
                    snapshot = latestSnapshot;
            }
            catch (NotSupportedException)
            {
                // Progress and terminal events remain sufficient for test doubles
                // and older CoreBridge implementations without queue snapshots.
            }
            catch (Exception exception)
            {
                _logger.Log($"Could not read final task queue snapshot: {exception.Message}", LogEntryKind.Warning);
            }
        }
        catch (OperationCanceledException exception)
        {
            canceledByException = true;
            message = string.IsNullOrWhiteSpace(exception.Message) ? "Canceled" : exception.Message;
        }
        catch (Exception exception)
        {
            failedByException = true;
            message = exception.Message;
            _logger.Log($"Task queue failed: {exception}", LogEntryKind.Error);
        }
        finally
        {
            _umaService.ConnectionEventReceived -= OnCoreEvent;
            EndBusyOperation(linkedCts);
        }

        if (snapshot is not null)
        {
            var count = Math.Min(requests.Count, snapshot.Tasks.Count);
            for (var index = 0; index < count; index++)
            {
                var task = snapshot.Tasks[index];
                indicesByTaskInstance[task.Id] = index;
                if (terminalIndices.Contains(index))
                    continue;

                switch (task.Status)
                {
                    case QueueTaskStatus.Succeeded:
                        terminalIndices.Add(index);
                        succeededIndices.Add(index);
                        Report(new(index, requests[index].TaskId, UmaTaskQueueEventKind.Completed,
                            task.CompletedUnits, task.TotalUnits, task.Name, task.Message, true));
                        break;
                    case QueueTaskStatus.Failed:
                        terminalIndices.Add(index);
                        var errorCode = Enum.IsDefined(typeof(ConnectionErrorCode), task.ErrorCode)
                            ? (ConnectionErrorCode)task.ErrorCode
                            : ConnectionErrorCode.CommandFailed;
                        if (errorCode == ConnectionErrorCode.Canceled)
                            canceledIndices.Add(index);
                        else
                            failedIndices.Add(index);
                        Report(new(index, requests[index].TaskId,
                            errorCode == ConnectionErrorCode.Canceled
                                ? UmaTaskQueueEventKind.Canceled : UmaTaskQueueEventKind.Failed,
                            task.CompletedUnits, task.TotalUnits, task.Name, task.Message, false, errorCode));
                        break;
                    case QueueTaskStatus.Skipped:
                        terminalIndices.Add(index);
                        skippedIndices.Add(index);
                        Report(new(index, requests[index].TaskId, UmaTaskQueueEventKind.Skipped,
                            task.CompletedUnits, task.TotalUnits, task.Name, task.Message));
                        break;
                }
            }
        }

        var canceled = canceledIndices.Count > 0
            || canceledByException
            || cancellationToken.IsCancellationRequested
            || queueResult?.ErrorCode == ConnectionErrorCode.Canceled
            || snapshot?.State == QueueRunState.Canceled;
        var failedCount = queueResult?.FailedTasks ?? 0;
        var skippedCount = queueResult?.SkippedTasks ?? 0;

        if (failedByException || queueResult is { Succeeded: false } && !canceled)
        {
            var failedIndex = currentTaskIndex;
            if (failedIndex < 0 || terminalIndices.Contains(failedIndex))
                failedIndex = Enumerable.Range(0, requests.Count).FirstOrDefault(index => !terminalIndices.Contains(index), -1);
            if (failedIndex >= 0 && terminalIndices.Add(failedIndex))
            {
                failedIndices.Add(failedIndex);
                Report(new(failedIndex, requests[failedIndex].TaskId, UmaTaskQueueEventKind.Failed,
                    Message: message.Length == 0 ? queueResult?.Message : message,
                    Succeeded: false,
                    ErrorCode: queueResult?.ErrorCode ?? ConnectionErrorCode.CommandFailed));
            }
        }
        else if (canceled)
        {
            var canceledIndex = snapshot?.CurrentTaskId is { } snapshotTaskId
                ? snapshot.Tasks.ToList().FindIndex(task => task.Id == snapshotTaskId)
                : currentTaskIndex;
            if (canceledIndex >= 0 && canceledIndex < requests.Count && terminalIndices.Add(canceledIndex))
            {
                canceledIndices.Add(canceledIndex);
                Report(new(canceledIndex, requests[canceledIndex].TaskId, UmaTaskQueueEventKind.Canceled,
                    Message: message.Length == 0 ? "Canceled" : message,
                    Succeeded: false,
                    ErrorCode: ConnectionErrorCode.Canceled));
            }
        }

        if (failedByException || canceled || queueResult is { Succeeded: false })
        {
            var skipFrom = Math.Max(0, (snapshot?.CurrentTaskId is { } activeTaskId
                ? snapshot.Tasks.ToList().FindIndex(task => task.Id == activeTaskId)
                : currentTaskIndex) + 1);
            for (var index = skipFrom; index < requests.Count; index++)
            {
                if (!terminalIndices.Add(index))
                    continue;
                skippedIndices.Add(index);
                Report(new(index, requests[index].TaskId, UmaTaskQueueEventKind.Skipped,
                    Message: canceled ? "Canceled" : "Task queue stopped"));
            }
        }

        failedCount = Math.Max(failedCount, failedIndices.Count);
        skippedCount = Math.Max(skippedCount, skippedIndices.Count);
        var succeededCount = queueResult?.SucceededTasks
            ?? (snapshot?.SucceededCount ?? 0);
        if (succeededCount == 0)
            succeededCount = succeededIndices.Count;
        if (succeededCount == 0 && failedCount == 0 && skippedCount == 0 && !canceled)
            succeededCount = requests.Count;
        if (string.IsNullOrWhiteSpace(message))
            message = queueResult?.Message ?? string.Empty;

        var succeeded = !failedByException
            && !canceled
            && queueResult?.Succeeded == true
            && failedCount == 0
            && skippedCount == 0;
        SetStatus(canceled
            ? "Task queue canceled."
            : succeeded ? "Task queue completed." : "Task queue finished with errors.");
        return new(succeeded, canceled, succeededCount, failedCount, skippedCount, message);
    }

    private CancellationTokenSource BeginBusyOperation(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock)
        {
            if (_maintenanceFailureReason is not null)
            {
                linked.Dispose();
                throw new InvalidOperationException("The native core is disabled until the application restarts because resource recovery failed.");
            }
            if (_isBusy != 0 || _maintenanceReserved)
            {
                linked.Dispose();
                throw new InvalidOperationException("Another device operation is already running.");
            }

            Volatile.Write(ref _isBusy, 1);
            _activeOperationCts = linked;
            _activeOperationDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RaiseBusyChanged(true);
        return linked;
    }

    private void EndBusyOperation(CancellationTokenSource linked)
    {
        bool becameIdle;
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeOperationCts, linked))
            {
                _activeOperationCts = null;
                _activeOperationDrained.TrySetResult();
            }
            becameIdle = !_maintenanceReserved && Interlocked.Exchange(ref _isBusy, 0) != 0;
        }

        linked.Dispose();
        if (becameIdle)
            RaiseBusyChanged(false);
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private void RaiseBusyChanged(bool isBusy)
    {
        var handlers = BusyChanged;
        if (handlers is null)
            return;

        foreach (Action<bool> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(isBusy);
            }
            catch (Exception exception)
            {
                _logger.Log($"Busy-state observer failed: {exception.Message}", LogEntryKind.Warning);
            }
        }
    }

    private void NotifyResourcesChanged(string path)
    {
        var handlers = ResourcesChanged;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(path); }
            catch (Exception exception)
            {
                _logger.Log($"Resource-change observer failed: {exception.Message}", LogEntryKind.Warning);
            }
        }
    }

    public void CancelCurrentOperation()
    {
        try
        {
            _activeOperationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completion may dispose the source while an external caller cancels it.
        }
    }

    private void OnCoreConnectionEvent(ConnectionEvent connectionEvent)
    {
        switch (connectionEvent)
        {
            case DeviceReconnectedEvent reconnected:
                if (_activeDeviceIdentity is not { } active
                    || active.SessionGeneration != reconnected.PreviousSessionGeneration
                    || !string.Equals(active.Serial, reconnected.Serial, StringComparison.Ordinal))
                    return;

                _activeDeviceIdentity = active with { SessionGeneration = reconnected.SessionGeneration };
                if (_currentDevice is { } current)
                {
                    _currentDevice = current with
                    {
                        Width = reconnected.Width,
                        Height = reconnected.Height,
                        PhysicalWidth = reconnected.PhysicalWidth,
                        PhysicalHeight = reconnected.PhysicalHeight,
                    };
                }
                _connectionState.SetConnected(reconnected.Serial, reconnected.Width, reconnected.Height);
                SetStatus($"Device session reconnected: {reconnected.Serial} ({reconnected.Width}x{reconnected.Height})");
                _logger.Log($"Device session {active.SessionGeneration} reconnected as {reconnected.SessionGeneration}.", LogEntryKind.Success);
                break;

            case DeviceDisconnectedEvent disconnected:
                if (_activeDeviceIdentity is not { } identity
                    || identity.SessionGeneration != disconnected.SessionGeneration)
                    return;

                _activeDeviceIdentity = null;
                _currentDevice = null;
                _connectionState.SetDisconnected();
                CancelCurrentOperation();
                SetStatus($"Device disconnected: {identity.Serial}.");
                _logger.Log(
                    $"Device session {disconnected.SessionGeneration} disconnected ({disconnected.ErrorCode}).",
                    LogEntryKind.Warning);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelCurrentOperation();
        _umaService.ConnectionEventReceived -= OnCoreConnectionEvent;
        await _umaService.DisposeAsync().ConfigureAwait(false);
    }
}
