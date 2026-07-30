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

/// <summary>
/// Sole owner of connection UI behavior: menu navigation, connection draft editing,
/// connect/cancel lifecycle, emulator discovery, last-verified management, settings
/// persistence, and language selection.
///
/// The ViewModel owns an editable <see cref="ConnectionSettings"/> draft that is
/// separate from the persisted settings until <see cref="SaveSettings"/> is called,
/// and separate from the immutable <see cref="LastVerifiedConnection"/> snapshot
/// stored in <see cref="IConnectionStateService"/>.
///
/// Seam properties (<see cref="RequestCandidateSelection"/>,
/// <see cref="RequestOverwriteConfirmation"/>) allow Task 8's selection dialog
/// to be wired in without creating a view dependency here.
/// </summary>
public sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    // ────────────────────────────────────────────────────────────────
    // Dependencies
    // ────────────────────────────────────────────────────────────────

    private readonly IUmaService _umaService;
    private readonly IConnectionStateService _connectionState;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IWinAdapter _winAdapter;
    private readonly IEmulatorLauncher _emulatorLauncher;
    private readonly IAsyncDelay _asyncDelay;
    private readonly IConnectionHealthMonitor _healthMonitor;

    // ────────────────────────────────────────────────────────────────
    // Mutable state
    // ────────────────────────────────────────────────────────────────

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

    // ────────────────────────────────────────────────────────────────
    // Construction
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the SettingsViewModel, loads draft settings from the persistence
    /// service, subscribes to connection state changes, and initialises the
    /// language from the localization service.
    /// </summary>
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

        // Load draft settings from persistence
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

        // Populate history from draft
        ConnectAddressHistory = new ObservableCollection<string>(_draft.ConnectAddressHistory);

        // Subscribe to state changes
        _connectionState.StateChanged += OnStateChanged;

        // Wire commands
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

    // ────────────────────────────────────────────────────────────────
    // Menu navigation (3 panels: Connection=0, Language=1, System=2)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Currently selected navigation index. Clamped to [0, 2].
    /// Changing this updates MenuItems selection state.
    /// </summary>
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

    // ────────────────────────────────────────────────────────────────
    // Draft settings (user-editable, not persisted until SaveSettings)
    // ────────────────────────────────────────────────────────────────

    /// <summary>Draft ADB executable path.</summary>
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

    /// <summary>Draft ADB connect address (ip:port or serial).</summary>
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

    /// <summary>Draft MAA-compatible connection profile used by native ADB commands.</summary>
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

    /// <summary>Profiles available to the connection ComboBox.</summary>
    public ObservableCollection<string> ConnectConfigOptions { get; } =
        new(ConnectionSettings.SupportedConnectConfigs);

    /// <summary>Draft auto-detect connection toggle.</summary>
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

    /// <summary>Draft always-auto-detect toggle.</summary>
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

    /// <summary>
    /// Draft language that will be persisted on save.
    /// Updated automatically when <see cref="SelectedLanguage"/> changes.
    /// </summary>
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

    // ────────────────────────────────────────────────────────────────
    // State (read-only, from IConnectionStateService)
    // ────────────────────────────────────────────────────────────────

    /// <summary>Current connection operation state.</summary>
    public ConnectionState State => _connectionState.State;

    /// <summary>True when an operation is in progress (Detecting, Connecting, or Canceling).</summary>
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

    // ────────────────────────────────────────────────────────────────
    // Last verified (read-only, immutable snapshot)
    // ────────────────────────────────────────────────────────────────

    /// <summary>Immutable last-verified connection record, or null if none.</summary>
    public LastVerifiedConnection? LastVerified => _connectionState.LastVerifiedConnection;

    /// <summary>Current control session snapshot, or null if none.</summary>
    public ControlSessionSnapshot? ControlSession => _connectionState.ControlSession;

    // ────────────────────────────────────────────────────────────────
    // Connection history
    // ────────────────────────────────────────────────────────────────

    /// <summary>Observable history of successfully connected addresses.</summary>
    public ObservableCollection<string> ConnectAddressHistory { get; }

    // ────────────────────────────────────────────────────────────────
    // Language selection
    // ────────────────────────────────────────────────────────────────

    /// <summary>Currently selected UI language. Changing this switches localization.</summary>
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

            // Keep draft language in sync with the effective language
            DraftLanguage = _localizationService.CurrentCulture;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Menu items for navigation
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigation menu items. Each entry has <see cref="MenuItemViewModel.LabelKey"/>
    /// for DynamicResource binding and <see cref="MenuItemViewModel.Index"/> for
    /// selection tracking.
    /// </summary>
    public ObservableCollection<MenuItemViewModel> MenuItems { get; } =
    [
        new("NavConnection", 0),
        new("NavLanguage", 1),
        new("NavSystem", 2),
    ];

    // ────────────────────────────────────────────────────────────────
    // System info
    // ────────────────────────────────────────────────────────────────

    /// <summary>Core bridge version string, or empty if unavailable.</summary>
    public string CoreVersion => _umaService.CoreVersion ?? string.Empty;

    /// <summary>Application resource path, or empty if unavailable.</summary>
