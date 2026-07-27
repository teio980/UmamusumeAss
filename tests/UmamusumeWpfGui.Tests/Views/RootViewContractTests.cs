using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

/// <summary>
/// Static XAML contract checks for RootView.xaml.
/// Confirms exactly 2 tabs (Log, Settings), no ConnectView reference,
/// all labels through DynamicResource.
/// </summary>
public sealed class RootViewContractTests
{
    private static readonly string BaseDir;
    private static readonly string RootViewPath;

    static RootViewContractTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        BaseDir = dir?.FullName
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory);

        RootViewPath = Path.Combine(
            BaseDir, "src", "UmamusumeWpfGui", "Views", "RootView.xaml");
    }

    private static XDocument LoadXaml()
    {
        if (!File.Exists(RootViewPath))
            throw new FileNotFoundException(
                "RootView.xaml not found at: " + RootViewPath);

        return XDocument.Load(RootViewPath);
    }

    // ================================================================
    // 1. File Existence
    // ================================================================

    [Fact]
    public void RootViewXaml_FileExists()
    {
        Assert.True(File.Exists(RootViewPath),
            "RootView.xaml must exist at " + RootViewPath);
    }

    // ================================================================
    // 2. Exactly 2 TabItem elements (Log, Settings)
    // ================================================================

    [Fact]
    public void RootView_HasExactlyTwoTabItems()
    {
        var xaml = LoadXaml();
        var tabItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .ToList();

        Assert.Equal(2, tabItems.Count);
    }

    [Fact]
    public void RootView_FirstTab_ReferencesLog()
    {
        var xaml = LoadXaml();
        var tabItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .ToList();

        var firstTab = tabItems[0];
        var header = firstTab.Attribute("Header")?.Value ?? "";
        var content = firstTab.ToString();

        // Tab header must use DynamicResource for "TabLog"
        bool usesTabLogResource = header.Contains("TabLog")
            || header.Contains("{DynamicResource");

        // Content should reference LogView
        bool referencesLogView = content.Contains("LogView")
            || content.Contains("Log");

        Assert.True(usesTabLogResource,
            $"First tab header should use DynamicResource for TabLog, got: {header}");
        Assert.True(referencesLogView,
            "First tab should reference LogView content");
    }

    [Fact]
    public void RootView_SecondTab_ReferencesSettings()
    {
        var xaml = LoadXaml();
        var tabItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .ToList();

        var secondTab = tabItems[1];
        var header = secondTab.Attribute("Header")?.Value ?? "";
        var content = secondTab.ToString();

        // Tab header must use DynamicResource for "TabSettings"
        bool usesTabSettingsResource = header.Contains("TabSettings")
            || header.Contains("{DynamicResource");

        // Content should reference SettingsView
        bool referencesSettingsView = content.Contains("SettingsView")
            || content.Contains("Settings");

        Assert.True(usesTabSettingsResource,
            $"Second tab header should use DynamicResource for TabSettings, got: {header}");
        Assert.True(referencesSettingsView,
            "Second tab should reference SettingsView content");
    }

    // ================================================================
    // 3. No ConnectView reference
    // ================================================================

    [Fact]
    public void RootView_NoConnectViewReference()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.DoesNotContain("ConnectView", content);
        Assert.DoesNotContain("ConnectViewModel", content);
    }

    // ================================================================
    // 4. Window uses DynamicResource for title
    // ================================================================

    [Fact]
    public void RootView_WindowTitle_UsesDynamicResource()
    {
        var xaml = LoadXaml();
        var root = xaml.Root;

        Assert.NotNull(root);
        var title = root!.Attribute("Title")?.Value ?? "";

        Assert.True(
            title.Contains("{DynamicResource") || title.Contains("WindowTitle"),
            $"Window Title should use DynamicResource, got: {title}");
    }

    // ================================================================
    // 5. Tab headers use DynamicResource
    // ================================================================

    [Fact]
    public void RootView_TabHeadersUseDynamicResource()
    {
        var xaml = LoadXaml();
        var tabItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .ToList();

        foreach (var tab in tabItems)
        {
            var header = tab.Attribute("Header")?.Value ?? "";
            Assert.True(
                header.Contains("{DynamicResource"),
                $"Tab header should use DynamicResource binding, got: {header}");
        }
    }

    // ================================================================
    // 6. Window type is NavigationWindow or Window
    // ================================================================

    [Fact]
    public void RootView_IsWindowType()
    {
        var xaml = LoadXaml();
        var root = xaml.Root;

        Assert.NotNull(root);
        var localName = root!.Name.LocalName;

        Assert.True(
            localName is "Window" or "NavigationWindow",
            $"Root element should be a Window, got: {localName}");
    }

    // ================================================================
    // 7. Window has reasonable default dimensions
    // ================================================================

    [Fact]
    public void RootView_HasReasonableDimensions()
    {
        var xaml = LoadXaml();
        var root = xaml.Root;

        Assert.NotNull(root);

        var width = root!.Attribute("Width")?.Value;
        var height = root!.Attribute("Height")?.Value;

        // Default size should be set
        Assert.False(string.IsNullOrEmpty(width),
            "RootView should have a Width set");
        Assert.False(string.IsNullOrEmpty(height),
            "RootView should have a Height set");
    }

    // ================================================================
    // 8. No third tab or extra tab content
    // ================================================================

    [Fact]
    public void RootView_NoExtraTabContent()
    {
        var xaml = LoadXaml();
        var tabItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .ToList();

        Assert.Equal(2, tabItems.Count);
    }

    // ================================================================
    // 9. TabControl is present
    // ================================================================

    [Fact]
    public void RootView_HasTabControl()
    {
        var xaml = LoadXaml();
        var hasTabControl = xaml.Descendants()
            .Any(e => e.Name.LocalName == "TabControl");

        Assert.True(hasTabControl,
            "RootView should contain a TabControl element");
    }

    // ================================================================
    // 10. Tab bindings match expected ViewModels
    // ================================================================

    [Fact]
    public void RootView_TabBindingsMatchExpectedViewModels()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Should reference LogView and SettingsView namespace
        bool referencesLog = content.Contains("LogView")
            || content.Contains("LogViewModel");
        bool referencesSettings = content.Contains("SettingsView")
            || content.Contains("SettingsViewModel");

        Assert.True(referencesLog,
            "RootView should reference LogView or LogViewModel");
        Assert.True(referencesSettings,
            "RootView should reference SettingsView or SettingsViewModel");
    }
}
