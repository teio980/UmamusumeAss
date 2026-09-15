using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Dialogs;
using UmamusumeWpfGui.Views.Dialogs;
using UmamusumeWpfGui.Services.Update;

namespace UmamusumeWpfGui.ViewModels;















public sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{




    private readonly IUmaService _umaService;
    private readonly IConnectionStateService _connectionState;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IWinAdapter _winAdapter;
    private readonly IEmulatorLauncher _emulatorLauncher;
    private readonly IAsyncDelay _asyncDelay;
    private readonly IConnectionHealthMonitor _healthMonitor;
    private readonly IUpdateService? _updateService;
    private readonly IActivityRegistry? _activityRegistry;





    private ConnectionSettings _draft;
    private int _selectedMenuIndex;
    private string _draftAdbPath;
    private string _draftConnectAddress;
    private string _draftConnectConfig;
    private bool _draftAutoDetect;
    private bool _draftAlwaysAutoDetect;
    private bool _draftAutoStartEmulator;
    private string _draftEmulatorExecutablePath;
    private string _draftLanguage;
    private string _selectedLanguage;
    private string _lastDetectedEmulator = string.Empty;
    private string _connectionDiagnostic = string.Empty;
    private CancellationTokenSource? _connectCts;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;
    private CancellationTokenSource? _updateCts;
    private UpdatePlan? _pendingUpdatePlan;
    private UpdateScope _pendingUpdateScope = UpdateScope.Program;
    private StagedUpdate? _stagedUpdate;
    private string _updateStatus = "Updates are ready to check.";
    private UpdateProgress? _updateProgress;










    public SettingsViewModel(
        IUmaService umaService,
        IConnectionStateService connectionState,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IWinAdapter winAdapter,
        IEmulatorLauncher emulatorLauncher,
        IAsyncDelay asyncDelay,
        IConnectionHealthMonitor healthMonitor,
        IUpdateService? updateService = null,
        IActivityRegistry? activityRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(umaService);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(winAdapter);
        ArgumentNullException.ThrowIfNull(emulatorLauncher);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        ArgumentNullException.ThrowIfNull(healthMonitor);

        _umaService = umaService;
        _connectionState = connectionState;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _winAdapter = winAdapter;
        _emulatorLauncher = emulatorLauncher;
        _asyncDelay = asyncDelay;
        _healthMonitor = healthMonitor;
        _updateService = updateService;
        _activityRegistry = activityRegistry;
        _healthMonitor.Failed += OnHealthMonitorFailed;


        _draft = _settingsService.Load();
        // Shop options are global Hachimi settings. Keep their existing
        // model/JSON shape, but host the editor from the sidebar Settings
        // page instead of the Hachimi queue page.
        HachimiShopSettings = new HachimiShopSettingsViewModel(_settingsService);
        _draftAdbPath = _draft.AdbPath?.Trim() ?? string.Empty;
        _draftConnectAddress = NormalizeConnectionAddress(_draft.ConnectAddress);
        _draftConnectConfig = _draft.ConnectConfig;
        _draftAutoDetect = _draft.AutoDetectConnection;
        _draftAlwaysAutoDetect = _draft.AlwaysAutoDetectConnection;
        _draftAutoStartEmulator = _draft.AutoStartEmulator;
        _draftEmulatorExecutablePath = _draft.EmulatorExecutablePath;
        _draftAutoStartEmulatorWaitSeconds = _draft.AutoStartEmulatorWaitSeconds;
        _draftLanguage = _draft.Language;
        _selectedLanguage = _localizationService.CurrentCulture;


        ConnectAddressHistory = new ObservableCollection<string>(_draft.ConnectAddressHistory);


        _connectionState.StateChanged += OnStateChanged;


        RequestCandidateSelection = ShowCandidateSelectionAsync;
        RequestAddressSelection = ShowAddressSelectionAsync;

        ConnectCommand = new RelayCommand(
            _ => { _ = ConnectAsync(); },
            _ => !_disposed && (State is ConnectionState.Disconnected or ConnectionState.Failed));

        CancelConnectCommand = new RelayCommand(
            _ => Cancel(),
            _ => !_disposed && IsOperationInProgress);

        SaveCommand = new RelayCommand(
            _ => SaveSettings(),
            _ => !_disposed);

        DetectAdbConfigCommand = new RelayCommand(
            _ => { _ = AutoDetectEmulatorsAsync(); },
            _ => !_disposed && !IsOperationInProgress);

        DisconnectCommand = new RelayCommand(
            _ => Disconnect(),
            _ => !_disposed && (State is ConnectionState.Connected or ConnectionState.Failed));

        ForgetCommand = new RelayCommand(
            _ => Forget(),
            _ => !_disposed);

        CheckForUpdatesCommand = new RelayCommand(
            _ => _ = CheckForUpdatesAsync(),
            _ => !_disposed && !IsUpdateBusy);
        DownloadUpdateCommand = new RelayCommand(
            _ => _ = DownloadUpdateAsync(),
            _ => !_disposed
                && !IsUpdateBusy
                && _pendingUpdatePlan is not null
                && !IsPendingUpdateCached());
        CancelUpdateCommand = new RelayCommand(
            _ => CancelUpdate(),
            _ => !_disposed && IsUpdateBusy);
        ClearUpdateCacheCommand = new RelayCommand(
            _ => _ = ClearUpdateCacheAsync(),
            _ => _updateService is not null && !_disposed && !IsUpdateBusy);
        InstallUpdateCommand = new RelayCommand(
            _ => _ = InstallUpdateAsync(),
            _ => !_disposed && !IsUpdateBusy && _stagedUpdate is not null);
        SkipUpdateCommand = new RelayCommand(
            _ => SkipUpdate(),
            _ => !_disposed
                && _pendingUpdateScope == UpdateScope.Program
                && _pendingUpdatePlan is not null);

        SetStagedUpdate(_updateService?.RestoreStagedProgram());
        if (_stagedUpdate is not null)
            _updateStatus = $"Version {_stagedUpdate.Manifest.Version} is downloaded and ready to install.";
    }









    public int SelectedMenuIndex
    {
        get => _selectedMenuIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 3);
            if (_selectedMenuIndex == clamped)
                return;
            _selectedMenuIndex = clamped;

            for (int i = 0; i < MenuItems.Count; i++)
            {
                MenuItems[i].IsSelected = i == clamped;
            }

            OnPropertyChanged();
        }
    }






    public string DraftAdbPath
    {
        get => _draftAdbPath;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (_draftAdbPath == value)
                return;
            _draftAdbPath = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    public string DraftConnectAddress
    {
        get => _draftConnectAddress;
        set
        {
            value = NormalizeConnectionAddress(value);
            if (_draftConnectAddress == value)
                return;
            _draftConnectAddress = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private static string NormalizeConnectionAddress(string? value) =>
        (value ?? string.Empty)
            .Replace("：", ":", StringComparison.Ordinal)
            .Replace("；", ":", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();


    public string DraftConnectConfig
    {
        get => _draftConnectConfig;
        set
        {
            var normalized = ConnectionSettings.SupportedConnectConfigs.Contains(
                value,
                StringComparer.Ordinal)
                ? value
                : "General";
            if (_draftConnectConfig == normalized)
                return;

            _draftConnectConfig = normalized;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    public ObservableCollection<string> ConnectConfigOptions { get; } =
        new(ConnectionSettings.SupportedConnectConfigs);


    public bool DraftAutoDetect
    {
        get => _draftAutoDetect;
        set
        {
            if (_draftAutoDetect == value)
                return;
            _draftAutoDetect = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    public bool DraftAlwaysAutoDetect
    {
        get => _draftAlwaysAutoDetect;
        set
        {
            if (_draftAlwaysAutoDetect == value)
                return;
            _draftAlwaysAutoDetect = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool DraftAutoStartEmulator
    {
        get => _draftAutoStartEmulator;
        set
        {
            if (_draftAutoStartEmulator == value)
                return;
            _draftAutoStartEmulator = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string DraftEmulatorExecutablePath
    {
        get => _draftEmulatorExecutablePath;
        set
        {
            if (_draftEmulatorExecutablePath == value)
                return;
            _draftEmulatorExecutablePath = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }





    public string DraftLanguage
    {
        get => _draftLanguage;
        set
        {
            if (_draftLanguage == value)
                return;
            _draftLanguage = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }






    public ConnectionState State => _connectionState.State;


    public bool IsOperationInProgress =>
        _connectionState.State is ConnectionState.Detecting
            or ConnectionState.Connecting
            or ConnectionState.Canceling;

    public string StatusText => !string.IsNullOrEmpty(_connectionDiagnostic)
        ? _connectionDiagnostic
        : _connectionState.State switch
        {
            ConnectionState.Idle => "Disconnected",
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Detecting => "Detecting emulators...",
            ConnectionState.Connecting => "Connecting...",
            ConnectionState.Connected => "Connected",
            ConnectionState.Failed => "Connection failed",
            ConnectionState.Canceling => "Canceling...",
            _ => "Unknown",
        };






    public LastVerifiedConnection? LastVerified => _connectionState.LastVerifiedConnection;


    public ControlSessionSnapshot? ControlSession => _connectionState.ControlSession;






    public ObservableCollection<string> ConnectAddressHistory { get; }

    public HachimiShopSettingsViewModel HachimiShopSettings { get; }






    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value)
                return;

            _selectedLanguage = value;
            OnPropertyChanged();

            _localizationService.SwitchLanguage(value);


            DraftLanguage = _localizationService.CurrentCulture;
        }
    }










    public ObservableCollection<MenuItemViewModel> MenuItems { get; } =
    [
        new("NavConnection", 0),
        new("NavLanguage", 1),
        new("NavSystem", 2),
        new("NavHachimi", 3),
    ];






    public string CoreVersion => _umaService.CoreVersion ?? string.Empty;


    public string ResourcePath => _umaService.ResourcePath ?? string.Empty;


    public string LastDetectedEmulator => _lastDetectedEmulator;

    public string UpdateStatus => _updateStatus;

    public bool HasCachedProgramUpdate =>
        _stagedUpdate is { Scope: UpdateScope.Program };

    public string CachedProgramUpdateDetails
    {
        get
        {
            if (_stagedUpdate is not { Scope: UpdateScope.Program } staged)
                return string.Empty;
            try
            {
                var size = new FileInfo(staged.PackagePath).Length;
                return $"v{staged.Manifest.Version} · {FormatFileSize(size)}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"v{staged.Manifest.Version}";
            }
        }
    }

    public bool IsUpdateBusy => _updateCts is not null;

    public bool IsUpdateProgressVisible => _updateProgress is not null;

    public bool IsUpdateProgressIndeterminate => _updateProgress is not { Total: > 0 };

    public double UpdateProgressPercentage => _updateProgress is not { Total: > 0 } progress
        ? 0
        : Math.Clamp(progress.Completed * 100d / progress.Total, 0d, 100d);

    public string UpdateProgressText => _updateProgress is not { } progress
        ? string.Empty
        : progress.Total > 0
            ? $"{progress.Stage} · {progress.Completed:N0} / {progress.Total:N0} bytes ({UpdateProgressPercentage:0}%)"
            : progress.Stage;

    public bool StartupUpdateCheck
    {
        get => _draft.StartupUpdateCheck;
        set
        {
            if (_draft.StartupUpdateCheck == value) return;
            _draft.StartupUpdateCheck = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }






    public ICommand ForgetCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand CancelUpdateCommand { get; }
    public ICommand ClearUpdateCacheCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand SkipUpdateCommand { get; }










    public Func<IReadOnlyList<DetectedEmulatorInfo>, Task<DetectedEmulatorInfo?>>?
        RequestCandidateSelection { get; set; }

    public Func<IReadOnlyList<string>, Task<string?>>? RequestAddressSelection { get; set; }







    public Func<Task<bool>>? RequestOverwriteConfirmation { get; set; }

    private Task<DetectedEmulatorInfo?> ShowCandidateSelectionAsync(
        IReadOnlyList<DetectedEmulatorInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (Application.Current is null)
            return Task.FromResult(candidates.Count == 0 ? null : candidates[0]);

        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            return Task.FromResult(ShowCandidateSelection(candidates));

        return dispatcher.InvokeAsync(() => ShowCandidateSelection(candidates)).Task;
    }

    private static DetectedEmulatorInfo? ShowCandidateSelection(
        IReadOnlyList<DetectedEmulatorInfo> candidates)
    {
        var viewModel = new SelectionDialogViewModel(candidates);
        var dialog = new SelectionDialogView
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true
            ? viewModel.SelectedCandidate
            : null;
    }

    private Task<string?> ShowAddressSelectionAsync(IReadOnlyList<string> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (Application.Current is null)
            return Task.FromResult(endpoints.Count == 0 ? null : endpoints[0]);

        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            return Task.FromResult(ShowAddressSelection(endpoints));

        return dispatcher.InvokeAsync(() => ShowAddressSelection(endpoints)).Task;
    }

    private static string? ShowAddressSelection(IReadOnlyList<string> endpoints)
    {
        var candidates = endpoints
            .Select(endpoint => new DetectedEmulatorInfo("ADB endpoint", endpoint))
            .ToList();
        var viewModel = new SelectionDialogViewModel(candidates);
        var dialog = new SelectionDialogView
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true
            ? viewModel.SelectedCandidate?.AdbPath
            : null;
    }






    public ICommand ConnectCommand { get; }


    public ICommand CancelConnectCommand { get; }


    public ICommand SaveCommand { get; }


    public ICommand DetectAdbConfigCommand { get; }


    public ICommand DisconnectCommand { get; }






    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {



        if (_disposed)
            return;

        var operationAcquired = false;
        try
        {
            using var activityLease = _activityRegistry?.Acquire(ActivityKind.Connection);
            operationAcquired = await _operationGate.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(true);
            if (!operationAcquired)
                return;

            await ConnectCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A Grass queue stop can cancel while waiting for Settings' gate.
        }
        finally
        {
            if (operationAcquired)
                _operationGate.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;


        var currentState = _connectionState.State;
        if (currentState is not (ConnectionState.Disconnected or ConnectionState.Failed))
            return;

        ClearConnectionDiagnostic();
        await _healthMonitor.StopAsync();

        using var cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        _connectCts = cts;
        var discoverySucceeded = true;





        if (!string.IsNullOrWhiteSpace(DraftAdbPath)
            && !string.IsNullOrWhiteSpace(DraftConnectAddress)
            && DraftConnectAddress.Contains(':', StringComparison.Ordinal)
            && !DraftAlwaysAutoDetect)
        {
            _connectionState.SetState(ConnectionState.Connecting);
            try
            {
                var direct = await _umaService.ConnectAsync(
                    DraftAdbPath,
                    DraftConnectAddress,
                    DraftConnectConfig,
                    cts.Token);
                if (direct is ConnectionSucceededEvent success)
                {
                    HandleConnectSuccess(success);
                    _connectCts = null;
                    return;
                }
            }
            catch (Exception)
            {


            }

            _connectionState.SetState(ConnectionState.Disconnected);
        }


        if (DraftAutoDetect)
        {
            bool shouldDetect = DraftAlwaysAutoDetect
                || string.IsNullOrWhiteSpace(DraftConnectAddress);

            if (shouldDetect)
            {


                if (DraftAlwaysAutoDetect
                    && !string.IsNullOrWhiteSpace(DraftConnectAddress)
                    && RequestOverwriteConfirmation is not null)
                {
                    bool confirmed = await RequestOverwriteConfirmation();
                    if (!confirmed)
                    {


                    }
                    else
                    {
                        discoverySucceeded = await RunDiscoveryAsync(
                            preferCachedAdb: true,
                            allowAutoStart: true,
                            cancellationToken: cts.Token);
                    }
                }
                else
                {
                    discoverySucceeded = await RunDiscoveryAsync(
                        preferCachedAdb: true,
                        allowAutoStart: true,
                        cancellationToken: cts.Token);
                }
            }
        }

        if (cts.IsCancellationRequested || !discoverySucceeded)
        {
            _connectCts = null;
            return;
        }


        if (string.IsNullOrWhiteSpace(DraftAdbPath))
        {
            SetConnectionDiagnostic("An ADB executable path is required.");
            _connectCts = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(DraftConnectAddress))
        {
            SetConnectionDiagnostic("A connection address is required.");
            _connectCts = null;
            return;
        }






        ClearConnectionDiagnostic();
        _connectionState.SetState(ConnectionState.Connecting);

        try
        {
            var result = await _umaService.ConnectAsync(
                DraftAdbPath,
                DraftConnectAddress,
                DraftConnectConfig,
                cts.Token);

            switch (result)
            {
                case ConnectionSucceededEvent success:
                    HandleConnectSuccess(success);
                    break;

                case ConnectionFailedEvent failure:
                    if (failure.ErrorCode == ConnectionErrorCode.Canceled
                        || cts.IsCancellationRequested)
                    {
                        _connectionState.SetState(ConnectionState.Disconnected);
                        SetConnectionDiagnostic("Connection canceled.");
                    }
                    else
                    {
                        _connectionState.SetState(ConnectionState.Failed);
                        SetConnectionDiagnostic(
                            $"Connection failed ({failure.ErrorCode}) at {failure.Phase}: {failure.Message}");
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _connectionState.SetState(ConnectionState.Disconnected);
            SetConnectionDiagnostic("Connection canceled.");
        }
        catch (Exception exception)
        {
            _connectionState.SetState(ConnectionState.Failed);
            SetConnectionDiagnostic($"Connection failed: {exception.Message}");
        }
        finally
        {
            _connectCts = null;
        }
    }






    public void Cancel()
    {
        if (_connectCts is null || _connectCts.IsCancellationRequested)
            return;

        _connectionState.SetState(ConnectionState.Canceling);
        _ = _healthMonitor.StopAsync();
        _connectCts.Cancel();
    }




    public void Disconnect()
    {
        if (_disposed)
            return;

        _connectCts?.Cancel();
        _ = _healthMonitor.StopAsync();
        _connectionState.SetState(ConnectionState.Disconnected);
        SetConnectionDiagnostic("Disconnected.");
    }





    public void Forget()
    {
        _connectionState.ClearLastVerified();
    }




    public void SaveSettings()
    {
        _draft.AdbPath = DraftAdbPath;
        _draft.ConnectAddress = DraftConnectAddress;
        _draft.ConnectConfig = DraftConnectConfig;
        _draft.AutoDetectConnection = DraftAutoDetect;
        _draft.AlwaysAutoDetectConnection = DraftAlwaysAutoDetect;
        _draft.AutoStartEmulator = DraftAutoStartEmulator;
        _draft.EmulatorExecutablePath = DraftEmulatorExecutablePath;
        _draft.AutoStartEmulatorWaitSeconds = DraftAutoStartEmulatorWaitSeconds;
        _draft.Language = DraftLanguage;
        _draft.StartupUpdateCheck = StartupUpdateCheck;




        var latest = _settingsService.Load();
        _draft.TargetPackageIds = latest.TargetPackageIds;
        _draft.TargetActivityName = latest.TargetActivityName;
        _draft.TaskQueue = latest.TaskQueue;
        _draft.Hachimi = latest.Hachimi;

        _settingsService.Save(_draft);
    }

    public async Task RunStartupUpdateCheckAsync()
    {
        if (_updateService is null || _disposed || IsUpdateBusy)
            return;

        var settings = _settingsService.Load();
        if (!settings.StartupUpdateCheck)
            return;

        SetStagedUpdate(_updateService.RestoreStagedProgram());
        _pendingUpdatePlan = null;
        _pendingUpdateScope = UpdateScope.Program;

        await CheckForUpdatesAsync().ConfigureAwait(true);
        if (_pendingUpdatePlan is null)
            return;

        if (_pendingUpdateScope == UpdateScope.Program
            && string.Equals(
                settings.SkippedProgramVersion,
                _pendingUpdatePlan.Manifest.Version,
                StringComparison.Ordinal))
            return;

        if (_pendingUpdateScope == UpdateScope.Program && _stagedUpdate is null)
        {
            var download = MessageBox.Show(
                Application.Current?.MainWindow,
                $"Version {_pendingUpdatePlan.Manifest.Version} is available. Download it now?",
                "UmamusumeAss update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (download == MessageBoxResult.Yes)
            {
                var downloaded = await DownloadUpdateAsync().ConfigureAwait(true);
                if (!downloaded)
                {
                    MessageBox.Show(
                        Application.Current?.MainWindow,
                        UpdateStatus,
                        "UmamusumeAss update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }
        }

        if (_pendingUpdateScope != UpdateScope.Program || _stagedUpdate is null)
            return;

        var restart = MessageBox.Show(
            Application.Current?.MainWindow,
            $"Version {_stagedUpdate.Manifest.Version} is downloaded. Restart now when the current work is idle?",
            "UmamusumeAss update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (restart == MessageBoxResult.Yes
            && !await InstallUpdateAsync().ConfigureAwait(true))
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                UpdateStatus,
                "UmamusumeAss update",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is null || _disposed || IsUpdateBusy) return;
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        SetUpdateProgress(null);
        SetUpdateStatus("Checking for updates...");
        RaiseUpdateCommands();
        try
        {
            var result = await _updateService.CheckAsync(UpdateScope.All, cts.Token).ConfigureAwait(true);
            if (result.Error is not null)
            {
                SetUpdateStatus($"Update check failed: {result.Error}");
                return;
            }
            _pendingUpdateScope = UpdateScope.Program;
            _pendingUpdatePlan = result.Program is null
                ? null
                : _updateService.SelectProgram(result.Program);
            if (_pendingUpdatePlan is null && result.Resource is not null)
            {
                _pendingUpdateScope = UpdateScope.Resource;
                _pendingUpdatePlan = _updateService.SelectResource(result.Resource);
            }
            if (_pendingUpdatePlan is null && _stagedUpdate is not null)
                SetUpdateStatus($"Version {_stagedUpdate.Manifest.Version} is already downloaded and ready to install.");
            else if (_pendingUpdatePlan is null)
                SetUpdateStatus("You are up to date.");
            else if (_stagedUpdate is not null
                && _stagedUpdate.Manifest.Version.Equals(
                    _pendingUpdatePlan.Manifest.Version, StringComparison.Ordinal)
                && _stagedUpdate.Asset.AssetName.Equals(
                    _pendingUpdatePlan.Asset.AssetName, StringComparison.Ordinal))
                SetUpdateStatus($"Version {_stagedUpdate.Manifest.Version} is already downloaded and ready to install.");
            else if (string.Equals(_draft.SkippedProgramVersion, _pendingUpdatePlan.Manifest.Version,
                         StringComparison.Ordinal))
                SetUpdateStatus($"Version {_pendingUpdatePlan.Manifest.Version} skipped once.");
            else
            {
                SetUpdateStatus($"Version {_pendingUpdatePlan.Manifest.Version} is available.");
            }
        }
        catch (Exception exception)
        {
            SetUpdateStatus($"Update check failed: {exception.Message}");
        }
        finally
        {
            _updateCts = null;
            RaiseUpdateCommands();
        }
    }

    private async Task<bool> DownloadUpdateAsync()
    {
        if (_updateService is null || _pendingUpdatePlan is null || _disposed || IsUpdateBusy)
            return false;
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        SetUpdateProgress(null);
        SetUpdateStatus("Downloading update...");
        RaiseUpdateCommands();
        try
        {
            var progress = new Progress<UpdateProgress>(SetUpdateProgress);
            var downloaded = await _updateService.DownloadAsync(
                _pendingUpdatePlan,
                _pendingUpdateScope,
                progress: progress,
                cancellationToken: cts.Token).ConfigureAwait(true);
            SetStagedUpdate(downloaded);
            if (downloaded.Scope == UpdateScope.Resource)
            {
                SetUpdateStatus("Resource downloaded. Applying when the app is idle...");
                await _updateService.ApplyResourceWhenIdleAsync(downloaded, cts.Token)
                    .ConfigureAwait(true);
                SetStagedUpdate(null);
                SetUpdateStatus("Resource update applied.");
            }
            else
            {
                SetUpdateStatus("Update downloaded. It will install when you confirm restart.");
            }
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetUpdateProgress(null);
            SetUpdateStatus("Update canceled.");
            return false;
        }
        catch (Exception exception)
        {
            SetUpdateStatus($"Download failed: {exception.Message}");
            return false;
        }
        finally
        {
            _updateCts = null;
            RaiseUpdateCommands();
        }
    }

    private async Task<bool> InstallUpdateAsync()
    {
        if (_updateService is null || _stagedUpdate is null || _disposed || IsUpdateBusy)
            return false;
        try
        {
            if (_stagedUpdate.Scope == UpdateScope.Program)
                await _updateService.RequestProgramRestartAsync(_stagedUpdate).ConfigureAwait(true);
            else
                await _updateService.ApplyResourceWhenIdleAsync(_stagedUpdate).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            if (_stagedUpdate.Scope == UpdateScope.Program)
            {
                _updateService.DiscardStagedProgram();
                SetStagedUpdate(null);
            }
            SetUpdateStatus($"Install failed: {exception.Message}");
            return false;
        }
    }

    private void CancelUpdate()
    {
        _updateCts?.Cancel();
        SetUpdateStatus("Update canceled.");
    }

    private async Task ClearUpdateCacheAsync()
    {
        if (_updateService is null || _disposed || IsUpdateBusy)
            return;
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        SetUpdateProgress(null);
        SetUpdateStatus("Deleting all update cache...");
        RaiseUpdateCommands();
        try
        {
            await _updateService.ClearAllUpdateCacheAsync(cts.Token).ConfigureAwait(true);
            SetStagedUpdate(null);
            SetUpdateStatus("All update cache deleted.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetUpdateStatus("Cache deletion canceled.");
        }
        catch (Exception exception)
        {
            SetUpdateStatus($"Failed to delete update cache: {exception.Message}");
        }
        finally
        {
            _updateCts = null;
            RaiseUpdateCommands();
        }
    }

    private void SkipUpdate()
    {
        if (_pendingUpdatePlan is null) return;
        _draft.SkippedProgramVersion = _pendingUpdatePlan.Manifest.Version;
        SaveSettings();
        SetUpdateStatus($"Version {_pendingUpdatePlan.Manifest.Version} skipped once.");
    }

    private void SetUpdateStatus(string status)
    {
        _updateStatus = status;
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void SetStagedUpdate(StagedUpdate? update)
    {
        _stagedUpdate = update;
        OnPropertyChanged(nameof(HasCachedProgramUpdate));
        OnPropertyChanged(nameof(CachedProgramUpdateDetails));
    }

    private bool IsPendingUpdateCached()
    {
        return _pendingUpdatePlan is not null
            && _stagedUpdate is not null
            && _stagedUpdate.Scope == _pendingUpdateScope
            && _stagedUpdate.Manifest.Version.Equals(
                _pendingUpdatePlan.Manifest.Version, StringComparison.Ordinal)
            && _stagedUpdate.Asset.AssetName.Equals(
                _pendingUpdatePlan.Asset.AssetName, StringComparison.Ordinal);
    }

    private static string FormatFileSize(long bytes)
    {
        const double oneMegabyte = 1024d * 1024d;
        return bytes >= oneMegabyte
            ? $"{bytes / oneMegabyte:0.##} MB"
            : $"{bytes / 1024d:0.##} KB";
    }

    private void SetUpdateProgress(UpdateProgress? progress)
    {
        _updateProgress = progress;
        OnPropertyChanged(nameof(IsUpdateProgressVisible));
        OnPropertyChanged(nameof(IsUpdateProgressIndeterminate));
        OnPropertyChanged(nameof(UpdateProgressPercentage));
        OnPropertyChanged(nameof(UpdateProgressText));
    }

    private void RaiseUpdateCommands()
    {
        (CheckForUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DownloadUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearUpdateCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InstallUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SkipUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsUpdateBusy));
    }





    public async Task AutoDetectEmulatorsAsync()
    {
        if (_disposed || !await _operationGate.WaitAsync(0).ConfigureAwait(true))
            return;

        using var cts = new CancellationTokenSource();
        _connectCts = cts;
        try
        {
            await RunDiscoveryAsync(cancellationToken: cts.Token).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
                _connectCts = null;
            _operationGate.Release();
        }
    }










    private async Task<bool> RunDiscoveryAsync(
        bool preferCachedAdb = false,
        bool allowAutoStart = false,
        CancellationToken cancellationToken = default)
    {
        _connectionState.SetState(ConnectionState.Detecting);

        try
        {




            if (preferCachedAdb
                && !string.IsNullOrWhiteSpace(DraftAdbPath))
            {
                var cachedDevices = await _winAdapter.GetAdbDevicesAsync(
                    DraftAdbPath,
                    cancellationToken).ConfigureAwait(true);




                var cachedDevice = cachedDevices.Records.FirstOrDefault(
                    device => string.Equals(
                        device.State,
                        "device",
                        StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(DraftConnectAddress)
                        && string.Equals(
                            device.Serial,
                            DraftConnectAddress,
                            StringComparison.OrdinalIgnoreCase))
                    ?? cachedDevices.Records.FirstOrDefault(
                        device => string.Equals(
                            device.State,
                            "device",
                            StringComparison.OrdinalIgnoreCase));
                if (cachedDevice is not null)
                {
                    DraftConnectAddress = cachedDevice.Serial;
                    _connectionState.SetState(ConnectionState.Disconnected);
                    return true;
                }
            }

            var discoveryResult = _winAdapter.RefreshEmulatorsInfo();

            var candidates = discoveryResult.Candidates
                .Where(c => c.AdbPath is not null)
                .ToList();
            var autoStartAttempted = false;

            if (allowAutoStart && candidates.Count == 0 && DraftAutoStartEmulator)
            {
                autoStartAttempted = true;
                if (!await HandleAutoStartLaunchAsync(cancellationToken))
                {
                    _connectionState.SetState(ConnectionState.Disconnected);
                    return false;
                }

                discoveryResult = await RefreshAfterAutoStartAsync(cancellationToken)
                    .ConfigureAwait(true);
                candidates = discoveryResult.Candidates
                    .Where(c => c.AdbPath is not null)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                SetConnectionDiagnostic("No running emulator with a usable ADB executable was found.");
                _connectionState.SetState(ConnectionState.Disconnected);
                return false;
            }


            DetectedEmulatorInfo selected;
            if (candidates.Count == 1)
            {
                selected = candidates[0];
            }
            else if (RequestCandidateSelection is not null)
            {
                var picked = await RequestCandidateSelection(candidates);
                if (picked is null || picked.AdbPath is null)
                {
                    SetConnectionDiagnostic("Emulator selection was canceled.");
                    _connectionState.SetState(ConnectionState.Disconnected);
                    return false;
                }
                selected = picked;
            }
            else
            {

                selected = candidates[0];
            }


            DraftAdbPath = selected.AdbPath!;
            DraftConnectConfig = selected.EmulatorName;
            _lastDetectedEmulator = selected.EmulatorName;
            OnPropertyChanged(nameof(LastDetectedEmulator));

            if (allowAutoStart && DraftAutoStartEmulator && !autoStartAttempted)
            {
                var devices = await _winAdapter.GetAdbDevicesAsync(
                    selected.AdbPath!,
                    cancellationToken).ConfigureAwait(true);
                var hasReadyDevice = devices.Records.Any(
                    device => string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase));
                if (!hasReadyDevice)
                {
                    autoStartAttempted = true;
                    if (!await HandleAutoStartLaunchAsync(cancellationToken))
                    {
                        _connectionState.SetState(ConnectionState.Disconnected);
                        return false;
                    }
                }
            }









            if (autoStartAttempted)
            {
                var readyDevice = await WaitForAutoStartAdbDeviceAsync(
                    selected.AdbPath!,
                    cancellationToken).ConfigureAwait(true);
                if (readyDevice is not null)
                {
                    DraftConnectAddress = readyDevice;
                    _connectionState.SetState(ConnectionState.Disconnected);
                    return true;
                }
            }

            var resolution = await _winAdapter.ResolveEndpointsAsync(
                selected.AdbPath!,
                selected.EmulatorName,
                cancellationToken);

            if (resolution.VerifiedEndpoints.Count == 1)
            {
                DraftConnectAddress = resolution.VerifiedEndpoints[0];
            }
            else if (resolution.VerifiedEndpoints.Count > 1 && RequestAddressSelection is not null)
            {
                var address = await RequestAddressSelection(resolution.VerifiedEndpoints);
                if (address is null || !resolution.VerifiedEndpoints.Contains(address))
                {
                    SetConnectionDiagnostic("Connection address selection was canceled.");
                    _connectionState.SetState(ConnectionState.Disconnected);
                    return false;
                }

                DraftConnectAddress = address;
            }
            else if (resolution.VerifiedEndpoints.Count > 1)
            {
                DraftConnectAddress = resolution.VerifiedEndpoints[0];
            }
            else
            {
                var details = string.Join(
                    " | ",
                    resolution.Diagnostics
                        .Select(diagnostic => diagnostic.Message)
                        .Distinct(StringComparer.Ordinal));
                SetConnectionDiagnostic(string.IsNullOrEmpty(details)
                    ? $"No usable {selected.EmulatorName} connection endpoint was found."
                    : $"No usable {selected.EmulatorName} connection endpoint was found: {details}");
                _connectionState.SetState(ConnectionState.Disconnected);
                return false;
            }

            _connectionState.SetState(ConnectionState.Disconnected);
            return true;
        }
        catch (OperationCanceledException)
        {
            _connectionState.SetState(ConnectionState.Disconnected);
            SetConnectionDiagnostic("Connection canceled.");
            return false;
        }
        catch (Exception exception)
        {
            SetConnectionDiagnostic($"Emulator discovery failed: {exception.Message}");
            _connectionState.SetState(ConnectionState.Disconnected);
            return false;
        }
    }

    private void SetConnectionDiagnostic(string diagnostic)
    {
        _connectionDiagnostic = diagnostic;
        OnPropertyChanged(nameof(StatusText));
    }

    private void ClearConnectionDiagnostic()
    {
        if (string.IsNullOrEmpty(_connectionDiagnostic))
            return;

        _connectionDiagnostic = string.Empty;
        OnPropertyChanged(nameof(StatusText));
    }





    private void HandleConnectSuccess(ConnectionSucceededEvent success)
    {
        ClearConnectionDiagnostic();


        var verified = new LastVerifiedConnection(
            AdbPath: DraftAdbPath,
            Serial: success.Serial,
            AndroidId: success.AndroidId,
            AndroidVersion: success.AndroidVersion,
            Width: success.Width,
            Height: success.Height,
            PhysicalWidth: success.PhysicalWidth,
            PhysicalHeight: success.PhysicalHeight,
            VerifiedAt: DateTimeOffset.UtcNow);

        _connectionState.UpdateLastVerified(verified);


        _draft.AddAddressToHistory(success.Serial);
        RefreshHistoryFromDraft();


        SaveSettings();

        _connectionState.SetState(ConnectionState.Connected);
        _healthMonitor.Start(new ConnectionHealthTarget(
            DraftAdbPath,
            success.Serial,
            DraftConnectConfig));
    }

    private void OnHealthMonitorFailed(ConnectionHealthFailure failure)
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnHealthMonitorFailed(failure)));
            return;
        }

        var disconnected = failure.ErrorCode == ConnectionErrorCode.DeviceDisconnected;
        SetConnectionDiagnostic(disconnected
            ? $"Disconnected: {failure.Diagnostic}"
            : $"Connection health failed: {failure.Diagnostic}");
        _connectionState.SetState(
            disconnected ? ConnectionState.Disconnected : ConnectionState.Failed);
    }




    private void RefreshHistoryFromDraft()
    {
        ConnectAddressHistory.Clear();
        foreach (var addr in _draft.ConnectAddressHistory)
        {
            ConnectAddressHistory.Add(addr);
        }
    }





    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsOperationInProgress));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ControlSession));
        OnPropertyChanged(nameof(LastVerified));

        if (ConnectCommand is RelayCommand rc)
            rc.RaiseCanExecuteChanged();
        if (CancelConnectCommand is RelayCommand rc2)
            rc2.RaiseCanExecuteChanged();
        if (DetectAdbConfigCommand is RelayCommand rc3)
            rc3.RaiseCanExecuteChanged();
        if (DisconnectCommand is RelayCommand rc4)
            rc4.RaiseCanExecuteChanged();
    }





    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }





    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connectionState.StateChanged -= OnStateChanged;
        _healthMonitor.Failed -= OnHealthMonitorFailed;
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = null;
        _healthMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }





    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool> _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
