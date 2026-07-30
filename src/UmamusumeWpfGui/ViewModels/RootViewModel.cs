namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Root view model that composes the overview, grass, log and settings pages
/// for the main window navigation shell.
/// Ownership of child VMs is transferred to this instance — call Dispose
/// to clean up children.
/// </summary>
public sealed class RootViewModel : IDisposable, System.ComponentModel.INotifyPropertyChanged
{
    private readonly OverviewViewModel _overviewViewModel;
    private readonly GrassViewModel _grassViewModel;
    private bool _disposed;
    private int _selectedNavigationIndex = 3;

    /// <summary>
    /// Creates the RootViewModel with child view models injected via DI.
    /// </summary>
    /// <param name="logViewModel">Log tab view model.</param>
    /// <param name="settingsViewModel">Settings tab view model.</param>
    /// <param name="grassViewModel">MAA-inspired grass tab view model.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is null.</exception>
    public RootViewModel(
        OverviewViewModel overviewViewModel,
        LogViewModel logViewModel,
        SettingsViewModel settingsViewModel,
        GrassViewModel grassViewModel)
    {
        ArgumentNullException.ThrowIfNull(overviewViewModel);
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(grassViewModel);

        _overviewViewModel = overviewViewModel;
        LogViewModel = logViewModel;
        SettingsViewModel = settingsViewModel;
        _grassViewModel = grassViewModel;
        NavigationItems =
        [
            new("TabHachimi", 3),
            new("NavOverview", 0),
            new("TabLog", 1),
            new("TabSettings", 2),
        ];
        ActiveContent = _grassViewModel;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<Models.RootNavigationItem> NavigationItems { get; }

    public int SelectedNavigationIndex
    {
        get => _selectedNavigationIndex;
        set
        {
            if (value is < 0 or > 3 || _selectedNavigationIndex == value)
                return;
            _selectedNavigationIndex = value;
            ActiveContent = value switch
            {
                0 => _overviewViewModel,
                1 => LogViewModel,
                2 => SettingsViewModel,
                _ => _grassViewModel,
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

    /// <summary>MAA-inspired grass/task configuration page.</summary>
    public GrassViewModel GrassViewModel => _grassViewModel;

    /// <summary>
    /// Disposes child view models. Idempotent — safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _overviewViewModel.Dispose();
        _grassViewModel.Dispose();
        LogViewModel.Dispose();
        SettingsViewModel.Dispose();
    }
}
