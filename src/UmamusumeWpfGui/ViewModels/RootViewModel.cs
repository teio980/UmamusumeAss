namespace UmamusumeWpfGui.ViewModels;







public sealed class RootViewModel : IDisposable, System.ComponentModel.INotifyPropertyChanged
{
    private readonly OverviewViewModel _overviewViewModel;
    private readonly GrassViewModel _grassViewModel;
    private bool _disposed;
    private int _selectedNavigationIndex = 3;








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


    public LogViewModel LogViewModel { get; }


    public OverviewViewModel OverviewViewModel => _overviewViewModel;


    public SettingsViewModel SettingsViewModel { get; }


    public GrassViewModel GrassViewModel => _grassViewModel;




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
