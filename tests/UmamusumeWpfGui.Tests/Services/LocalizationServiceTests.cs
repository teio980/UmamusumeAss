using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class LocalizationServiceTests
{
    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Creates a test-friendly dictionary factory that returns
    /// in-memory <see cref="ResourceDictionary"/> instances.
    /// </summary>
    private static ResourceDictionary CreateTestDictionary(string culture)
    {
        var dict = new ResourceDictionary();
        if (culture == "en-US")
        {
            dict["TabLog"] = "Log";
            dict["TabSettings"] = "Settings";
            dict["NavConnection"] = "Connection";
            dict["NavLanguage"] = "Language";
            dict["ConnectButton"] = "Connect";
            dict["StatusLabel"] = "Status";
            dict["StateConnected"] = "Connected";
            dict["StateDisconnected"] = "Disconnected";
            dict["LogEmptyMessage"] = "No log entries yet.";
            dict["WindowTitle"] = "UmamusumeAss";
        }
        else if (culture == "zh-CN")
        {
            dict["TabLog"] = "日志";
            dict["TabSettings"] = "设置";
            dict["NavConnection"] = "连接";
            dict["NavLanguage"] = "语言";
            dict["ConnectButton"] = "连接";
            dict["StatusLabel"] = "状态";
            dict["StateConnected"] = "已连接";
            dict["StateDisconnected"] = "已断开";
            dict["LogEmptyMessage"] = "暂无日志条目。";
            dict["WindowTitle"] = "马娘助手";
        }
        return dict;
    }

    private static (FakeSettingsService Settings, ResourceDictionary AppResources, Func<string, ResourceDictionary> Factory) CreateFixture()
    {
        var settings = new FakeSettingsService();
        var appResources = new ResourceDictionary();
        return (settings, appResources, CreateTestDictionary);
    }

    private static LocalizationService CreateService(
        FakeSettingsService settings,
        ResourceDictionary appResources,
        Func<string, ResourceDictionary>? factory = null)
    {
        return new LocalizationService(
            settings,
            appResources,
            factory ?? CreateTestDictionary);
    }

    // ================================================================
    // Default culture
    // ================================================================

    [Fact]
    public void DefaultCulture_IsEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        Assert.Equal("en-US", service.CurrentCulture);
    }

    // ================================================================
    // Initialize — loads persisted culture
    // ================================================================

    [Fact]
    public void Initialize_LoadsPersistedCulture()
    {
        var (settings, appResources, factory) = CreateFixture();
        settings.Save(new ConnectionSettings { Language = "zh-CN" });

        var service = CreateService(settings, appResources, factory);
        service.Initialize();

        Assert.Equal("zh-CN", service.CurrentCulture);
    }

    [Fact]
    public void Initialize_WithDefaultLanguage_StaysEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        // Default ConnectionSettings has Language = "en-US"
        settings.Save(new ConnectionSettings());

        var service = CreateService(settings, appResources, factory);
        service.Initialize();

        Assert.Equal("en-US", service.CurrentCulture);
    }

    [Fact]
    public void Initialize_WithInvalidCulture_StaysEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        settings.Save(new ConnectionSettings { Language = "ja-JP" });

        var service = CreateService(settings, appResources, factory);
        service.Initialize();

        // Invalid culture should not be applied; stays default
        Assert.Equal("en-US", service.CurrentCulture);
    }

    // ================================================================
    // SwitchLanguage — valid cultures
    // ================================================================

    [Fact]
    public void SwitchLanguage_ToZhCn_ChangesCulture()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("zh-CN");

        Assert.Equal("zh-CN", service.CurrentCulture);
    }

    [Fact]
    public void SwitchLanguage_ToZhCn_PersistsToSettings()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("zh-CN");

        var loaded = settings.Load();
        Assert.Equal("zh-CN", loaded.Language);
    }

    [Fact]
    public void SwitchLanguage_ToEnUs_ChangesCulture()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("zh-CN");
        service.SwitchLanguage("en-US");

        Assert.Equal("en-US", service.CurrentCulture);
    }

    // ================================================================
    // SwitchLanguage — invalid, null, empty fallback
    // ================================================================

    [Fact]
    public void SwitchLanguage_Null_FallsBackToEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage(null!);

        Assert.Equal("en-US", service.CurrentCulture);
    }

    [Fact]
    public void SwitchLanguage_Empty_FallsBackToEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("");

        Assert.Equal("en-US", service.CurrentCulture);
    }

    [Fact]
    public void SwitchLanguage_Invalid_FallsBackToEnUs()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("fr-FR");

        Assert.Equal("en-US", service.CurrentCulture);
    }

    // ================================================================
    // SwitchLanguage — same culture is idempotent
    // ================================================================

    [Fact]
    public void SwitchLanguage_SameCulture_DoesNotChange()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("en-US");

        Assert.Equal("en-US", service.CurrentCulture);
    }

    // ================================================================
    // LanguageChanged event
    // ================================================================

    [Fact]
    public void SwitchLanguage_FiresLanguageChanged()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);
        string? capturedCulture = null;
        service.LanguageChanged += (_, culture) => capturedCulture = culture;

        service.SwitchLanguage("zh-CN");

        Assert.Equal("zh-CN", capturedCulture);
    }

    [Fact]
    public void SwitchLanguage_SameCulture_DoesNotFireLanguageChanged()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);
        int callCount = 0;
        service.LanguageChanged += (_, _) => callCount++;

        service.SwitchLanguage("en-US");

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void SwitchLanguage_Invalid_DoesNotFireLanguageChanged()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);
        int callCount = 0;
        service.LanguageChanged += (_, _) => callCount++;

        service.SwitchLanguage("fr-FR");

        // Falls back to en-US which is already the default, so no change
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Initialize_DoesNotFireLanguageChanged()
    {
        var (settings, appResources, factory) = CreateFixture();
        settings.Save(new ConnectionSettings { Language = "zh-CN" });
        var service = CreateService(settings, appResources, factory);
        int callCount = 0;
        service.LanguageChanged += (_, _) => callCount++;

        service.Initialize();

        Assert.Equal(0, callCount);
    }

    // ================================================================
    // GetString
    // ================================================================

    [Fact]
    public void GetString_ReturnsValueFromCurrentDictionary()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        // Switch to en-US to load the dictionary (default is en-US but no
        // dictionary is loaded until SwitchLanguage or Initialize is called).
        service.SwitchLanguage("zh-CN");
        service.SwitchLanguage("en-US");
        var result = service.GetString("TabLog");

        Assert.Equal("Log", result);
    }

    [Fact]
    public void GetString_AfterSwitch_ReturnsNewCultureValue()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        service.SwitchLanguage("zh-CN");
        var result = service.GetString("TabLog");

        Assert.Equal("日志", result);
    }

    [Fact]
    public void GetString_MissingKey_ReturnsKey()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        var result = service.GetString("NonExistentKey");

        Assert.Equal("NonExistentKey", result);
    }

    [Fact]
    public void GetString_BeforeInitialize_ReturnsKey()
    {
        var (settings, appResources, factory) = CreateFixture();
        // No dictionary loaded yet — service was just created
        var service = CreateService(settings, appResources, factory);

        var result = service.GetString("TabLog");

        // No dictionary has been loaded, so key is returned as-is
        Assert.Equal("TabLog", result);
    }

    // ================================================================
    // Resource dictionary replacement preserves theme dictionaries
    // ================================================================

    [Fact]
    public void ReplaceStringDictionary_PreservesNonStringDictionaries()
    {
        var (settings, appResources, factory) = CreateFixture();

        // Add a theme dictionary (simulating Theme.xaml merge).
        // Use a sentinel key instead of Source to avoid WPF URI resolution in tests.
        var themeDict = new ResourceDictionary();
        themeDict["__ThemeMarker__"] = "present";
        appResources.MergedDictionaries.Add(themeDict);

        var service = CreateService(settings, appResources, factory);

        // Switch language — should replace only the string dictionary
        service.SwitchLanguage("zh-CN");

        // Theme dictionary should still be present
        Assert.Contains(appResources.MergedDictionaries, d =>
            d.Contains("__ThemeMarker__"));
    }

    [Fact]
    public void ReplaceStringDictionary_ReplacesPreviousStringDictionary()
    {
        var (settings, appResources, factory) = CreateFixture();
        var service = CreateService(settings, appResources, factory);

        // First switch adds zh-CN dictionary
        service.SwitchLanguage("zh-CN");

        // Verify zh-CN dictionary is present by checking a known key
        Assert.Contains(appResources.MergedDictionaries, d =>
            d.Contains("TabLog") &&
            d["TabLog"] as string == "日志");

        // Switch to en-US
        service.SwitchLanguage("en-US");

        // Should have exactly one string dictionary (en-US)
        var stringDicts = appResources.MergedDictionaries
            .Where(d => d.Contains("TabLog"))
            .ToList();

        Assert.Single(stringDicts);
        Assert.Equal("Log", stringDicts[0]["TabLog"] as string);
    }

    // ================================================================
    // FakeSettingsService
    // ================================================================

    private sealed class FakeSettingsService : ISettingsService
    {
        private ConnectionSettings _settings = new();

        public ConnectionSettings Load() => _settings;

        public void Save(ConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;
        }
    }
}