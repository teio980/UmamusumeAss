using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

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
public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    // ────────────────────────────────────────────────────────────────
    // Dependencies
    // ────────────────────────────────────────────────────────────────

    private readonly IUmaService _umaService;
    private readonly IConnectionStateService _connectionState;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IWinAdapter _winAdapter;

    // ────────────────────────────────────────────────────────────────
    // Mutable state
    // ────────────────────────────────────────────────────────────────

    private ConnectionSettings _draft;
    private int _selectedMenuIndex;
    private string _draftAdbPath;
    private string _draftConnectAddress;
    private bool _draftAutoDetect;
    private bool _draftAlwaysAutoDetect;
    private string _draftLanguage;
    private string _selectedLanguage;
    private CancellationTokenSource? _connectCts;
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
        IWinAdapter winAdapter)
    {
        ArgumentNullException.ThrowIfNull(umaService);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(winAdapter);

        _umaService = umaService;
        _connectionState = connectionState;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _winAdapter = winAdapter;

        // Load draft settings from persistence
        _draft = _settingsService.Load();
        _draftAdbPath = _draft.AdbPath;
        _draftConnectAddress = _draft.ConnectAddress;
        _draftAutoDetect = _draft.AutoDetectConnection;
        _draftAlwaysAutoDetect = _draft.AlwaysAutoDetectConnection;
        _draftLanguage = _draft.Language;
        _selectedLanguage = _localizationService.CurrentCulture;

        // Populate history from draft
        ConnectAddressHistory = new ObservableCollection<string>(_draft.ConnectAddressHistory);

        // Subscribe to state changes
        _connectionState.StateChanged += OnStateChanged;

        // Wire commands
        ConnectCommand = new RelayCommand(
            _ => { _ = ConnectAsync(); },
            _ => !_disposed && State is ConnectionState.Disconnected or ConnectionState.Failed);

        CancelConnectCommand = new RelayCommand(
            _ => Cancel(),
            _ => !_disposed && IsOperationInProgress);

        SaveCommand = new RelayCommand(
            _ => SaveSettings(),
            _ => !_disposed);

        DetectAdbConfigCommand = new RelayCommand(
            _ => { _ = AutoDetectEmulatorsAsync(); },
            _ => !_disposed && !IsOperationInProgress);

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
            if (_draftConnectAddress == value)
                return;
            _draftConnectAddress = value;
            OnPropertyChanged();
        }
    }

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

    public string StatusText => _connectionState.State switch
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
#pragma warning disable CA1822 // WPF binding requires instance property
    public string LastDetectedEmulator => string.Empty;
#pragma warning restore CA1822

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

    /// <summary>
    /// Called when <see cref="DraftAlwaysAutoDetect"/> is enabled and
    /// auto-detect would overwrite non-blank manual values.
    /// Return true to allow overwrite, false to keep manual values.
    /// When null, the overwrite proceeds without confirmation.
    /// </summary>
    public Func<Task<bool>>? RequestOverwriteConfirmation { get; set; }

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

    /// <summary>
    /// Runs the full connect flow: optional auto-detect (if enabled),
    /// then connect via <see cref="IUmaService.ConnectAsync"/>.
    /// Guards against overlapping operations.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_disposed)
            return;

        // ── Overlap prevention ──────────────────────────────────
        var currentState = _connectionState.State;
        if (currentState is not (ConnectionState.Disconnected or ConnectionState.Failed))
            return;

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
                        await RunDiscoveryAsync();
                    }
                }
                else
                {
                    await RunDiscoveryAsync();
                }
            }
        }

        // ── Validate before connect ─────────────────────────────
        if (string.IsNullOrWhiteSpace(DraftAdbPath)
            || string.IsNullOrWhiteSpace(DraftConnectAddress))
        {
            return;
        }

        // ── Connect phase ───────────────────────────────────────
        _connectionState.SetState(ConnectionState.Connecting);

        using var cts = new CancellationTokenSource();
        _connectCts = cts;

        try
        {
            var result = await _umaService.ConnectAsync(
                DraftAdbPath,
                DraftConnectAddress,
                _draft.ConnectConfig,
                cts.Token);

            switch (result)
            {
                case ConnectionSucceededEvent success:
                    HandleConnectSuccess(success);
                    break;

                case ConnectionFailedEvent failure:
                    _connectionState.SetState(ConnectionState.Failed);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _connectionState.SetState(ConnectionState.Disconnected);
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
        _connectCts.Cancel();
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
        _draft.AutoDetectConnection = DraftAutoDetect;
        _draft.AlwaysAutoDetectConnection = DraftAlwaysAutoDetect;
        _draft.Language = DraftLanguage;

        _settingsService.Save(_draft);
    }

    /// <summary>
    /// Runs emulator discovery and applies found values to the draft.
    /// May transition through <see cref="ConnectionState.Detecting"/>.
    /// </summary>
    public async Task AutoDetectEmulatorsAsync()
    {
        await RunDiscoveryAsync();
    }

    // ────────────────────────────────────────────────────────────────
    // Private: discovery
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs emulator process discovery, filters candidates with ADB paths,
    /// optionally asks the user to pick when multiple are found, then runs
    /// adb devices on the selected candidate to find an eligible serial.
    /// </summary>
    private async Task RunDiscoveryAsync()
    {
        _connectionState.SetState(ConnectionState.Detecting);

        var discoveryResult = _winAdapter.RefreshEmulatorsInfo();

        // Filter to candidates with resolvable ADB paths
        var candidates = discoveryResult.Candidates
            .Where(c => c.AdbPath is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            _connectionState.SetState(ConnectionState.Disconnected);
            return;
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
                _connectionState.SetState(ConnectionState.Disconnected);
                return;
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

        // Run adb devices to find a serial
        var devicesResult = _winAdapter.GetAdbDevices(selected.AdbPath!);
        var eligibleDevices = devicesResult.Records
            .Where(r => r.State == "device")
            .ToList();

        if (eligibleDevices.Count > 0)
        {
            // Use the first eligible device (multi-device selection
            // can be added via a seam in a future task)
            DraftConnectAddress = eligibleDevices[0].Serial;
        }

        _connectionState.SetState(ConnectionState.Disconnected);
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

        if (ConnectCommand is RelayCommand rc)
            rc.RaiseCanExecuteChanged();
        if (CancelConnectCommand is RelayCommand rc2)
            rc2.RaiseCanExecuteChanged();
        if (DetectAdbConfigCommand is RelayCommand rc3)
            rc3.RaiseCanExecuteChanged();
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
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;
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
