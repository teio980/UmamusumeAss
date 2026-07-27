using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Umamusume.CoreBridge;

public sealed class UmaService : IUmaService
{
    private readonly object _lifecycleLock = new();
    private readonly object _operationLock = new();
    private readonly IUmaNativeApi _native;
    private readonly IEventDispatcher _dispatcher;
    private readonly UmaApiCallback _nativeCallback;
    private SafeUmaHandle? _handle;
    private OperationState? _startingOperation;
    private OperationState? _activeOperation;
    private bool _initializing;
    private bool _disposed;

    public UmaService(IEventDispatcher dispatcher)
        : this(new UmaCoreBridgeNative(), dispatcher)
    {
    }

    internal UmaService(IUmaNativeApi native, IEventDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _native = native;
        _dispatcher = dispatcher;
        _nativeCallback = OnNativeCallback;
    }

    public string? CoreVersion { get; private set; }

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

        try
        {
            Directory.CreateDirectory(canonicalAppDataDir);
            RequireSuccess(_native.SetUserDir(canonicalAppDataDir), "UmaSetUserDir");
            RequireSuccess(_native.LoadResource(canonicalBaseDir), "UmaLoadResource");

            string version = _native.GetVersion();
            if (string.IsNullOrWhiteSpace(version))
            {
                throw BridgeFailure("The native core returned an empty version.");
            }

            SafeUmaHandle handle = _native.Create(_nativeCallback, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw BridgeFailure("UmaCreate returned an invalid handle.");
            }

            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                _handle = handle;
                CoreVersion = version;
            }

            return Task.CompletedTask;
        }
        finally
        {
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
        SafeUmaHandle handle = GetInitializedHandle();
        cancellationToken.ThrowIfCancellationRequested();

        OperationState? operation = null;
        operation = new OperationState(raw => RouteCallback(operation!, raw));
        lock (_operationLock)
        {
            if (_startingOperation is not null || _activeOperation is not null)
            {
                return Task.FromException<ConnectionTerminalEvent>(
                    BridgeFailure("A native operation is already active."));
            }

            _startingOperation = operation;
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
            start = _native.Connect(handle, adbPath, serial, profile);
        }
        catch (Exception exception)
        {
            ResolveStartFailure(operation, "UmaConnectAsync threw before returning a start result.", exception);
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
                ? $"UmaConnectAsync rejected the start with error code {start.ErrorCode}."
                : "UmaConnectAsync returned a nonzero operation ID with an error.";
            ResolveRejectedStart(operation, message);
            return operation.Completion.Task;
        }

        if (start.OperationId == 0)
        {
            ResolveRejectedStart(operation, "UmaConnectAsync accepted a start with operation ID zero.");
            return operation.Completion.Task;
        }

        lock (_operationLock)
        {
            operation.OperationId = start.OperationId;
            _activeOperation = operation;
        }

        operation.Buffer.Bind(start.OperationId);
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
    {
        SafeUmaHandle? handle;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            handle = _handle;
            _handle = null;
        }

        handle?.Dispose();
        return ValueTask.CompletedTask;
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
        try
        {
            connectionEvent = CallbackParser.Parse(raw);
        }
        catch (CallbackProtocolException exception)
        {
            FailOperation(
                operation,
                new ManagedBridgeException(exception.Category, exception.OperationId, exception.Message, exception));
            return;
        }

        CancellationTokenRegistration registration = default;
        bool terminal = false;
        BridgeDiagnostic? rejected = null;
        lock (_operationLock)
        {
            if (operation.Terminal)
            {
                rejected = Diagnostic(
                    DiagnosticCategory.LateEvent,
                    connectionEvent.OperationId,
                    "A callback arrived after the operation was terminal.");
            }
            else if (operation.OperationId != connectionEvent.OperationId)
            {
                rejected = Diagnostic(
                    DiagnosticCategory.UnknownEvent,
                    connectionEvent.OperationId,
                    "The callback operation ID does not match the active operation.");
            }
            else if (connectionEvent is ConnectionStartedEvent)
            {
                if (operation.Started)
                {
                    rejected = Diagnostic(
                        DiagnosticCategory.NativeContractViolation,
                        connectionEvent.OperationId,
                        "ConnectionStarted was emitted more than once.");
                }
                else
                {
                    operation.Started = true;
                }
            }
            else if (!operation.Started)
            {
                rejected = Diagnostic(
                    DiagnosticCategory.NativeContractViolation,
                    connectionEvent.OperationId,
                    "A connection event arrived before ConnectionStarted.");
            }
            else if (connectionEvent is ConnectionTerminalEvent)
            {
                operation.Terminal = true;
                terminal = true;
                registration = operation.CancellationRegistration;
                if (ReferenceEquals(_activeOperation, operation))
                {
                    _activeOperation = null;
                }
            }
        }

        if (rejected is not null)
        {
            if (rejected.Category == DiagnosticCategory.NativeContractViolation
                && operation.OperationId == connectionEvent.OperationId)
            {
                FailOperation(
                    operation,
                    new ManagedBridgeException(rejected.Category, rejected.OperationId, rejected.Message));
            }
            else
            {
                SafePublishDiagnostic(rejected);
            }

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
                connectionEvent.OperationId,
                $"Connection event dispatch failed: {exception.GetType().Name}."));
        }

        if (terminal)
        {
            registration.Dispose();
            operation.Completion.TrySetResult((ConnectionTerminalEvent)connectionEvent);
        }
    }

    private void ResolveRejectedStart(OperationState operation, string message)
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

        operation.Completion.TrySetException(BridgeFailure(message));
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
    }

    private void RequestCancellation(OperationState operation)
    {
        lock (_operationLock)
        {
            if (operation.Terminal)
            {
                return;
            }

            operation.CancellationRequested = true;
        }

        TryIssueCancellation(operation);
    }

    private void TryIssueCancellation(OperationState operation)
    {
        SafeUmaHandle handle;
        try
        {
            handle = GetInitializedHandle();
        }
        catch (ObjectDisposedException)
        {
            return;
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string ValidateBaseDirectory(string path)
    {
        string canonicalPath = ValidateAbsolutePath(path, nameof(path));
        return Directory.Exists(canonicalPath)
            ? canonicalPath
            : throw new DirectoryNotFoundException($"Application base directory does not exist: {canonicalPath}");
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

    private sealed class OperationState
    {
        internal OperationState(Action<RawCallback> route)
        {
            Buffer = new StartOperationBuffer(route);
            Completion = new TaskCompletionSource<ConnectionTerminalEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal StartOperationBuffer Buffer { get; }
        internal TaskCompletionSource<ConnectionTerminalEvent> Completion { get; }
        internal CancellationTokenRegistration CancellationRegistration { get; set; }
        internal ulong? OperationId { get; set; }
        internal bool Started { get; set; }
        internal bool Terminal { get; set; }
        internal bool CancellationRequested { get; set; }
        internal bool CancellationSent { get; set; }
    }
}
