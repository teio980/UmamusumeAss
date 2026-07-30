using Umamusume.CoreBridge.Tests.Fakes;
using System.Text.Json;

namespace Umamusume.CoreBridge.Tests;

public sealed class UmaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uma-bridge-{Guid.NewGuid():N}");

    public UmaServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InitializeCallsNativeApiInRequiredOrder()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string appData = Path.Combine(_root, "app-data");

        await service.InitializeAsync(_root, appData);

        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
        Assert.Equal("0.1.0", service.CoreVersion);
        Assert.Equal(Path.Combine(_root, "resource"), service.ResourcePath);
        Assert.True(Directory.Exists(appData));
    }

    [Theory]
    [InlineData("relative", true)]
    [InlineData("relative", false)]
    public async Task InitializeRejectsRelativePaths(string path, bool useAsBase)
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string basePath = useAsBase ? path : _root;
        string appData = useAsBase ? Path.Combine(_root, "data") : path;

        await Assert.ThrowsAsync<ArgumentException>(() => service.InitializeAsync(basePath, appData));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsMissingBaseDirectory()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.InitializeAsync(Path.Combine(_root, "missing"), Path.Combine(_root, "data")));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeStopsWhenSetUserDirFails()
    {
        var native = new FakeUmaNativeApi { SetUserDirResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeStopsWhenLoadResourceFails()
    {
        var native = new FakeUmaNativeApi { LoadResourceResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeRejectsEmptyVersion()
    {
        var native = new FakeUmaNativeApi { Version = "" };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion"], native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsInvalidHandle()
    {
        var native = new FakeUmaNativeApi { CreateInvalidHandle = true };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
    }

    [Fact]
    public async Task InitializeCannotRunTwice()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize(service));
    }

    [Fact]
    public async Task DisposeRacingWithCreateDestroysTheUnassignedHandle()
    {
        using var createReachedReturn = new ManualResetEventSlim();
        using var releaseCreate = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            BeforeCreateReturn = () =>
            {
                createReachedReturn.Set();
                releaseCreate.Wait();
            },
        };
        var service = CreateService(native);
        Task initialization = Task.Run(() => Initialize(service));
        Assert.True(createReachedReturn.Wait(TimeSpan.FromSeconds(5)));

        await service.DisposeAsync();
        releaseCreate.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await initialization);

        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task ConnectBeforeInitializationIsRejected()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task ConnectReplaysCallbacksDeliveredBeforeNativeReturn()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);
        var events = new List<ConnectionEvent>();
        service.ConnectionEventReceived += events.Add;

        ConnectionTerminalEvent result = await service.ConnectAsync("adb.exe", "serial", "General");

        Assert.IsType<ConnectionSucceededEvent>(result);
        Assert.Collection(
            events,
            item => Assert.IsType<ConnectionStartedEvent>(item),
            item => Assert.IsType<ConnectionProgressEvent>(item),
            item => Assert.IsType<ConnectionSucceededEvent>(item));
    }

    [Fact]
    public async Task ConnectRoutesCallbacksDeliveredAfterBinding()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);

        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");
        EmitSuccess(native, 42);

        Assert.IsType<ConnectionSucceededEvent>(await operation);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(42, 11)]
    [InlineData(0, 11)]
    public async Task ConnectRejectsInvalidOrFailedStartResults(ulong operationId, int errorCode)
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(operationId, errorCode) };
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task ConnectDiagnosesIllegalCallbackFromRejectedStart()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(0, 11) };
        native.BeforeConnectReturn = () => native.Emit(1, Started(99));
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));

        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.NativeContractViolation);
    }

    [Fact]
    public async Task WrongOperationIdDoesNotCompleteCurrentOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        EmitSuccess(native, 99);
        Assert.False(operation.IsCompleted);
        EmitSuccess(native, 42);

        Assert.IsType<ConnectionSucceededEvent>(await operation);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.UnknownEvent);
    }

    [Fact]
    public async Task MalformedCallbackFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.Emit(2, Progress(42));

        await Assert.ThrowsAsync<ManagedBridgeException>(async () => await operation);
    }

    [Fact]
    public async Task NullCallbackPointerFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.EmitNull(2);

        await Assert.ThrowsAsync<ManagedBridgeException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OversizedCallbackFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.Emit(2, new string('a', CallbackParser.MaxCallbackJsonBytes + 1));

        await Assert.ThrowsAsync<ManagedBridgeException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SecondConnectIsRejectedWhileFirstIsActive()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> first = service.ConnectAsync("adb.exe", "serial", "General");

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
        EmitSuccess(native, 42);
        await first;

        Assert.Equal(1, native.Calls.Count(call => call == "Connect"));
    }

    [Fact]
    public async Task DuplicateAndLateTerminalCallbacksDoNotReplaceResult()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        EmitSuccess(native, 42);
        ConnectionTerminalEvent first = await operation;
        native.Emit(4, Failed(42, 7));

        Assert.IsType<ConnectionSucceededEvent>(first);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.LateEvent);
    }

    [Fact]
    public async Task TerminalEventHandlerCannotStartAnotherOperationBeforeCallbackCleanup()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent>? reentrant = null;
        service.ConnectionEventReceived += connectionEvent =>
        {
            if (connectionEvent is ConnectionTerminalEvent)
            {
                reentrant = service.ConnectAsync("adb.exe", "serial", "General");
            }
        };
        Task<ConnectionTerminalEvent> first = service.ConnectAsync("adb.exe", "serial", "General");

        EmitSuccess(native, 42);
        await first;

        Assert.NotNull(reentrant);
        await Assert.ThrowsAsync<ManagedBridgeException>(async () => await reentrant);
        Assert.Equal(1, native.Calls.Count(call => call == "Connect"));
    }

    [Fact]
    public async Task TokenCancellationDuringNativeStartIsSentAfterBinding()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        native.BeforeConnectReturn = () =>
        {
            entered.Set();
            release.Wait();
        };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        using var cancellation = new CancellationTokenSource();

        Task<Task<ConnectionTerminalEvent>> start = Task.Factory.StartNew(
            () => service.ConnectAsync("adb.exe", "serial", "General", cancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        release.Set();
        Task<ConnectionTerminalEvent> operation = await start;

        var result = Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal(ConnectionErrorCode.Canceled, result.ErrorCode);
        Assert.Equal([42UL], native.CanceledOperationIds);
    }

    [Fact]
    public async Task ManualCancellationUsesBoundOperationIdOnce()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        await service.CancelOperationAsync(42);

        Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal([42UL], native.CanceledOperationIds);
    }

    [Fact]
    public async Task CancellationRegistrationIsDisposedAfterTerminalEvent()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        using var cancellation = new CancellationTokenSource();
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync(
            "adb.exe", "serial", "General", cancellation.Token);
        EmitSuccess(native, 42);
        await operation;

        cancellation.Cancel();

        Assert.Empty(native.CanceledOperationIds);
    }

    [Fact]
    public async Task DisposeDestroysIdleHandleExactlyOnce()
    {
        var native = new FakeUmaNativeApi();
        var service = CreateService(native);
        await Initialize(service);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task DisposeCancelsActiveOperationBeforeDestroy()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        await service.DisposeAsync();

        Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal([42UL], native.CanceledOperationIds);
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task DisposeAbandonsHandleWhenTerminalNeverArrives()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        var service = CreateService(native, TimeSpan.Zero);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        _ = service.ConnectAsync("adb.exe", "serial", "General");

        await service.DisposeAsync();

        Assert.Equal(0, native.DestroyCalls);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.FatalShutdownTimeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task DisposeReturnsWhenDestroyRemainsBlocked()
    {
        using var destroyEntered = new ManualResetEventSlim();
        using var continueDestroy = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            DestroyEntered = destroyEntered,
            ContinueDestroy = continueDestroy,
        };
        var service = CreateService(native, TimeSpan.Zero);
        await Initialize(service);

        await service.DisposeAsync();
        Assert.True(destroyEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, native.DestroyCalls);
        continueDestroy.Set();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task Initialize(UmaService service) =>
        service.InitializeAsync(_root, Path.Combine(_root, "app-data"));

    private static UmaService CreateService(FakeUmaNativeApi native, TimeSpan? shutdownTimeout = null) =>
        new(native, new InlineEventDispatcher(), shutdownTimeout ?? TimeSpan.FromSeconds(10), TimeProvider.System);

    private static void EmitSuccess(FakeUmaNativeApi native, ulong operationId)
    {
        native.Emit(1, Started(operationId));
        native.Emit(2, Progress(operationId, includePhase: true));
        native.Emit(3, Succeeded(operationId));
    }

    private static string Started(ulong operationId) =>
        Envelope(operationId, "ConnectionStarted", new { });

    private static string Progress(ulong operationId, bool includePhase = false) => includePhase
        ? Envelope(operationId, "ConnectionProgress", new { phase = "adb_devices" })
        : Envelope(operationId, "ConnectionProgress", new { });

    private static string Succeeded(ulong operationId) =>
        Envelope(operationId, "ConnectionSucceeded", new
        {
            serial = "serial",
            android_id = "0123456789abcdef",
            android_version = "14",
            width = 1080,
            height = 1920,
            physical_width = 1080,
            physical_height = 1920,
            size_source = "physical",
        });

    private static string Failed(ulong operationId, int errorCode) =>
        Envelope(operationId, "ConnectionFailed", new
        {
            error_code = errorCode,
            phase = "cancel",
            message = "failed",
            attempt = 1,
            max_attempts = 1,
        });

    private static string Envelope(ulong operationId, string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            operation_id = operationId,
            type,
            payload,
        });

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
