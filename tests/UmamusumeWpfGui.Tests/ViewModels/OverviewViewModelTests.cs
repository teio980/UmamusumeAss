using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class OverviewViewModelTests
{
    [Fact]
    public void Constructor_ProjectsExistingConnectionStateAndCoreVersion()
    {
        var connectionState = new FakeConnectionStateService();
        var umaService = new FakeUmaService("1.2.3");

        using var viewModel = new OverviewViewModel(connectionState, umaService);

        Assert.Equal(ConnectionState.Disconnected, viewModel.State);
        Assert.Equal("1.2.3", viewModel.CoreVersion);
        Assert.False(viewModel.HasVerifiedConnection);
    }

    [Fact]
    public void StateChanged_RefreshesConnectionProjection()
    {
        var connectionState = new FakeConnectionStateService();
        using var viewModel = new OverviewViewModel(connectionState, new FakeUmaService("1.0"));
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName ?? string.Empty);

        connectionState.UpdateLastVerified(new LastVerifiedConnection(
            "adb", "serial", "android", "14", 1080, 1920, 1080, 1920, DateTimeOffset.UtcNow));
        connectionState.SetState(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, viewModel.State);
        Assert.True(viewModel.HasVerifiedConnection);
        Assert.NotNull(viewModel.LastVerifiedConnection);
        Assert.Contains(nameof(OverviewViewModel.State), changed);
        Assert.Contains(nameof(OverviewViewModel.HasVerifiedConnection), changed);
    }

    [Fact]
    public void LastVerifiedChange_RefreshesConnectionProjection()
    {
        var connectionState = new FakeConnectionStateService();
        using var viewModel = new OverviewViewModel(connectionState, new FakeUmaService("1.0"));
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName ?? string.Empty);

        connectionState.UpdateLastVerified(new LastVerifiedConnection(
            "adb", "serial", "android", "14", 1080, 1920, 1080, 1920, DateTimeOffset.UtcNow));

        Assert.Contains(nameof(OverviewViewModel.LastVerifiedConnection), changed);
        Assert.Contains(nameof(OverviewViewModel.HasVerifiedConnection), changed);
    }

    [Fact]
    public void ClearingLastVerified_RefreshesConnectionProjection()
    {
        var connectionState = new FakeConnectionStateService();
        connectionState.UpdateLastVerified(new LastVerifiedConnection(
            "adb", "serial", "android", "14", 1080, 1920, 1080, 1920, DateTimeOffset.UtcNow));
        using var viewModel = new OverviewViewModel(connectionState, new FakeUmaService("1.0"));
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName ?? string.Empty);

        connectionState.ClearLastVerified();

        Assert.Contains(nameof(OverviewViewModel.LastVerifiedConnection), changed);
        Assert.Contains(nameof(OverviewViewModel.HasVerifiedConnection), changed);
        Assert.False(viewModel.HasVerifiedConnection);
    }

    private sealed class FakeUmaService(string coreVersion) : IUmaService
    {
        public string? CoreVersion { get; } = coreVersion;
        public string? ResourcePath => null;
#pragma warning disable CS0067
        public event Action<ConnectionEvent>? ConnectionEventReceived;
        public event Action<BridgeDiagnostic>? DiagnosticReceived;
#pragma warning restore CS0067
        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionTerminalEvent> ConnectAsync(string adbPath, string serial, string profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnectionStateService : IConnectionStateService
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public LastVerifiedConnection? LastVerifiedConnection { get; private set; }
        public ControlSessionSnapshot? ControlSession => null;
        public event EventHandler? StateChanged;
        public void SetState(ConnectionState newState)
        {
            if (State == newState) return;
            State = newState;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void UpdateLastVerified(LastVerifiedConnection record)
        {
            LastVerifiedConnection = record;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void ClearLastVerified()
        {
            LastVerifiedConnection = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
