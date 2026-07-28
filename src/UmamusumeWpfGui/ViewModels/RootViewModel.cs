namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Root view model that composes LogViewModel and SettingsViewModel
/// as child view models for the main window's two-tab layout.
/// Ownership of child VMs is transferred to this instance — call Dispose
/// to clean up children.
/// </summary>
public sealed class RootViewModel : IDisposable
{
    private readonly OverviewViewModel _overviewViewModel;
    private bool _disposed;
    private int _selectedNavigationIndex;

    /// <summary>
    /// Creates the RootViewModel with child view models injected via DI.
    /// </summary>
    /// <param name="logViewModel">Log tab view model.</param>
    /// <param name="settingsViewModel">Settings tab view model.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is null.</exception>
    public RootViewModel(
        OverviewViewModel overviewViewModel,
        LogViewModel logViewModel,
        SettingsViewModel settingsViewModel)
    {
        ArgumentNullException.ThrowIfNull(overviewViewModel);
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(settingsViewModel);

        _overviewViewModel = overviewViewModel;
        LogViewModel = logViewModel;
        SettingsViewModel = settingsViewModel;
        NavigationItems =
        [
            new("NavOverview", 0),
            new("TabLog", 1),
            new("TabSettings", 2),
        ];
        ActiveContent = _overviewViewModel;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<Models.RootNavigationItem> NavigationItems { get; }

    public int SelectedNavigationIndex
    {
        get => _selectedNavigationIndex;
        set
        {
            if (value is < 0 or > 2 || _selectedNavigationIndex == value)
                return;
            _selectedNavigationIndex = value;
            ActiveContent = value switch
            {
                0 => _overviewViewModel,
                1 => LogViewModel,
                _ => SettingsViewModel,
            };
            PropertyChanged?.Invoke(this, new(nameof(SelectedNavigationIndex)));
            PropertyChanged?.Invoke(this, new(nameof(ActiveContent)));
        }
    }

    public object ActiveContent { get; private set; }

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
        _overviewViewModel.Dispose();
        LogViewModel.Dispose();
        SettingsViewModel.Dispose();
    }
}
