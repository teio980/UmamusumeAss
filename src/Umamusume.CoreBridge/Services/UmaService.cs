using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Umamusume.CoreBridge;

public sealed class UmaService : IUmaService
{
    private readonly object _lifecycleLock = new();
    private readonly IUmaNativeApi _native;
    private readonly IEventDispatcher _dispatcher;
    private readonly UmaApiCallback _nativeCallback;
    private SafeUmaHandle? _handle;
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
        _ = GetInitializedHandle();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ConnectionTerminalEvent>(
            new NotSupportedException("Connection operation routing is implemented in the next bridge task."));
    }

    public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default)
    {
        _ = GetInitializedHandle();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            new NotSupportedException("Connection cancellation routing is implemented in the next bridge task."));
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
                PublishDiagnostic(new BridgeDiagnostic(
                    DiagnosticCategory.MalformedCallback,
                    DiagnosticSeverity.Error,
                    null,
                    "The native callback JSON pointer was null."));
                return;
            }

            string? json = Marshal.PtrToStringUTF8(detailsJson);
            if (json is null || Encoding.UTF8.GetByteCount(json) > CallbackParser.MaxCallbackJsonBytes)
            {
                PublishDiagnostic(new BridgeDiagnostic(
                    DiagnosticCategory.MalformedCallback,
                    DiagnosticSeverity.Error,
                    null,
                    "The native callback JSON was null or oversized."));
                return;
            }

            _ = new RawCallback(message, json);
            PublishDiagnostic(new BridgeDiagnostic(
                DiagnosticCategory.LateEvent,
                DiagnosticSeverity.Warning,
                null,
                "A callback arrived without an active operation."));
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
}
