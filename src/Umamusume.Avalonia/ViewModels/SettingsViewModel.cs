using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly ILocalizationService _localizationService;
    private readonly IUmaAutomationService _automationService;
    private readonly ReleaseUpdateService _updates;
    private readonly MacUpdateService _macUpdates;
    private readonly MacResourceStore _macResourceStore;
    private readonly IMacUpdateCacheCleanupService _macCacheCleanup;
    private readonly MacResourceUpdateCoordinator _macResourceCoordinator;
    private ReleaseDownload? _availableRelease;
    private MacUpdateCheck? _macUpdateCheck;
    private StagedUpdate? _stagedProgramUpdate;
    private StagedUpdate? _stagedResourceUpdate;
    private MacUpdateCleanupResult? _cacheCleanupResult;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _connectionCts;
    private bool _isUpdatingLanguage;
    private bool _stagedRestoreAttempted;

    private enum UpdateState
    {
        NotChecked,
        Checking,
        UpToDate,
        UpdateAvailable,
        CheckFailed,
        Downloading,
        Downloaded,
        ProgramReady,
        DownloadFailed,
        Skipped,
        CleaningCache,
        CacheCleaned,
        CacheCleaningCanceled,
        CacheCleaningFailed
    }
    private UpdateState _currentUpdateState = UpdateState.NotChecked;
    private string _updateDetail = string.Empty;

    private bool IsEnglish => _localizationService.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private IReadOnlyList<string> _availableThemes = [];
    [ObservableProperty] private IReadOnlyList<string> _availableLanguages = [];
    [ObservableProperty] private string _selectedTheme = string.Empty;
    [ObservableProperty] private string _selectedLanguage = string.Empty;
    [ObservableProperty] private string _adbPath = string.Empty;
    [ObservableProperty] private string _deviceAddress = string.Empty;
    [ObservableProperty] private string _selectedProfile = "General";
    [ObservableProperty] private bool _autoDetectConnection = true;
    [ObservableProperty] private bool _alwaysAutoDetectConnection;
    [ObservableProperty] private bool _autoStartEmulator;
    [ObservableProperty] private string _emulatorExecutablePath = string.Empty;
    [ObservableProperty] private int _autoStartEmulatorWaitSeconds = 5;
    [ObservableProperty] private int _timeoutSeconds = 15;
    [ObservableProperty] private string _targetPackageIds = string.Empty;
    [ObservableProperty] private string _targetActivityName = string.Empty;
    [ObservableProperty] private bool _startupUpdateCheck;
    [ObservableProperty] private string _platformInfo = string.Empty;
    [ObservableProperty] private string _coreVersion = string.Empty;
    [ObservableProperty] private string _nativeCoreStatus = string.Empty;
    [ObservableProperty] private string _resourceDirectory = string.Empty;
    [ObservableProperty] private string _userDataDirectory = string.Empty;
    [ObservableProperty] private bool _isNotificationOpen;
    [ObservableProperty] private string _notificationTitle = string.Empty;
    [ObservableProperty] private string _notificationMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _notificationSeverity;
    [ObservableProperty] private string _updateStatus = string.Empty;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _hasResourceUpdate;
    [ObservableProperty] private bool _hasProgramStaged;
    [ObservableProperty] private bool _hasResourceStaged;
    [ObservableProperty] private string _resourceUpdateStatus = string.Empty;
    [ObservableProperty] private string _programUpdateProgress = string.Empty;
    [ObservableProperty] private string _programDownloadLabel = "Download app update";
    [ObservableProperty] private string _restartUpdateLabel = "Restart to update";
    [ObservableProperty] private string _resourceDownloadLabel = "Download resource update";
    [ObservableProperty] private string _resourceApplyLabel = "Apply resources";
    [ObservableProperty] private string _deleteOldResourcesLabel = "Delete old resources";
    [ObservableProperty] private string _deleteOldResourcesDescription = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnectionBusy;
    [ObservableProperty] private string _connectionStatus = string.Empty;
    [ObservableProperty] private ConnectedDeviceInfo? _connectedDevice;
    [ObservableProperty] private string _connectedDeviceResolution = string.Empty;
    [ObservableProperty] private bool _shopEnabled = true;
    [ObservableProperty] private bool _shopSelectAll;
    [ObservableProperty] private bool _buyStarPieces;
    [ObservableProperty] private bool _buyAlarmClock;
    [ObservableProperty] private bool _buyPleasingParfait;
    [ObservableProperty] private bool _buyShoes;
    [ObservableProperty] private bool _buySupportPoints;
    [ObservableProperty] private bool _buyFlags;

    public IReadOnlyList<string> AvailableProfiles { get; } = ConnectionSettings.SupportedConnectConfigs;
    public event Action? ConnectionSettingsSaved;

    public bool IsMacUpdateSupported { get; } = OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public SettingsViewModel(ISettingsService settingsService, ILogService logService,
        ILocalizationService localizationService, IUmaAutomationService automationService,
        MacUpdateService? macUpdates = null,
        MacResourceStore? macResourceStore = null,
        IMacUpdateCacheCleanupService? macCacheCleanup = null)
    {
        _settingsService = settingsService; _logService = logService;
        _localizationService = localizationService; _automationService = automationService;
        _updates = new ReleaseUpdateService(localizationService: localizationService);
        _macResourceStore = macResourceStore ?? new MacResourceStore();
        _macUpdates = macUpdates ?? new MacUpdateService(userDataRoot: _macResourceStore.UserDataRoot);
        _macCacheCleanup = macCacheCleanup ?? new MacUpdateCacheCleanupService(_macResourceStore.UserDataRoot);
        _macResourceCoordinator = new MacResourceUpdateCoordinator(_macResourceStore);
        PlatformInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
        ResourceDirectory = PlatformPaths.GetDefaultResourceDirectory();
        UserDataDirectory = _macResourceStore.UserDataRoot;
        RefreshConnectionSettings();

        var settings = settingsService.Load();
        _localizationService.CurrentLanguage = settings.Language;
        UiLocalization.Apply(settings.Language);
        LoadHachimiShopSettings(settings.Hachimi.Shop);

        RefreshConnectionStatus();
        automationService.StatusChanged += _ => RunOnUI(RefreshConnectionStatus);
        automationService.BusyChanged += _ => RunOnUI(RefreshConnectionStatus);

        UpdateLocalizedTexts(settings.Theme);
        ApplyTheme();

        TargetPackageIds = string.Join(Environment.NewLine, settings.TargetPackageIds);
        TargetActivityName = settings.TargetActivityName;
        StartupUpdateCheck = settings.StartupUpdateCheck;

        automationService.StatusChanged += _ => RunOnUI(RefreshCoreStatus);
        automationService.ResourcesChanged += _ => RunOnUI(() => ResourceDirectory = automationService.ResourcePath ?? PlatformPaths.GetDefaultResourceDirectory());
        _localizationService.LanguageChanged += () => RunOnUI(() => UpdateLocalizedTexts());
    }

    private static void RunOnUI(Action action)
    {
        try
        {
            if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                action();
            else
                global::Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
        catch
        {
            action();
        }
    }

    private void UpdateLocalizedTexts(string? preferredTheme = null)
    {
        if (_isUpdatingLanguage) return;
        _isUpdatingLanguage = true;
        try
        {
            bool english = IsEnglish;
            ProgramDownloadLabel = english ? "Download app update" : "下载程序更新";
            RestartUpdateLabel = english ? "Restart to update" : "重启更新";
            ResourceDownloadLabel = english ? "Download resource update" : "下载资源更新";
            ResourceApplyLabel = english ? "Apply resources" : "应用资源";
            DeleteOldResourcesLabel = english ? "Delete old resources" : "删除旧资源";
            DeleteOldResourcesDescription = english
                ? "Removes downloaded updates and unused resource copies. Keeps the active resources, personal settings, and app rollback backup."
                : "删除已下载的更新和未使用的资源副本；保留当前资源、个人设置和程序回滚备份。";

            string currentTheme;
            if (!string.IsNullOrEmpty(preferredTheme))
            {
                currentTheme = preferredTheme;
            }
            else
            {
                currentTheme = SelectedTheme.Contains("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
                    : SelectedTheme.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "System";
            }

            AvailableThemes = english
                ? ["Dark", "Light", "System"]
                : ["深色模式 (Dark)", "浅色模式 (Light)", "跟随系统 (System)"];

            SelectedTheme = currentTheme switch
            {
                "Dark" => AvailableThemes[0],
                "Light" => AvailableThemes[1],
                _ => AvailableThemes[2]
            };

            AvailableLanguages = english
                ? ["Simplified Chinese (zh-CN)", "English (en-US)"]
                : ["简体中文 (zh-CN)", "English (en-US)"];

            SelectedLanguage = english ? AvailableLanguages[1] : AvailableLanguages[0];

            var version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            CoreVersion = english ? $"Version {version}" : $"程序 {version}";

            RefreshCoreStatus();
            RefreshUpdateStatusText();
        }
        finally
        {
            _isUpdatingLanguage = false;
        }
    }

    private void RefreshCoreStatus()
    {
        bool english = IsEnglish;
        NativeCoreStatus = _automationService.IsInitialized
            ? (english
                ? $"Initialized · {_automationService.NativeCoreVersion ?? "unknown version"}"
                : $"已初始化 · {_automationService.NativeCoreVersion ?? "版本未知"}")
            : (english
                ? "Not initialized; loaded when connecting or running diagnostics"
                : "尚未初始化；连接或运行本机诊断时加载核心");
        ResourceDirectory = _automationService.ResourcePath ?? PlatformPaths.GetDefaultResourceDirectory();
    }

    private void RefreshUpdateStatusText()
    {
        bool english = IsEnglish;
        UpdateStatus = _currentUpdateState switch
        {
            UpdateState.NotChecked => english ? "Not checked" : "尚未检查",
            UpdateState.Checking => english ? "Checking for releases for current platform…" : "正在检查适用于本机平台的正式发布…",
            UpdateState.UpToDate => english ? "No newer releases available for the current platform." : "没有高于当前版本且适用于本机平台的正式发布包。",
            UpdateState.UpdateAvailable => english ? $"Update available: {_updateDetail}" : $"可用更新：{_updateDetail}",
            UpdateState.CheckFailed => english ? $"Check failed: {_updateDetail}" : $"检查失败：{_updateDetail}",
            UpdateState.Downloading => english ? "Downloading and verifying update package…" : "正在下载并校验更新包…",
            UpdateState.Downloaded => english ? $"SHA-256 verified: {_updateDetail}. Exit application, extract and replace." : $"SHA-256 已校验：{_updateDetail}。退出程序后解压并替换应用。",
            UpdateState.ProgramReady => english ? $"Verified update {_updateDetail} is ready. Click Restart to update." : $"更新 {_updateDetail} 已验证并准备就绪。点击“重启更新”完成安装。",
            UpdateState.DownloadFailed => english ? $"Download failed: {_updateDetail}" : $"下载未完成：{_updateDetail}",
            UpdateState.Skipped => english ? $"Skipped {_updateDetail}" : $"已跳过 {_updateDetail}",
            UpdateState.CleaningCache => english ? "Deleting old update and resource caches…" : "正在删除旧更新和资源缓存…",
            UpdateState.CacheCleaned => _cacheCleanupResult is { } cleanup ? FormatCleanupResult(cleanup) : _updateDetail,
            UpdateState.CacheCleaningCanceled => english ? "Cache deletion canceled. Staged updates were checked again." : "已取消清理，并重新检查了已暂存的更新。",
            UpdateState.CacheCleaningFailed => english ? $"Cache deletion failed: {_updateDetail}" : $"清理失败：{_updateDetail}",
            _ => UpdateStatus
        };
    }

    public void RefreshConnectionSettings()
    {
        var settings = _settingsService.Load();
        AdbPath = PlatformPaths.FindAdbExecutable(settings.AdbPath);
        DeviceAddress = settings.ConnectAddress; SelectedProfile = settings.ConnectConfig;
        AutoDetectConnection = settings.AutoDetectConnection; AlwaysAutoDetectConnection = settings.AlwaysAutoDetectConnection;
        AutoStartEmulator = settings.AutoStartEmulator; EmulatorExecutablePath = settings.EmulatorExecutablePath;
        AutoStartEmulatorWaitSeconds = settings.AutoStartEmulatorWaitSeconds; TimeoutSeconds = settings.ConnectionTimeoutSeconds;
    }

    private void LoadHachimiShopSettings(HachimiShopSettings shop)
    {
        ShopEnabled = shop.Enabled;
        ShopSelectAll = shop.SelectAll;
        BuyStarPieces = shop.BuyStarPieces;
        BuyAlarmClock = shop.BuyAlarmClock;
        BuyPleasingParfait = shop.BuyPleasingParfait;
        BuyShoes = shop.BuyShoes;
        BuySupportPoints = shop.BuySupportPoints;
        BuyFlags = shop.BuyFlags;
    }

    private void RefreshConnectionStatus()
    {
        IsConnected = _automationService.IsConnected;
        IsConnectionBusy = _automationService.IsBusy;
        ConnectionStatus = _automationService.CurrentStatus;
        ConnectedDevice = _automationService.CurrentDevice;
        ConnectedDeviceResolution = ConnectedDevice is { } device ? $"{device.Width} × {device.Height}" : string.Empty;
    }

    [RelayCommand]
    private async Task ConnectDeviceAsync()
    {
        if (_automationService.IsBusy) return;
        if (string.IsNullOrWhiteSpace(AdbPath) || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            ShowNotification(
                IsEnglish ? "Connection settings required" : "请填写连接配置",
                IsEnglish ? "Enter an ADB path and device address before connecting." : "连接前请填写 ADB 路径和设备地址。",
                InfoBarSeverity.Warning);
            return;
        }

        using var cts = new CancellationTokenSource();
        _connectionCts = cts;
        IsConnecting = true;
        RefreshConnectionStatus();
        try
        {
            bool connected = await _automationService.ConnectAsync(
                AdbPath.Trim(), DeviceAddress.Trim(), SelectedProfile, cts.Token);
            RefreshConnectionStatus();
            if (!connected)
                ShowNotification(IsEnglish ? "Connection failed" : "连接失败", ConnectionStatus, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_connectionCts, cts)) _connectionCts = null;
            IsConnecting = false;
            RefreshConnectionStatus();
        }
    }

    [RelayCommand]
    private void CancelConnection()
    {
        _connectionCts?.Cancel();
        _automationService.CancelCurrentOperation();
    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync()
    {
        await _automationService.DisconnectAsync();
        RefreshConnectionStatus();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        if (_isUpdatingLanguage) return;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = SelectedTheme.Contains("Dark", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark
                : SelectedTheme.Contains("Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Default;
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_isUpdatingLanguage || string.IsNullOrWhiteSpace(value)) return;
        bool toEnglish = value.Contains("en-US", StringComparison.OrdinalIgnoreCase) ||
                         value.Contains("English", StringComparison.OrdinalIgnoreCase);
        var newLang = toEnglish ? "en-US" : "zh-CN";
        if (_localizationService.CurrentLanguage != newLang)
        {
            _localizationService.CurrentLanguage = newLang;
            UiLocalization.Apply(newLang);
        }
        UpdateLocalizedTexts();
    }

    [RelayCommand] private void AutoDetectAdb() => AdbPath = PlatformPaths.FindAdbExecutable();
    [RelayCommand] private async Task BrowseAdbAsync()
    {
        var title = IsEnglish ? "Select ADB executable" : "选择 ADB";
        if (await DesktopActions.PickFileAsync(title) is { } path) AdbPath = path;
    }
    [RelayCommand] private async Task BrowseEmulatorAsync()
    {
        var title = IsEnglish ? "Select emulator .app or executable" : "选择模拟器 .app 或可执行文件";
        if (await DesktopActions.PickFileAsync(title) is { } path) EmulatorExecutablePath = path;
    }
    [RelayCommand] private void OpenResourceDirectory() => Open(ResourceDirectory);
    [RelayCommand] private void OpenUserDataDirectory() => Open(UserDataDirectory);
    [RelayCommand] private void OpenGitHub() => Open("https://github.com/amwx/FluentAvalonia");
    [RelayCommand] private void OpenReleases() => Open(_availableRelease?.PageUrl ?? ReleaseUpdateService.ReleasesUrl);
    private void Open(string target)
    {
        try { DesktopActions.Open(target); }
        catch (Exception ex) { ShowNotification(IsEnglish ? "Failed to open" : "打开失败", ex.Message, InfoBarSeverity.Error); }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.AdbPath = AdbPath.Trim(); settings.ConnectAddress = DeviceAddress.Trim(); settings.ConnectConfig = SelectedProfile;
            settings.Theme = SelectedTheme.Contains("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
                : SelectedTheme.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "System";
            settings.Language = _localizationService.CurrentLanguage;
            settings.ConnectionTimeoutSeconds = TimeoutSeconds; settings.AutoDetectConnection = AutoDetectConnection;
            settings.AlwaysAutoDetectConnection = AlwaysAutoDetectConnection; settings.AutoStartEmulator = AutoStartEmulator;
            settings.EmulatorExecutablePath = EmulatorExecutablePath; settings.AutoStartEmulatorWaitSeconds = AutoStartEmulatorWaitSeconds;
            settings.TargetPackageIds = TargetPackageIds.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
            settings.TargetActivityName = TargetActivityName.Trim(); settings.StartupUpdateCheck = StartupUpdateCheck;
            settings.Hachimi.Shop.Enabled = ShopEnabled;
            settings.Hachimi.Shop.SelectAll = ShopSelectAll;
            settings.Hachimi.Shop.BuyStarPieces = BuyStarPieces;
            settings.Hachimi.Shop.BuyAlarmClock = BuyAlarmClock;
            settings.Hachimi.Shop.BuyPleasingParfait = BuyPleasingParfait;
            settings.Hachimi.Shop.BuyShoes = BuyShoes;
            settings.Hachimi.Shop.BuySupportPoints = BuySupportPoints;
            settings.Hachimi.Shop.BuyFlags = BuyFlags;
            _settingsService.Save(settings);
            ConnectionSettingsSaved?.Invoke();
            ShowNotification(
                IsEnglish ? "Settings saved" : "设置已保存",
                IsEnglish ? "Connection, startup, appearance, update, and shop settings saved." : "连接、启动、外观、更新和商店设置已保存。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotification(IsEnglish ? "Failed to save" : "保存失败", ex.Message, InfoBarSeverity.Error); }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        AdbPath = PlatformPaths.FindAdbExecutable(); DeviceAddress = "127.0.0.1:5555"; SelectedProfile = "General";
        TimeoutSeconds = 15; AutoDetectConnection = true; AlwaysAutoDetectConnection = false; AutoStartEmulator = false;
        EmulatorExecutablePath = ""; AutoStartEmulatorWaitSeconds = 5; TargetPackageIds = ""; TargetActivityName = ""; StartupUpdateCheck = true;
        SelectedTheme = AvailableThemes.Count > 2 ? AvailableThemes[2] : (IsEnglish ? "System" : "跟随系统 (System)");
        ShopEnabled = true; ShopSelectAll = false;
        BuyStarPieces = false; BuyAlarmClock = false; BuyPleasingParfait = false;
        BuyShoes = false; BuySupportPoints = false; BuyFlags = false;
        ShowNotification(
            IsEnglish ? "Defaults restored" : "默认值已填入",
            IsEnglish ? "Click Save Settings to apply." : "点击保存配置后生效。",
            InfoBarSeverity.Informational);
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        _currentUpdateState = UpdateState.Checking;
        RefreshUpdateStatusText();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _updateCts = cts;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string appVersion = MacManifestCompatibility.CurrentAppVersion();
                string resourceVersion = await _macResourceStore.ReadActiveVersionAsync(cts.Token);
                await RestoreStagedUpdatesAsync(appVersion, resourceVersion, cts.Token);
                _macUpdateCheck = await _macUpdates.CheckAsync(appVersion, resourceVersion, cts.Token);
                string? skipped = _settingsService.Load().SkippedProgramVersion;
                HasUpdate = _macUpdateCheck.HasProgramUpdate
                    && !string.Equals(_macUpdateCheck.ProgramManifest!.Version, skipped, StringComparison.OrdinalIgnoreCase);
                HasResourceUpdate = _macUpdateCheck.HasResourceUpdate;
                _availableRelease = null;
                if (HasUpdate)
                {
                    _currentUpdateState = UpdateState.UpdateAvailable;
                    _updateDetail = _macUpdateCheck.ProgramManifest!.Version;
                }
                else if (HasResourceUpdate)
                {
                    _currentUpdateState = UpdateState.UpToDate;
                    _updateDetail = string.Empty;
                }
                else
                {
                    _currentUpdateState = UpdateState.UpToDate;
                    _updateDetail = string.Empty;
                }
                ResourceUpdateStatus = HasResourceUpdate
                    ? (IsEnglish ? $"Resource revision {_macUpdateCheck.ResourceManifest!.Version} is available." : $"资源版本 {_macUpdateCheck.ResourceManifest!.Version} 可用。")
                    : (IsEnglish ? "No compatible resource update available." : "没有适用于当前程序版本的资源更新。");
                RefreshUpdateStatusText();
                return;
            }
            _availableRelease = await _updates.FindUpdateAsync(typeof(SettingsViewModel).Assembly.GetName().Version ?? new Version(0, 0), _settingsService.Load().SkippedProgramVersion, cts.Token);
            HasUpdate = _availableRelease != null;
            if (_availableRelease is { } release)
            {
                _currentUpdateState = UpdateState.UpdateAvailable;
                _updateDetail = $"{release.Version} · {release.FileName}";
            }
            else
            {
                _currentUpdateState = UpdateState.UpToDate;
                _updateDetail = string.Empty;
            }
            RefreshUpdateStatusText();
        }
        catch (Exception ex)
        {
            _currentUpdateState = UpdateState.CheckFailed;
            _updateDetail = ex.Message;
            RefreshUpdateStatusText();
            _logService.Log(UpdateStatus, LogEntryKind.Warning);
        }
        finally { _updateCts = null; IsUpdating = false; }
    }

    /// <summary>Restores locally staged macOS packages without making a network request.</summary>
    public async Task RestoreStagedUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsMacUpdateSupported || _stagedRestoreAttempted || IsUpdating) return;
        IsUpdating = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _updateCts = cts;
        try
        {
            string appVersion = MacManifestCompatibility.CurrentAppVersion();
            string resourceVersion = await _macResourceStore.ReadActiveVersionAsync(cts.Token);
            await RestoreStagedUpdatesAsync(appVersion, resourceVersion, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _stagedRestoreAttempted = false;
        }
        catch (Exception exception)
        {
            _stagedRestoreAttempted = false;
            _logService.Log($"Could not restore downloaded updates: {exception.Message}", LogEntryKind.Warning);
        }
        finally
        {
            _updateCts = null;
            IsUpdating = false;
        }
    }

    private async Task RestoreStagedUpdatesAsync(
        string appVersion,
        string resourceVersion,
        CancellationToken cancellationToken)
    {
        if (_stagedRestoreAttempted) return;
        _stagedRestoreAttempted = true;
        await RefreshStagedUpdatesAsync(appVersion, resourceVersion, cancellationToken);
    }

    private async Task RefreshStagedUpdatesAsync(
        string appVersion,
        string resourceVersion,
        CancellationToken cancellationToken)
    {
        var restored = await Task.Run(() => (
            Program: _macUpdates.RestoreStaged(UpdateScope.Program, appVersion, resourceVersion),
            Resource: _macUpdates.RestoreStaged(UpdateScope.Resource, appVersion, resourceVersion)), cancellationToken);
        _stagedProgramUpdate = restored.Program;
        _stagedResourceUpdate = restored.Resource;
        HasProgramStaged = _stagedProgramUpdate is not null;
        HasResourceStaged = _stagedResourceUpdate is not null;
        if (_stagedProgramUpdate is not null)
            ProgramUpdateProgress = IsEnglish
                ? $"Verified downloaded app update {_stagedProgramUpdate.Manifest.Version} is ready to install."
                : $"已重新验证应用更新 {_stagedProgramUpdate.Manifest.Version}，可以重启安装。";
        if (_stagedResourceUpdate is not null)
            ResourceUpdateStatus = IsEnglish
                ? $"Resource revision {_stagedResourceUpdate.Manifest.Version} is verified. Click Apply resources to activate it."
                : $"资源版本 {_stagedResourceUpdate.Manifest.Version} 已重新验证。点击“应用资源”后才会生效。";
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        _currentUpdateState = UpdateState.Downloading;
        RefreshUpdateStatusText();
        using var cts = new CancellationTokenSource(); _updateCts = cts;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (_macUpdateCheck is null || !_macUpdateCheck.HasProgramUpdate) return;
                _stagedProgramUpdate = await _macUpdates.DownloadProgramAsync(_macUpdateCheck,
                    new Progress<UpdateProgress>(value => ProgramUpdateProgress = value.Stage), cts.Token);
                _macUpdates.RememberStaged(_stagedProgramUpdate);
                HasProgramStaged = true;
                HasUpdate = false;
                _currentUpdateState = UpdateState.ProgramReady;
                _updateDetail = _stagedProgramUpdate.Manifest.Version;
                RefreshUpdateStatusText();
                return;
            }
            if (_availableRelease is null) return;
            var target = await _updates.DownloadAsync(_availableRelease, Path.Combine(UserDataDirectory, "updates"), cts.Token);
            _currentUpdateState = UpdateState.Downloaded;
            _updateDetail = target;
            RefreshUpdateStatusText();
            DesktopActions.Open(Path.GetDirectoryName(target)!);
        }
        catch (Exception ex)
        {
            _currentUpdateState = UpdateState.DownloadFailed;
            _updateDetail = ex.Message;
            RefreshUpdateStatusText();
        }
        finally { _updateCts = null; IsUpdating = false; }
    }

    [RelayCommand]
    private async Task RestartToInstallUpdateAsync()
    {
        if (_stagedProgramUpdate is null || IsUpdating) return;
        IsUpdating = true;
        using var cts = new CancellationTokenSource(); _updateCts = cts;
        try
        {
            ProgramUpdateProgress = IsEnglish ? "Waiting for current operations to finish…" : "等待当前操作结束…";
            var progress = new Progress<string>(stage => ProgramUpdateProgress = stage);
            await _automationService.RunMaintenanceWhenIdleAsync(async token =>
            {
                await MacProgramInstaller.BeginInstallAndExitAsync(_stagedProgramUpdate, progress, token);
                return true;
            }, cts.Token);
        }
        catch (Exception ex)
        {
            _currentUpdateState = UpdateState.DownloadFailed;
            _updateDetail = ex.Message;
            RefreshUpdateStatusText();
            ShowNotification(IsEnglish ? "Could not start update" : "无法启动更新", ex.Message, InfoBarSeverity.Error);
        }
        finally { _updateCts = null; IsUpdating = false; }
    }

    [RelayCommand]
    private async Task DownloadResourcesAsync()
    {
        if (_macUpdateCheck is null || !_macUpdateCheck.HasResourceUpdate || IsUpdating) return;
        IsUpdating = true;
        using var cts = new CancellationTokenSource(); _updateCts = cts;
        ResourceUpdateStatus = IsEnglish ? "Downloading and verifying resource revision…" : "正在下载并验证资源版本…";
        try
        {
            _stagedResourceUpdate = await _macUpdates.DownloadResourceAsync(_macUpdateCheck,
                new Progress<UpdateProgress>(value => ResourceUpdateStatus = value.Stage), cts.Token);
            _macUpdates.RememberStaged(_stagedResourceUpdate);
            HasResourceStaged = true;
            HasResourceUpdate = false;
            ResourceUpdateStatus = IsEnglish
                ? $"Resource revision {_stagedResourceUpdate.Manifest.Version} is verified. Click Apply resources to activate it."
                : $"资源版本 {_stagedResourceUpdate.Manifest.Version} 已验证。点击“应用资源”后才会生效。";
        }
        catch (Exception ex)
        {
            ResourceUpdateStatus = (IsEnglish ? "Resource download failed: " : "资源下载失败：") + ex.Message;
        }
        finally { _updateCts = null; IsUpdating = false; }
    }

    [RelayCommand]
    private async Task ApplyResourcesAsync()
    {
        if (_stagedResourceUpdate is null || IsUpdating) return;
        IsUpdating = true;
        using var cts = new CancellationTokenSource(); _updateCts = cts;
        ResourceUpdateStatus = IsEnglish ? "Waiting for native operations to finish…" : "等待当前原生操作结束…";
        try
        {
            string version = await _macResourceCoordinator.ApplyAsync(_stagedResourceUpdate, _automationService, cts.Token);
            HasResourceStaged = false;
            HasResourceUpdate = false;
            _macUpdates.ForgetStaged(UpdateScope.Resource);
            _stagedResourceUpdate = null;
            ResourceUpdateStatus = IsEnglish ? $"Resource revision {version} is active." : $"资源版本 {version} 已应用。";
        }
        catch (Exception ex)
        {
            ResourceUpdateStatus = (IsEnglish ? "Resource apply failed: " : "应用资源失败：") + ex.Message;
        }
        finally { _updateCts = null; IsUpdating = false; }
    }

    [RelayCommand]
    private async Task DeleteOldResourcesAsync()
    {
        if (!IsMacUpdateSupported || IsUpdating) return;
        IsUpdating = true;
        _cacheCleanupResult = null;
        _currentUpdateState = UpdateState.CleaningCache;
        RefreshUpdateStatusText();
        ProgramUpdateProgress = IsEnglish ? "Waiting for current operations to finish…" : "等待当前原生操作结束…";
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        var progress = new Progress<MacUpdateCleanupStage>(stage =>
            ProgramUpdateProgress = (stage, IsEnglish) switch
            {
                (MacUpdateCleanupStage.Scanning, true) => "Scanning update and resource caches…",
                (MacUpdateCleanupStage.Scanning, false) => "正在扫描更新和资源缓存…",
                (MacUpdateCleanupStage.RemovingDownloads, true) => "Removing downloaded update packages…",
                (MacUpdateCleanupStage.RemovingDownloads, false) => "正在删除已下载的更新包…",
                (MacUpdateCleanupStage.RemovingResourceRevisions, true) => "Removing unused resource revisions…",
                (MacUpdateCleanupStage.RemovingResourceRevisions, false) => "正在删除未使用的资源版本…",
                (MacUpdateCleanupStage.RemovingResourceViews, true) => "Removing inactive resource views…",
                _ => "正在删除未使用的资源视图…"
            });
        try
        {
            MacUpdateCleanupResult result = await _automationService.RunMaintenanceWhenIdleAsync(
                token => _automationService.RequiresRestartForMaintenance
                    ? Task.FromResult(new MacUpdateCleanupResult(0, 0, 0, 0,
                        InstallerRunning: false, ActivePointerInvalid: false, MaintenanceRestartRequired: true))
                    : _macCacheCleanup.ClearAsync(_automationService.ActiveResourceWorkspace, progress, token),
                cts.Token);
            _currentUpdateState = UpdateState.CacheCleaned;
            _cacheCleanupResult = result;
            _updateDetail = FormatCleanupResult(result);
            RefreshUpdateStatusText();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _currentUpdateState = UpdateState.CacheCleaningCanceled;
            RefreshUpdateStatusText();
        }
        catch (Exception exception)
        {
            _currentUpdateState = UpdateState.CacheCleaningFailed;
            _updateDetail = exception.Message;
            RefreshUpdateStatusText();
        }
        finally
        {
            try
            {
                _stagedRestoreAttempted = true;
                string appVersion = MacManifestCompatibility.CurrentAppVersion();
                string resourceVersion = await _macResourceStore.ReadActiveVersionAsync(CancellationToken.None);
                await RefreshStagedUpdatesAsync(appVersion, resourceVersion, CancellationToken.None);
                if (!HasProgramStaged) ProgramUpdateProgress = string.Empty;
                if (!HasResourceStaged) ResourceUpdateStatus = string.Empty;
                if (_macUpdateCheck is { } check)
                {
                    HasUpdate = check.HasProgramUpdate && !HasProgramStaged
                        && IsProgramVersionNewer(check.ProgramManifest!.Version, appVersion)
                        && check.ProgramManifest.Version != _settingsService.Load().SkippedProgramVersion;
                    HasResourceUpdate = check.HasResourceUpdate && !HasResourceStaged
                        && IsResourceRevisionNewer(check.ResourceManifest!.Version, resourceVersion);
                }
            }
            catch (Exception exception)
            {
                _stagedProgramUpdate = null;
                _stagedResourceUpdate = null;
                HasProgramStaged = false;
                HasResourceStaged = false;
                ProgramUpdateProgress = string.Empty;
                ResourceUpdateStatus = string.Empty;
                _logService.Log($"Could not restore staged updates after cache cleanup: {exception.Message}", LogEntryKind.Warning);
            }
            _updateCts = null;
            IsUpdating = false;
        }
    }

    private string FormatCleanupResult(MacUpdateCleanupResult result)
    {
        if (result.MaintenanceRestartRequired)
        {
            return IsEnglish
                ? "Restart the application before deleting caches because resource recovery is incomplete."
                : "资源恢复尚未完成，请先重启应用，再删除缓存。";
        }

        if (result.InstallerRunning)
        {
            return IsEnglish
                ? "An installer or recovery helper is still running. Update caches were kept."
                : "安装或恢复辅助程序仍在运行，已保留更新缓存。";
        }

        string message = IsEnglish
            ? $"Removed {result.RemovedOperations} update cache item(s), {result.RemovedRevisions} unused resource revision(s), and {result.RemovedViews} inactive resource view(s)."
            : $"已删除 {result.RemovedOperations} 个更新缓存项、{result.RemovedRevisions} 个未使用资源版本和 {result.RemovedViews} 个未使用资源视图。";
        if (result.PreservedOperations > 0)
        {
            message += IsEnglish
                ? $" Kept {result.PreservedOperations} update operation(s) awaiting installer recovery."
                : $"另保留了 {result.PreservedOperations} 个等待安装器恢复的更新操作。";
        }
        if (result.ActivePointerInvalid)
        {
            message += IsEnglish
                ? " Resource revisions were retained because the active-resource pointer could not be validated."
                : "由于无法验证当前资源指针，已保留全部资源版本。";
        }
        return message;
    }

    private static bool IsProgramVersionNewer(string candidate, string current) =>
        SemVersion.TryParse(candidate, out var available)
        && SemVersion.TryParse(current, out var installed)
        && available > installed;

    private static bool IsResourceRevisionNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var available)
        && Version.TryParse(current, out var installed)
        && available > installed;

    [RelayCommand] private void CancelUpdate() => _updateCts?.Cancel();
    [RelayCommand]
    private void SkipVersion()
    {
        string? version = OperatingSystem.IsMacOS()
            ? _macUpdateCheck?.ProgramManifest?.Version
            : _availableRelease?.Version.TrimStart('v');
        if (string.IsNullOrWhiteSpace(version)) return;
        try
        {
            var settings = _settingsService.Load();
            settings.SkippedProgramVersion = version;
            _settingsService.Save(settings);
            HasUpdate = false;
            _currentUpdateState = UpdateState.Skipped;
            _updateDetail = version;
            RefreshUpdateStatusText();
        }
        catch (Exception ex) { ShowNotification(IsEnglish ? "Failed to save" : "保存失败", ex.Message, InfoBarSeverity.Error); }
    }
    [RelayCommand] private void DismissNotification() => IsNotificationOpen = false;
    private void ShowNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title; NotificationMessage = message; NotificationSeverity = severity; IsNotificationOpen = true;
        _logService.Log($"{title}: {message}", severity == InfoBarSeverity.Error ? LogEntryKind.Error : LogEntryKind.Normal);
    }
}
