using System.Text.RegularExpressions;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.ViewModels;
using Umamusume.CoreBridge;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed partial class SettingsViewModelTests
{
    private static readonly string[] EnglishThemes = ["Dark", "Light", "System"];
    private static readonly string[] ChineseThemes = ["深色模式 (Dark)", "浅色模式 (Light)", "跟随系统 (System)"];

    private static bool ContainsChinese(string text) =>
        Regex.IsMatch(text, @"[\u4e00-\u9fff]");

    [Fact]
    public void SettingsWhenEnglishHasZeroChineseInAllDisplayedProperties()
    {
        var settingsService = new MemorySettings(new ConnectionSettings { Language = "en-US", Theme = "System" });
        var logService = new LogService();
        var localizationService = new LocalizationService { CurrentLanguage = "en-US" };
        var automationService = new FakeAutomationService();

        var vm = new SettingsViewModel(settingsService, logService, localizationService, automationService);

        Assert.False(ContainsChinese(vm.SelectedTheme), $"SelectedTheme leaked Chinese: {vm.SelectedTheme}");
        Assert.Equal("System", vm.SelectedTheme);

        foreach (var theme in vm.AvailableThemes)
        {
            Assert.False(ContainsChinese(theme), $"AvailableThemes leaked Chinese: {theme}");
        }
        Assert.Equal(EnglishThemes, vm.AvailableThemes);

        Assert.False(ContainsChinese(vm.SelectedLanguage), $"SelectedLanguage leaked Chinese: {vm.SelectedLanguage}");
        Assert.Equal("English (en-US)", vm.SelectedLanguage);

        Assert.False(ContainsChinese(vm.CoreVersion), $"CoreVersion leaked Chinese: {vm.CoreVersion}");
        Assert.StartsWith("Version ", vm.CoreVersion);

        Assert.False(ContainsChinese(vm.NativeCoreStatus), $"NativeCoreStatus leaked Chinese: {vm.NativeCoreStatus}");
        Assert.Contains("Not initialized", vm.NativeCoreStatus);

        Assert.False(ContainsChinese(vm.UpdateStatus), $"UpdateStatus leaked Chinese: {vm.UpdateStatus}");
        Assert.Equal("Not checked", vm.UpdateStatus);
        Assert.Equal("Delete old resources", vm.DeleteOldResourcesLabel);
        Assert.Contains("Removes downloaded updates", vm.DeleteOldResourcesDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWhenChineseDisplaysChineseOptions()
    {
        var settingsService = new MemorySettings(new ConnectionSettings { Language = "zh-CN", Theme = "System" });
        var logService = new LogService();
        var localizationService = new LocalizationService { CurrentLanguage = "zh-CN" };
        var automationService = new FakeAutomationService();

        var vm = new SettingsViewModel(settingsService, logService, localizationService, automationService);

        Assert.Equal("跟随系统 (System)", vm.SelectedTheme);
        Assert.Equal(ChineseThemes, vm.AvailableThemes);
        Assert.Equal("简体中文 (zh-CN)", vm.SelectedLanguage);
        Assert.StartsWith("程序 ", vm.CoreVersion);
        Assert.Contains("尚未初始化", vm.NativeCoreStatus);
        Assert.Equal("尚未检查", vm.UpdateStatus);
        Assert.Equal("删除旧资源", vm.DeleteOldResourcesLabel);
        Assert.Contains("删除已下载的更新", vm.DeleteOldResourcesDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWhenSwitchingLanguageUpdatesAllPropertiesDynamically()
    {
        var settingsService = new MemorySettings(new ConnectionSettings { Language = "zh-CN", Theme = "Dark" });
        var logService = new LogService();
        var localizationService = new LocalizationService { CurrentLanguage = "zh-CN" };
        var automationService = new FakeAutomationService();

        var vm = new SettingsViewModel(settingsService, logService, localizationService, automationService);
        Assert.Equal("深色模式 (Dark)", vm.SelectedTheme);

        // Switch to English
        vm.SelectedLanguage = "English (en-US)";

        Assert.Equal("en-US", localizationService.CurrentLanguage);
        Assert.Equal("Dark", vm.SelectedTheme);
        Assert.False(ContainsChinese(vm.SelectedTheme));
        Assert.False(ContainsChinese(vm.NativeCoreStatus));
        Assert.False(ContainsChinese(vm.CoreVersion));
        Assert.False(ContainsChinese(vm.UpdateStatus));
        Assert.Equal("Not checked", vm.UpdateStatus);
        Assert.StartsWith("Version ", vm.CoreVersion);

        // Switch back to Chinese
        vm.SelectedLanguage = "简体中文 (zh-CN)";

        Assert.Equal("zh-CN", localizationService.CurrentLanguage);
        Assert.Equal("深色模式 (Dark)", vm.SelectedTheme);
        Assert.Contains("尚未初始化", vm.NativeCoreStatus);
        Assert.StartsWith("程序 ", vm.CoreVersion);
        Assert.Equal("尚未检查", vm.UpdateStatus);
    }

    [Fact]
    public void SettingsSavingThemePersistsThemeVariant()
    {
        var settingsService = new MemorySettings(new ConnectionSettings { Language = "en-US", Theme = "System" });
        var logService = new LogService();
        var localizationService = new LocalizationService { CurrentLanguage = "en-US" };
        var automationService = new FakeAutomationService();

        var vm = new SettingsViewModel(settingsService, logService, localizationService, automationService);
        vm.SelectedTheme = "Dark";
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal("Dark", settingsService.Value.Theme);
        Assert.Equal("en-US", settingsService.Value.Language);
    }

    private sealed class MemorySettings(ConnectionSettings initial) : ISettingsService
    {
        public ConnectionSettings Value { get; private set; } = initial;
        public ConnectionSettings Load() => Value;
        public void Save(ConnectionSettings settings) => Value = settings;
    }

    private sealed class FakeAutomationService : IUmaAutomationService
    {
        public bool IsInitialized => false;
        public bool IsConnected => false;
        public bool IsBusy => false;
        public bool RequiresRestartForMaintenance { get; set; }
        public string? NativeCoreVersion => "1.0.0";
        public string? ResourcePath => null;
        public Umamusume.AppCore.Platform.ResourceWorkspace? ActiveResourceWorkspace => null;
        public DeviceSessionIdentity? ActiveDeviceIdentity => null;
        public IUmaService CoreService => throw new NotImplementedException();
        public string CurrentStatus => string.Empty;
        public ConnectedDeviceInfo? CurrentDevice => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<bool>? BusyChanged;
        public event Action<CapturedScreenshot>? ScreenshotCaptured;
        public event Action<string>? ResourcesChanged;
#pragma warning restore CS0067

        public int MaintenanceCallCount { get; private set; }
        public TaskCompletionSource<bool>? MaintenanceEntered { get; set; }
        public TaskCompletionSource<bool>? MaintenanceRelease { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string ResolveActiveResourcePath(string sourcePath) => sourcePath;
        public Task<bool> SaveResourceTextAsync(string path, string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SaveResourceBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ConnectAsync(string adbPath, string serial, string profile = "General", CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapturedScreenshot?> CaptureScreenshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapturedScreenshot?>(null);
        public Task<TextRecognitionResult?> RecognizeOcrAsync(CapturedScreenshot? screenshot = null, string language = "eng", CancellationToken cancellationToken = default) => Task.FromResult<TextRecognitionResult?>(null);
        public Task<bool> StartGameAndRecognizeHomeAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public async Task<T> RunMaintenanceWhenIdleAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            MaintenanceCallCount++;
            MaintenanceEntered?.TrySetResult(true);
            if (MaintenanceRelease is not null)
                await MaintenanceRelease.Task.WaitAsync(cancellationToken);
            return await operation(cancellationToken);
        }
        public void ActivateResourceWorkspace(Umamusume.AppCore.Platform.ResourceWorkspace workspace) { }
        public void RequireRestartAfterMaintenanceFailure(string reason) { }
        public Task<UmaTaskQueueRunResult> RunTaskQueueAsync(IReadOnlyList<UmaTaskRequest> requests, Action<UmaTaskQueueEvent>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new UmaTaskQueueRunResult(true, false, 0, 0, 0, ""));
        public void CancelCurrentOperation() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
