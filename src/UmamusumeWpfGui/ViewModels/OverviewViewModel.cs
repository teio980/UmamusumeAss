using System.ComponentModel;
using System.Runtime.CompilerServices;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels;

public sealed class OverviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IConnectionStateService _connectionState;
    private readonly IUmaService _umaService;
    private bool _disposed;

    public OverviewViewModel(IConnectionStateService connectionState, IUmaService umaService)
    {
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(umaService);
        _connectionState = connectionState;
        _umaService = umaService;
        _connectionState.StateChanged += OnStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionState State => _connectionState.State;
    public LastVerifiedConnection? LastVerifiedConnection => _connectionState.LastVerifiedConnection;
    public string CoreVersion => _umaService.CoreVersion ?? string.Empty;
    public bool HasVerifiedConnection => LastVerifiedConnection is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectionState.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(LastVerifiedConnection));
        OnPropertyChanged(nameof(HasVerifiedConnection));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
