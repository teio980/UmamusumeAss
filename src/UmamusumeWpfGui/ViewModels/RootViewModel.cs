namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Root view model that composes LogViewModel and SettingsViewModel
/// as child view models for the main window's two-tab layout.
/// Ownership of child VMs is transferred to this instance — call Dispose
/// to clean up children.
/// </summary>
public sealed class RootViewModel : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Creates the RootViewModel with child view models injected via DI.
    /// </summary>
    /// <param name="logViewModel">Log tab view model.</param>
    /// <param name="settingsViewModel">Settings tab view model.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is null.</exception>
    public RootViewModel(
        LogViewModel logViewModel,
        SettingsViewModel settingsViewModel)
    {
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(settingsViewModel);

        LogViewModel = logViewModel;
        SettingsViewModel = settingsViewModel;
    }

    /// <summary>Log tab view model — displays connection callback events.</summary>
    public LogViewModel LogViewModel { get; }

    /// <summary>Settings tab view model — connection config, language, system info.</summary>
    public SettingsViewModel SettingsViewModel { get; }

    /// <summary>
    /// Disposes child view models. Idempotent — safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LogViewModel.Dispose();
        SettingsViewModel.Dispose();
    }
}