#pragma warning disable CA1822 // WPF binding requires instance property
    public string ResourcePath => string.Empty;
#pragma warning restore CA1822

    /// <summary>Last detected emulator name, or empty if none.</summary>
    public string LastDetectedEmulator => _lastDetectedEmulator;

    // ────────────────────────────────────────────────────────────────
    // Forget command
    // ────────────────────────────────────────────────────────────────

    /// <summary>Command wrapping <see cref="Forget"/>.</summary>
    public ICommand ForgetCommand { get; }

    // ────────────────────────────────────────────────────────────────
    // Seams for Task 8 (selection dialog, overwrite confirmation)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when auto-detect discovers multiple emulator candidates
    /// and the user must pick one. Task 8 wires this to a selection dialog.
    /// When null, the first candidate is used automatically.
    /// </summary>
    public Func<IReadOnlyList<DetectedEmulatorInfo>, Task<DetectedEmulatorInfo?>>?
        RequestCandidateSelection { get; set; }

    public Func<IReadOnlyList<string>, Task<string?>>? RequestAddressSelection { get; set; }

    /// <summary>
    /// Called when <see cref="DraftAlwaysAutoDetect"/> is enabled and
    /// auto-detect would overwrite non-blank manual values.
    /// Return true to allow overwrite, false to keep manual values.
    /// When null, the overwrite proceeds without confirmation.
    /// </summary>
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

    // ────────────────────────────────────────────────────────────────
    // Commands
    // ────────────────────────────────────────────────────────────────

    /// <summary>Command wrapping <see cref="ConnectAsync"/>; disabled during operations.</summary>
    public ICommand ConnectCommand { get; }

    /// <summary>Command wrapping <see cref="Cancel"/>; enabled only during operations.</summary>
    public ICommand CancelConnectCommand { get; }

    /// <summary>Command wrapping <see cref="SaveSettings"/>; always enabled unless disposed.</summary>
    public ICommand SaveCommand { get; }

    /// <summary>Command wrapping <see cref="AutoDetectEmulatorsAsync"/>; disabled during operations.</summary>
    public ICommand DetectAdbConfigCommand { get; }

    /// <summary>Stops health monitoring and marks the current session disconnected.</summary>
    public ICommand DisconnectCommand { get; }

    /// <summary>
    /// Runs the full connect flow: optional auto-detect (if enabled),
    /// then connect via <see cref="IUmaService.ConnectAsync"/>.
    /// Guards against overlapping operations.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_disposed || !await _operationGate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            await ConnectCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ConnectCoreAsync()
    {
        if (_disposed)
            return;

        // ── Overlap prevention ──────────────────────────────────
        var currentState = _connectionState.State;
        if (currentState is not (ConnectionState.Disconnected or ConnectionState.Failed))
            return;

        ClearConnectionDiagnostic();
        await _healthMonitor.StopAsync();

        using var cts = new CancellationTokenSource();
        _connectCts = cts;
        var discoverySucceeded = true;

        // ── Auto-detect phase ───────────────────────────────────
        if (DraftAutoDetect)
        {
            bool shouldDetect = DraftAlwaysAutoDetect
                || string.IsNullOrWhiteSpace(DraftConnectAddress);

            if (shouldDetect)
            {
                // When AlwaysAutoDetect is on and user has non-blank values,
                // request confirmation before overwriting.
                if (DraftAlwaysAutoDetect
                    && !string.IsNullOrWhiteSpace(DraftConnectAddress)
                    && RequestOverwriteConfirmation is not null)
                {
                    bool confirmed = await RequestOverwriteConfirmation();
                    if (!confirmed)
                    {
                        // User declined overwrite — do not run discovery,
                        // but still proceed to connect with manual values.
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

        // ── Validate before connect ─────────────────────────────
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

        // ── Connect phase ───────────────────────────────────────
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

    /// <summary>
    /// Cancels the current connect operation (if any).
    /// Transitions through <see cref="ConnectionState.Canceling"/> before
    /// requesting cancellation. Safe to call when idle.
    /// </summary>
    public void Cancel()
    {
        if (_connectCts is null || _connectCts.IsCancellationRequested)
            return;

        _connectionState.SetState(ConnectionState.Canceling);
        _ = _healthMonitor.StopAsync();
        _connectCts.Cancel();
    }

    /// <summary>
    /// Ends the managed session without touching the shared ADB server.
    /// </summary>
    public void Disconnect()
    {
        if (_disposed)
            return;

        _connectCts?.Cancel();
        _ = _healthMonitor.StopAsync();
        _connectionState.SetState(ConnectionState.Disconnected);
        SetConnectionDiagnostic("Disconnected.");
    }

    /// <summary>
    /// Clears the immutable last-verified snapshot without affecting
    /// the editable draft or persisted settings.
    /// </summary>
    public void Forget()
    {
        _connectionState.ClearLastVerified();
    }

    /// <summary>
    /// Persists the current draft settings to the settings service.
    /// </summary>
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

        _settingsService.Save(_draft);
    }

    /// <summary>
    /// Runs emulator discovery and applies found values to the draft.
    /// May transition through <see cref="ConnectionState.Detecting"/>.
    /// </summary>
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

    // ────────────────────────────────────────────────────────────────
    // Private: discovery
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs emulator process discovery, filters candidates with ADB paths,
    /// optionally asks the user to pick when multiple are found, then runs
    /// adb devices on the selected candidate to find an eligible serial.
    /// </summary>
    private async Task<bool> RunDiscoveryAsync(
        bool preferCachedAdb = false,
        bool allowAutoStart = false,
        CancellationToken cancellationToken = default)
    {
        _connectionState.SetState(ConnectionState.Detecting);

        try
        {
            // A previously verified ADB path is the cheapest and most stable
            // discovery source. Only fall back to process scanning when the
            // cached ADB server has no ready device; this also handles emulator
            // process names that changed between vendor releases.
            if (preferCachedAdb
                && !DraftAlwaysAutoDetect
                && !string.IsNullOrWhiteSpace(DraftAdbPath))
            {
                var cachedDevices = await _winAdapter.GetAdbDevicesAsync(
                    DraftAdbPath,
                    cancellationToken).ConfigureAwait(true);
                var cachedDevice = cachedDevices.Records.FirstOrDefault(
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

            // Select candidate — ask for user input when multiple, or auto-pick
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
                // No seam and multiple candidates — pick the first one
                selected = candidates[0];
            }

            // Apply ADB path
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

    // ────────────────────────────────────────────────────────────────
    // Private: connect success handling
    // ────────────────────────────────────────────────────────────────

    private void HandleConnectSuccess(ConnectionSucceededEvent success)
    {
        // Store immutable last-verified snapshot
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

        // Add address to history (use the serial from the event)
        _draft.AddAddressToHistory(success.Serial);
        RefreshHistoryFromDraft();

        // Persist current draft (includes updated AdbPath, ConnectAddress, history)
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

    /// <summary>
    /// Synchronises the observable history collection from the draft's list.
    /// </summary>
    private void RefreshHistoryFromDraft()
    {
        ConnectAddressHistory.Clear();
        foreach (var addr in _draft.ConnectAddressHistory)
        {
            ConnectAddressHistory.Add(addr);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Private: state change monitoring
    // ────────────────────────────────────────────────────────────────

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

    // ────────────────────────────────────────────────────────────────
    // INotifyPropertyChanged
    // ────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ────────────────────────────────────────────────────────────────
    // IDisposable
    // ────────────────────────────────────────────────────────────────

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

    // ────────────────────────────────────────────────────────────────
    // Minimal RelayCommand for WPF ICommand binding
    // ────────────────────────────────────────────────────────────────

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
