using System;
using System.Linq;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class LogViewModelTests
{
    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Creates a fake UmaService and a LogViewModel wired to it.
    /// </summary>
    private static (FakeUmaService Service, LogViewModel ViewModel) CreateFixture()
    {
        var service = new FakeUmaService();
        var vm = new LogViewModel(service);
        return (service, vm);
    }

    // ================================================================
    // Initial state
    // ================================================================

    [Fact]
    public void Initial_Entries_IsEmpty()
    {
        var (_, vm) = CreateFixture();
        Assert.Empty(vm.Entries);
    }

    // ================================================================
    // Event mapping — ConnectionStarted
    // ================================================================

    [Fact]
    public void OnConnectionStarted_AddsOneEntry()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionStarted(operationId: 1);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void OnConnectionStarted_EntryTypeIsConnectionStarted()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionStarted(operationId: 42);

        Assert.Equal("ConnectionStarted", vm.Entries[0].Type);
    }

    [Fact]
    public void OnConnectionStarted_EntryKindIsInfo()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionStarted(operationId: 1);

        Assert.Equal(LogEntryKind.Info, vm.Entries[0].Kind);
    }

    // ================================================================
    // Event mapping — ConnectionProgress
    // ================================================================

    [Fact]
    public void OnConnectionProgress_AddsOneEntry()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionProgress(operationId: 1, ConnectionPhase.AdbDevices);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void OnConnectionProgress_EntryTypeIsConnectionProgress()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionProgress(operationId: 1, ConnectionPhase.BootPoll);

        Assert.Equal("ConnectionProgress", vm.Entries[0].Type);
    }

    [Fact]
    public void OnConnectionProgress_DetailsIsValidJson()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionProgress(operationId: 1, ConnectionPhase.AndroidId);

        var details = vm.Entries[0].Details;
        Assert.StartsWith("{", details, StringComparison.Ordinal);
        Assert.EndsWith("}", details, StringComparison.Ordinal);
        Assert.Contains("OperationId", details, StringComparison.Ordinal);
    }

    [Fact]
    public void OnConnectionProgress_EntryKindIsInfo()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionProgress(operationId: 1, ConnectionPhase.AdbGetState);

        Assert.Equal(LogEntryKind.Info, vm.Entries[0].Kind);
    }

    // ================================================================
    // Event mapping — ConnectionSucceeded
    // ================================================================

    [Fact]
    public void OnConnectionSucceeded_AddsOneEntry()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionSucceeded(
            operationId: 1, serial: "127.0.0.1:5555", androidId: "abcd1234",
            androidVersion: "14", width: 1080, height: 1920);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void OnConnectionSucceeded_EntryTypeIsConnectionSucceeded()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 100, height: 200);

        Assert.Equal("ConnectionSucceeded", vm.Entries[0].Type);
    }

    [Fact]
    public void OnConnectionSucceeded_EntryKindIsSuccess()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 100, height: 200);

        Assert.Equal(LogEntryKind.Success, vm.Entries[0].Kind);
    }

    [Fact]
    public void OnConnectionSucceeded_DetailsContainsSerial()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionSucceeded(
            operationId: 1, serial: "192.168.1.100:5555", androidId: "id",
            androidVersion: "12", width: 100, height: 200);

        Assert.Contains("192.168.1.100:5555", vm.Entries[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public void OnConnectionSucceeded_DetailsContainsResolution()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 1920, height: 1080);

        var details = vm.Entries[0].Details;
        Assert.Contains("1920", details, StringComparison.Ordinal);
        Assert.Contains("1080", details, StringComparison.Ordinal);
    }

    // ================================================================
    // Event mapping — ConnectionFailed
    // ================================================================

    [Fact]
    public void OnConnectionFailed_AddsOneEntry()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionFailed(
            operationId: 1, errorCode: ConnectionErrorCode.DeviceUnavailable,
            phase: "adb_devices", message: "device not found");

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void OnConnectionFailed_EntryTypeIsConnectionFailed()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionFailed(
            operationId: 1, errorCode: ConnectionErrorCode.CommandTimedOut,
            phase: "boot_poll", message: "timed out");

        Assert.Equal("ConnectionFailed", vm.Entries[0].Type);
    }

    [Fact]
    public void OnConnectionFailed_EntryKindIsFailure()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionFailed(
            operationId: 1, errorCode: ConnectionErrorCode.DeviceOffline,
            phase: "adb_devices", message: "device is offline");

        Assert.Equal(LogEntryKind.Failure, vm.Entries[0].Kind);
    }

    [Fact]
    public void OnConnectionFailed_DetailsContainsErrorMessage()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionFailed(
            operationId: 1, errorCode: ConnectionErrorCode.DeviceUnauthorized,
            phase: "adb_devices", message: "unauthorized device");

        Assert.Contains("unauthorized device", vm.Entries[0].Details, StringComparison.Ordinal);
    }

    // ================================================================
    // Multiple events
    // ================================================================

    [Fact]
    public void MultipleEvents_AllAdded()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionStarted(operationId: 1);
        service.FireConnectionProgress(operationId: 1, ConnectionPhase.AdbDevices);
        service.FireConnectionProgress(operationId: 1, ConnectionPhase.BootPoll);
        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 100, height: 200);

        Assert.Equal(4, vm.Entries.Count);
    }

    [Fact]
    public void MultipleEvents_OrderPreserved()
    {
        var (service, vm) = CreateFixture();

        service.FireConnectionStarted(operationId: 1);
        service.FireConnectionProgress(operationId: 1, ConnectionPhase.AdbDevices);
        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 100, height: 200);

        Assert.Equal("ConnectionStarted", vm.Entries[0].Type);
        Assert.Equal("ConnectionProgress", vm.Entries[1].Type);
        Assert.Equal("ConnectionSucceeded", vm.Entries[2].Type);
    }

    // ================================================================
    // 500-entry cap
    // ================================================================

    [Fact]
    public void EventCap_At500Entries_DropsOldest()
    {
        var (service, vm) = CreateFixture();

        // Fire 501 events — the 1st should be dropped
        for (int i = 0; i < 501; i++)
        {
            service.FireConnectionStarted(operationId: (ulong)i);
        }

        Assert.Equal(500, vm.Entries.Count);
        // The first entry should be the 2nd event (index 1, OperationId=1), not the 1st (index 0)
        Assert.Contains("\"OperationId\":1", vm.Entries[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public void EventCap_Under500_NoDrop()
    {
        var (service, vm) = CreateFixture();

        for (int i = 0; i < 499; i++)
        {
            service.FireConnectionStarted(operationId: (ulong)i);
        }

        Assert.Equal(499, vm.Entries.Count);
    }

    [Fact]
    public void EventCap_Exactly500_NoDrop()
    {
        var (service, vm) = CreateFixture();

        for (int i = 0; i < 500; i++)
        {
            service.FireConnectionStarted(operationId: (ulong)i);
        }

        Assert.Equal(500, vm.Entries.Count);
    }

    // ================================================================
    // Timestamp is set on each entry
    // ================================================================

    [Fact]
    public void EachEntry_HasTimestamp()
    {
        var (service, vm) = CreateFixture();
        var before = DateTimeOffset.UtcNow;

        service.FireConnectionStarted(operationId: 1);

        Assert.InRange(vm.Entries[0].Timestamp, before, DateTimeOffset.UtcNow);
    }

    // ================================================================
    // Dispose — cleanup and event leakage
    // ================================================================

    [Fact]
    public void Dispose_UnsubscribesFromService()
    {
        var (service, vm) = CreateFixture();

        vm.Dispose();
        service.FireConnectionStarted(operationId: 1);

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        var (_, vm) = CreateFixture();

        vm.Dispose();
        // Second dispose should be idempotent
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void AfterDispose_EventsAreIgnored()
    {
        var (service, vm) = CreateFixture();

        vm.Dispose();

        service.FireConnectionStarted(operationId: 1);
        service.FireConnectionSucceeded(
            operationId: 1, serial: "s1", androidId: "id",
            androidVersion: "12", width: 100, height: 200);
        service.FireConnectionFailed(
            operationId: 2, errorCode: ConnectionErrorCode.Canceled,
            phase: "adb_devices", message: "canceled");

        Assert.Empty(vm.Entries);
    }

    // ================================================================
    // FakeUmaService
    // ================================================================

    /// <summary>
    /// A fake IUmaService that can fire ConnectionEventReceived
    /// for testing LogViewModel subscription behavior.
    /// </summary>
    public sealed class FakeUmaService : IUmaService
    {
        public string? CoreVersion => "1.0.0";

        public event Action<ConnectionEvent>? ConnectionEventReceived;
#pragma warning disable CS0067 // DiagnosticReceived is not raised in tests
        public event Action<BridgeDiagnostic>? DiagnosticReceived;
#pragma warning restore CS0067

        public void FireConnectionStarted(ulong operationId)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionStartedEvent(operationId));
        }

        public void FireConnectionProgress(ulong operationId, ConnectionPhase phase)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionProgressEvent(operationId, phase));
        }

        public void FireConnectionSucceeded(
            ulong operationId,
            string serial,
            string androidId,
            string androidVersion,
            int width,
            int height)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionSucceededEvent(
                    operationId, serial, androidId, androidVersion,
                    width, height, width, height,
                    DisplaySizeSource.Physical));
        }

        public void FireConnectionFailed(
            ulong operationId,
            ConnectionErrorCode errorCode,
            string phase,
            string message)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionFailedEvent(operationId, errorCode, phase, message, 1, 1));
        }

        public Task InitializeAsync(
            string appBaseDir,
            string appDataDir,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath,
            string serial,
            string profile,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ConnectionTerminalEvent>(
                new InvalidOperationException("Not expected in log tests."));

        public Task CancelOperationAsync(
            ulong operationId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
