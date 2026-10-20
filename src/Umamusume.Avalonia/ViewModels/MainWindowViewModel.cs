using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IUmaAutomationService _automationService;
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly IConnectionStateService _connectionStateService;
    private readonly ILocalizationService _localizationService;

    // Sub-page ViewModels
    public TasksViewModel Tasks { get; }
    public ScreenOcrViewModel ScreenOcr { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public DeveloperToolsViewModel DeveloperTools { get; }
    public string SettingsNavigationLabel => _localizationService["NavSettings"];

    // Navigation State
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private string _currentPageTitle = "Hachimi~";

    // Global Status
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _connectionStateText = "Disconnected";

    [ObservableProperty]
    private string _deviceSerial = "-";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _platformInfo = string.Empty;

    private string _currentPageTag = "Hachimi";

    public MainWindowViewModel(
        IUmaAutomationService automationService,
        ISettingsService settingsService,
        ILogService logService,
        IConnectionStateService connectionStateService,
        ILocalizationService localizationService,
        IUmaService coreService)
    {
        _automationService = automationService;
        _settingsService = settingsService;
        _logService = logService;
        _connectionStateService = connectionStateService;
        _localizationService = localizationService;

        _platformInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";

        // Initialize child ViewModels
        Tasks = new TasksViewModel(automationService, logService, connectionStateService, localizationService, settingsService);
        ScreenOcr = new ScreenOcrViewModel(automationService, logService, connectionStateService, coreService);
        Logs = new LogsViewModel(logService);
        Settings = new SettingsViewModel(settingsService, logService, localizationService, automationService);
        DeveloperTools = new DeveloperToolsViewModel(automationService, coreService, logService, connectionStateService);

        // Default to Tasks page
        _currentPage = Tasks;

        // Synchronize connection state globally
        _connectionStateService.StateChanged += (sender, args) =>
        {
            var state = _connectionStateService.State;
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = state == ConnectionState.Connected;
                IsConnecting = state == ConnectionState.Connecting;
                ConnectionStateText = state switch
                {
                    ConnectionState.Connected => _localizationService["Connected"],
                    ConnectionState.Connecting => _localizationService["Connecting"],
                    _ => _localizationService["Disconnected"]
                };

                if (_automationService.CurrentDevice is { } dev)
                {
                    DeviceSerial = dev.Serial;
                }
                else
                {
                    DeviceSerial = "-";
                }
            });
        };

        // Synchronize automation status text
        _automationService.StatusChanged += status =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = status;
            });
        };

        ConnectionStateText = _localizationService["Disconnected"];
        UpdateCurrentPage();
        _localizationService.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(SettingsNavigationLabel));
            UpdateCurrentPage();
            ConnectionStateText = _localizationService[_connectionStateService.State switch
            {
                ConnectionState.Connected => "Connected",
                ConnectionState.Connecting => "Connecting",
                _ => "Disconnected"
            }];
        });
        _logService.Log("Umamusume Assistant · FluentAvalonia ready", LogEntryKind.System);
    }

    [RelayCommand]
    public void Navigate(string pageTag)
    {
        _currentPageTag = pageTag;
        UpdateCurrentPage();
    }

    private void UpdateCurrentPage()
    {
        var isEn = _localizationService.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        switch (_currentPageTag)
        {
            case "Hachimi":
            case "Tasks":
                CurrentPage = Tasks;
                CurrentPageTitle = "Hachimi~";
                break;
            case "ScreenOcr":
                CurrentPage = ScreenOcr;
                CurrentPageTitle = isEn ? "Screen and OCR" : "屏幕与 OCR";
                break;
            case "DeveloperTools":
                CurrentPage = DeveloperTools;
                CurrentPageTitle = isEn ? "Developer Tools" : "开发者工具";
                break;
            case "Logs":
                CurrentPage = Logs;
                CurrentPageTitle = isEn ? "Logs" : "运行日志";
                break;
            case "Settings":
                CurrentPage = Settings;
                CurrentPageTitle = isEn ? "Settings" : "系统设置";
                break;
        }
    }
}
