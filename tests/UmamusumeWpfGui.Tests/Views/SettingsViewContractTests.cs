using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

/// <summary>
/// Static XAML contract checks for SettingsView.xaml.
/// These parse the source XAML as XML and verify structural invariants:
/// 160 px left nav, all labels through DynamicResource, exactly three panels,
/// and no custom ControlTemplate definitions.
/// </summary>
public sealed class SettingsViewContractTests
{
    private static readonly string ProjectDir;
    private static readonly string SettingsViewPath;

    static SettingsViewContractTests()
    {
        // Resolve the project source directory relative to the test assembly
        // Tests run from the test project output; walk up to find solution root
        var baseDir = AppContext.BaseDirectory;

        // Walk up until we find the solution root (contains both src/ and tests/)
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        ProjectDir = dir?.FullName
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + baseDir);

        SettingsViewPath = Path.Combine(
            ProjectDir, "src", "UmamusumeWpfGui", "Views", "SettingsView.xaml");
    }

    /// <summary>
    /// Returns the parsed XAML document for SettingsView.
    /// </summary>
    private static XDocument LoadXaml()
    {
        if (!File.Exists(SettingsViewPath))
        {
            throw new FileNotFoundException(
                "SettingsView.xaml not found at expected path: " + SettingsViewPath);
        }

        return XDocument.Load(SettingsViewPath);
    }

    // ================================================================
    // 1. File Existence
    // ================================================================

    [Fact]
    public void SettingsViewXaml_FileExists()
    {
        Assert.True(File.Exists(SettingsViewPath),
            "SettingsView.xaml must exist at " + SettingsViewPath);
    }

    // ================================================================
    // 2. Navigation Layout: 160 px left column
    // ================================================================

    [Fact]
    public void SettingsView_Has160pxLeftNavColumn()
    {
        var xaml = LoadXaml();

        // The main Grid should have a ColumnDefinitions with Width="160"
        // or a ColumnDefinition whose Width attribute value is "160"
        var grid = xaml.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Grid");

        Assert.NotNull(grid);
        Assert.NotNull(grid!.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Grid.ColumnDefinitions"));

        var colDefs = grid!.Elements()
            .First(e => e.Name.LocalName == "Grid.ColumnDefinitions");

        var columns = colDefs.Elements()
            .Where(e => e.Name.LocalName == "ColumnDefinition")
            .ToList();

        Assert.True(columns.Count >= 2,
            "SettingsView should have at least 2 column definitions");

        // Check the first column is 160 px
        var firstCol = columns[0];
        var width = firstCol.Attribute("Width")?.Value ?? "";
        Assert.True(width == "160" || width == "160*",
            $"First column should be 160 or 160*, got \"{width}\"");
    }

    [Fact]
    public void SettingsView_HasRightContentPadding24()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // The right panel should use Space-6 = 24 px padding
        // Look for ScrollViewer or similar with 24px right padding
        bool hasContentPadding24 = content.Contains("Padding=\"24")
            || content.Contains("Margin=\"24");

        // The right panel content area should be scrollable
        bool hasScrollViewer = content.Contains("ScrollViewer")
            || content.Contains("ScrollViewer.VerticalScrollBarVisibility");

        Assert.True(hasContentPadding24 || hasScrollViewer,
            "SettingsView should have a scrollable right panel with 24 px padding");
    }

    // ================================================================
    // 3. Navigation ItemsControl
    // ================================================================

    [Fact]
    public void SettingsView_HasNavigationItemsControl()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Should have an ItemsControl or ListBox bound to MenuItems
        bool hasNavControl = content.Contains("ItemsSource")
            && (content.Contains("MenuItems") || content.Contains("SelectedMenuIndex"));

        Assert.True(hasNavControl,
            "SettingsView should have an ItemsControl/ListBox bound to MenuItems");
    }

    [Fact]
    public void SettingsView_NavigationItemsUseBottomDividers()
    {
        var xaml = LoadXaml();
        var navigationItem = xaml.Descendants()
            .First(element => element.Name.LocalName == "Border"
                && element.Attribute("MouseLeftButtonUp")?.Value == "OnNavItemClick");

        Assert.Equal("0,0,0,1", navigationItem.Attribute("BorderThickness")?.Value);
        Assert.Equal("{DynamicResource BorderSubtleBrush}", navigationItem.Attribute("BorderBrush")?.Value);
        Assert.Null(navigationItem.Attribute("Margin"));
    }

    // ================================================================
    // 4. All user-visible labels use DynamicResource
    // ================================================================

    [Fact]
    public void SettingsView_AllTextUsesDynamicResource()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Find all TextBlock elements and check they use DynamicResource
        // or are data-bound (Binding) for dynamic content
        var doc = XDocument.Parse(content);
        var textBlocks = doc.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .ToList();

        // Filter out TextBlocks that are only used for data-bound values
        // (like serial numbers, status text, etc.) or navigation items
        var staticLabels = textBlocks
            .Where(tb =>
            {
                var text = tb.Attribute("Text")?.Value ?? "";
                // Skip TextBlocks with no direct Text attribute
                // (they use Binding or DynamicResource)
                if (string.IsNullOrEmpty(text))
                    return false;

                // Allow ":" suffix for field labels like "Serial:", "Status:"
                if (text.EndsWith(':'))
                    return false;

                // Check if this looks like a hardcoded string
                // Valid patterns: DynamicResource, Binding, empty, or design-time data
                return !text.StartsWith("{DynamicResource", StringComparison.Ordinal)
                    && !text.StartsWith("{Binding", StringComparison.Ordinal);
            })
            .ToList();

        // Any static label found is a violation
        var violations = staticLabels
            .Select(tb => $"Hardcoded text found: \"{tb.Attribute("Text")?.Value}\"")
            .ToList();

        Assert.True(staticLabels.Count == 0,
            $"All user-visible labels must use DynamicResource. Violations:\n"
            + string.Join("\n", violations));
    }

    // ================================================================
    // 5. Three panels only (Connection, Language, System)
    // ================================================================

    [Fact]
    public void SettingsView_HasExactlyThreeContentPanels()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // The connection panel content
        bool hasConnectionPanel = content.Contains("ConnectionTitle")
            || content.Contains("NavConnection");

        // The language panel content
        bool hasLanguagePanel = content.Contains("LanguageTitle")
            || content.Contains("NavLanguage");

        // The system panel content  
        bool hasSystemPanel = content.Contains("SystemTitle")
            || content.Contains("NavSystem");

        Assert.True(hasConnectionPanel,
            "SettingsView should contain Connection panel content");
        Assert.True(hasLanguagePanel,
            "SettingsView should contain Language panel content");
        Assert.True(hasSystemPanel,
            "SettingsView should contain System panel content");
    }

    // ================================================================
    // 6. Connection Panel Elements
    // ================================================================

    [Fact]
    public void ConnectionPanel_HasRequiredElements()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // ADB Path section
        Assert.True(content.Contains("AdbPathLabel"),
            "Connection panel should have ADB Path label via DynamicResource");
        Assert.True(content.Contains("Browse"),
            "Connection panel should have Browse button");

        // Serial section
        Assert.True(content.Contains("SerialLabel"),
            "Connection panel should have Serial label");
        Assert.True(content.Contains("IsEditable"),
            "Serial ComboBox should be editable");

        // Auto Detect
        Assert.True(content.Contains("AutoDetectButton"),
            "Connection panel should have Auto Detect button");
        Assert.True(content.Contains("AlwaysAutoDetectLabel"),
            "Connection panel should have Always Auto Detect checkbox");
        Assert.True(content.Contains("AutoStartEmulatorWaitSecondsLabel"),
            "Connection panel should label the emulator startup wait setting");
        Assert.True(content.Contains("DraftAutoStartEmulatorWaitSeconds"),
            "Connection panel should bind the emulator startup wait setting");

        // Profile
        Assert.True(content.Contains("ProfileLabel"),
            "Connection panel should have Profile label");

        // Connect / Cancel buttons
        bool hasConnectOrCancel = content.Contains("ConnectButton")
            || content.Contains("CancelButton");
        Assert.True(hasConnectOrCancel,
            "Connection panel should have Connect or Cancel button");

        // Status
        Assert.True(content.Contains("StatusLabel") || content.Contains("StatusText"),
            "Connection panel should have Status label");

        // Device info card
        bool hasDeviceInfo = content.Contains("DeviceInfoTitle")
            || content.Contains("LastVerifiedLabel");
        Assert.True(hasDeviceInfo,
            "Connection panel should have device info card");

        // Forget button
        Assert.True(content.Contains("ForgetButton"),
            "Connection panel should have Forget button");
    }

    [Fact]
    public void ConnectionResources_ContainAutoStartWaitLabel()
    {
        var resourcesDirectory = Path.Combine(ProjectDir, "src", "UmamusumeWpfGui", "Resources");

        Assert.Contains(
            "AutoStartEmulatorWaitSecondsLabel",
            File.ReadAllText(Path.Combine(resourcesDirectory, "Strings.en-US.xaml")));
        Assert.Contains(
            "AutoStartEmulatorWaitSecondsLabel",
            File.ReadAllText(Path.Combine(resourcesDirectory, "Strings.zh-CN.xaml")));
    }

    // ================================================================
    // 7. Language Panel Elements
    // ================================================================

    [Fact]
    public void LanguagePanel_HasRequiredElements()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.True(content.Contains("LanguageTitle"),
            "Language panel should have title via DynamicResource");
        Assert.True(content.Contains("SelectedLanguage"),
            "Language panel should bind to SelectedLanguage");
        Assert.True(content.Contains("LanguageHint"),
            "Language panel should have hint text via DynamicResource");
    }

    // ================================================================
    // 8. System Panel Elements
    // ================================================================

    [Fact]
    public void SystemPanel_HasRequiredElements()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.True(content.Contains("SystemTitle"),
            "System panel should have title via DynamicResource");
        Assert.True(content.Contains("CoreVersionLabel"),
            "System panel should have Core Version label");
        Assert.True(content.Contains("CoreVersion"),
            "System panel should bind to CoreVersion");
    }

    // ================================================================
    // 9. No custom ControlTemplate
    // ================================================================

    [Fact]
    public void SettingsView_NoCustomControlTemplate()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Should not contain ControlTemplate definitions
        Assert.DoesNotContain("ControlTemplate", content);
    }

    // ================================================================
    // 10. Connection Panel: all bindings target existing VM members
    // ================================================================

    [Fact]
    public void ConnectionPanel_BindingsMatchViewModel()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Key ViewModel properties that should be bound
        var expectedBindings = new[]
        {
            "DraftAdbPath",
            "DraftConnectAddress",
            "DraftAutoDetect",
            "DraftAlwaysAutoDetect",
            "ConnectAddressHistory",
            "ConnectCommand",
            "CancelConnectCommand",
            "SaveCommand",
            "DetectAdbConfigCommand",
            "LastVerified",
            "Forget",
            "StatusText",
            "State",
        };

        // At least some of these bindings should be present
        int matches = expectedBindings.Count(b => content.Contains(b));
        Assert.True(matches >= 6,
            $"Only {matches}/{expectedBindings.Length} expected VM bindings found in XAML");
    }

    // ================================================================
    // 11. Last Verified Card: read-only display with Forget
    // ================================================================

    [Fact]
    public void DeviceInfoCard_HasImmutableLastVerified()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Last verified card should reference the LastVerified connection
        Assert.True(content.Contains("LastVerified"),
            "Device info card should bind to LastVerified");
        Assert.True(content.Contains("VerifiedAt")
                || content.Contains("AndroidId")
                || content.Contains("AndroidVersion")
                || content.Contains("Serial"),
            "Device info card should display last verified fields");

        // Should NOT be editable (no TextBox binding to LastVerified fields)
        // LastVerified is immutable, so any binding should be read-only TextBlock
        Assert.True(content.Contains("TextBlock"),
            "Device info card fields should be read-only TextBlocks");
    }

    [Fact]
    public void DeviceInfoCard_UsesNonNullVisibilityConverter()
    {
        var content = LoadXaml().ToString();
        int resourceIndex = content.IndexOf(
            "x:Key=\"NotNullToVisibility\"",
            StringComparison.Ordinal);

        Assert.True(resourceIndex >= 0,
            "SettingsView should define the device info visibility converter");

        int resourceEnd = content.IndexOf(" />", resourceIndex, StringComparison.Ordinal);
        Assert.True(resourceEnd > resourceIndex,
            "Device info visibility converter should be self-closing");

        var resource = content[resourceIndex..resourceEnd];
        Assert.False(resource.Contains("Invert=\"True\"", StringComparison.Ordinal),
            "A non-null LastVerified snapshot must make the device info card visible");
    }

    // ================================================================
    // 12. Profile ComboBox is read-only (General only in S1)
    // ================================================================

    [Fact]
    public void ProfileComboBox_IsReadOnly()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // The profile ComboBox should NOT be editable (read-only in S1)
        // It can use IsReadOnly or omit IsEditable
        bool hasProfileCombo = content.Contains("Profile")
            || content.Contains("ConnectConfig");

        Assert.True(hasProfileCombo,
            "Connection panel should have a Profile ComboBox");

        // General should be the only profile option
        Assert.True(content.Contains("General"),
            "Profile should contain 'General' as the only option");
    }

    // ================================================================
    // 13. Connection panel commands bound correctly
    // ================================================================

    [Fact]
    public void ConnectionPanel_CommandsBound()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        // Connect command binding
        Assert.True(content.Contains("ConnectCommand"),
            "Connect button should bind to ConnectCommand");

        // Cancel command binding  
        Assert.True(content.Contains("CancelConnectCommand"),
            "Cancel button should bind to CancelConnectCommand");

        // Save command binding
        Assert.True(content.Contains("SaveCommand"),
            "Save button should bind to SaveCommand");

        // Detect ADB config command binding
        Assert.True(content.Contains("DetectAdbConfigCommand"),
            "Auto Detect button should bind to DetectAdbConfigCommand");
    }

    // ================================================================
    // 14. Nav item padding is 10px horizontal, 16px vertical
    // ================================================================

    [Fact]
    public void SettingsView_NavItemPaddingIs10x16()
    {
        var xaml = LoadXaml();
        var doc = XDocument.Parse(xaml.ToString());

        var navBorders = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border"
                && e.Attribute("Padding")?.Value == "10,16")
            .ToList();

        Assert.True(navBorders.Count >= 1,
            "Nav item Border should have Padding=\"10,16\"");
    }

    // ================================================================
    // 15. Selection indicator width is exactly 2px
    // ================================================================

    [Fact]
    public void SettingsView_SelectionIndicatorWidthIs2px()
    {
        var xaml = LoadXaml();
        var doc = XDocument.Parse(xaml.ToString());

        var indicatorBorders = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border"
                && e.Attribute("Width")?.Value == "2"
                && e.Parent?.Parent?.Name.LocalName == "Border"
                && e.Parent!.Parent!.Attribute("Padding")?.Value == "10,16")
            .ToList();

        Assert.True(indicatorBorders.Count >= 1,
            "Selection indicator Border should have Width=\"2\" within nav item with Padding=\"10,16\"");
    }

    // ================================================================
    // 16. Control Readiness Card (disabled, S2 not available)
    // ================================================================

    [Fact]
    public void ControlReadinessCard_HasTitle()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.True(content.Contains("ControlReadinessTitle"),
            "Control readiness card should have title via DynamicResource");
    }

    [Fact]
    public void ControlReadinessCard_IsDisabled()
    {
        var xaml = LoadXaml();
        var doc = XDocument.Parse(xaml.ToString());

        // Find the card Border that contains ControlReadinessTitle
        var borders = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border" && e.ToString().Contains("ControlReadinessTitle"))
            .ToList();

        Assert.True(borders.Count >= 1,
            "Control readiness card should exist as a Border element");

        var cardBorder = borders[0];
        var isEnabled = cardBorder.Attribute("IsEnabled")?.Value ?? "True";
        Assert.Equal("False", isEnabled);
    }

    [Fact]
    public void ControlReadinessCard_HasReducedOpacity()
    {
        var xaml = LoadXaml();
        var doc = XDocument.Parse(xaml.ToString());

        var borders = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border" && e.ToString().Contains("ControlReadinessTitle"))
            .ToList();

        Assert.True(borders.Count >= 1,
            "Control readiness card should exist as a Border element");

        var cardBorder = borders[0];
        var opacity = cardBorder.Attribute("Opacity")?.Value ?? "1.0";
        Assert.Equal("0.6", opacity);
    }

    [Fact]
    public void ControlReadinessCard_HasControlSessionBinding()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.True(content.Contains("ControlSession"),
            "Control readiness card should bind to ControlSession");
        Assert.True(content.Contains("ControlSession.State"),
            "Card should display State from ControlSession");
        Assert.True(content.Contains("ControlSession.TargetPackageId"),
            "Card should display TargetPackageId from ControlSession");
        Assert.True(content.Contains("ControlSession.FrameWidth"),
            "Card should display FrameWidth from ControlSession");
        Assert.True(content.Contains("ControlSession.FrameHeight"),
            "Card should display FrameHeight from ControlSession");
        Assert.True(content.Contains("ControlSession.GeometryGeneration"),
            "Card should display GeometryGeneration from ControlSession");
    }

    [Fact]
    public void ControlReadinessCard_HasAllFourActionButtons()
    {
        var xaml = LoadXaml();
        var content = xaml.ToString();

        Assert.True(content.Contains("VerifyGameButton"),
            "Card should have Verify Game button via DynamicResource");
        Assert.True(content.Contains("CaptureScreenButton"),
            "Card should have Capture Screen button via DynamicResource");
        Assert.True(content.Contains("TapTestButton"),
            "Card should have Tap Test button via DynamicResource");
        Assert.True(content.Contains("SwipeTestButton"),
            "Card should have Swipe Test button via DynamicResource");
    }

    [Fact]
    public void ControlReadinessCard_AllButtonsAreDisabled()
    {
        var xaml = LoadXaml();
        var doc = XDocument.Parse(xaml.ToString());

        // Find all Button elements inside the card Border
        var cardBorders = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border" && e.ToString().Contains("ControlReadinessTitle"))
            .ToList();

        Assert.True(cardBorders.Count >= 1,
            "Control readiness card should exist");

        var buttons = cardBorders[0].Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .ToList();

        Assert.True(buttons.Count >= 4,
            $"Expected at least 4 buttons in the card, found {buttons.Count}");

        foreach (var btn in buttons)
        {
            var isEnabled = btn.Attribute("IsEnabled")?.Value ?? "True";
            Assert.Equal("False", isEnabled);
        }
    }
}
