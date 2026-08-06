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










    public SettingsViewModel(
        IUmaService umaService,
        IConnectionStateService connectionState,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IWinAdapter winAdapter,
        IEmulatorLauncher emulatorLauncher,
        IAsyncDelay asyncDelay,
        IConnectionHealthMonitor healthMonitor)
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
        _healthMonitor.Failed += OnHealthMonitorFailed;


        _draft = _settingsService.Load();
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
    }









    public int SelectedMenuIndex
    {
        get => _selectedMenuIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
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
    ];






    public string CoreVersion => _umaService.CoreVersion ?? string.Empty;


    public string ResourcePath => _umaService.ResourcePath ?? string.Empty;


    public string LastDetectedEmulator => _lastDetectedEmulator;






    public ICommand ForgetCommand { get; }










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




        var latest = _settingsService.Load();
        _draft.TargetPackageIds = latest.TargetPackageIds;
        _draft.TargetActivityName = latest.TargetActivityName;
        _draft.TaskQueue = latest.TaskQueue;
        _draft.Hachimi = latest.Hachimi;

        _settingsService.Save(_draft);
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
