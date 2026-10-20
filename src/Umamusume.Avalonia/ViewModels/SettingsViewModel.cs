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

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly ILocalizationService _localizationService;
    private readonly IUmaAutomationService _automationService;
    private readonly ReleaseUpdateService _updates = new();
    private ReleaseDownload? _availableRelease;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _connectionCts;
    private bool _isUpdatingLanguage;

    private enum UpdateState
    {
        NotChecked,
        Checking,
        UpToDate,
        UpdateAvailable,
        CheckFailed,
        Downloading,
        Downloaded,
        DownloadFailed,
        Skipped
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

    public SettingsViewModel(ISettingsService settingsService, ILogService logService,
        ILocalizationService localizationService, IUmaAutomationService automationService)
    {
        _settingsService = settingsService; _logService = logService;
        _localizationService = localizationService; _automationService = automationService;
        PlatformInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
        ResourceDirectory = PlatformPaths.GetDefaultResourceDirectory();
        UserDataDirectory = PlatformPaths.GetDefaultUserDataDirectory();
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
            UpdateState.DownloadFailed => english ? $"Download failed: {_updateDetail}" : $"下载未完成：{_updateDetail}",
            UpdateState.Skipped => english ? $"Skipped {_updateDetail}" : $"已跳过 {_updateDetail}",
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

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_availableRelease == null || IsUpdating) return;
        IsUpdating = true;
        _currentUpdateState = UpdateState.Downloading;
        RefreshUpdateStatusText();
        using var cts = new CancellationTokenSource(); _updateCts = cts;
        try
        {
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

    [RelayCommand] private void CancelUpdate() => _updateCts?.Cancel();
    [RelayCommand]
    private void SkipVersion()
    {
        if (_availableRelease == null) return;
        try
        {
            var settings = _settingsService.Load();
            settings.SkippedProgramVersion = _availableRelease.Version.TrimStart('v');
            _settingsService.Save(settings);
            HasUpdate = false;
            _currentUpdateState = UpdateState.Skipped;
            _updateDetail = _availableRelease.Version;
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
