using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class SettingsViewContractTests
{
    private static readonly string ProjectDir = ResolveProjectDir();
    private static readonly string SettingsViewPath = Path.Combine(
        ProjectDir, "src", "UmamusumeWpfGui", "Views", "SettingsView.xaml");

    private static string ResolveProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Cannot locate solution root");
    }

    private static XDocument LoadXaml()
    {
        Assert.True(File.Exists(SettingsViewPath));
        return XDocument.Load(SettingsViewPath);
    }

    [Fact]
    public void SettingsView_UsesMarch7thCardAndPivotStructure()
    {
        var document = LoadXaml();
        var content = document.ToString();

        Assert.Contains("ScrollViewer", content);
        Assert.Contains("Margin=\"36,24,42,42\"", content);
        Assert.Contains("SettingCardGroupStyle", content);
        Assert.Contains("SettingCardStyle", content);
        Assert.Contains("ExpandableSettingCardStyle", content);
        Assert.Contains("SettingsTabControlStyle", content);
        Assert.Contains("SettingsTabItemStyle", content);
        Assert.Contains("SurfacePanelBrush", content);
        Assert.Contains("BorderSubtleBrush", content);
        Assert.Contains("AccentPrimaryBrush", content);
        Assert.Contains("ConnectionSettingCardGroup", content);
        Assert.Contains("LanguageSettingCardGroup", content);
        Assert.Contains("SystemSettingCardGroup", content);
        Assert.Contains("ExpandableSettingCard", content);
    }

    [Fact]
    public void SettingsView_UsesOfficialWpfUiControlsAndPivotIndicators()
    {
        var content = File.ReadAllText(SettingsViewPath);
        Assert.Contains("http://schemas.lepo.co/wpfui/2022/xaml", content);
        Assert.Contains("ui:Button", content);
        Assert.Contains("ui:TextBox", content);
        Assert.Contains("ui:SymbolIcon", content);
        Assert.Equal(5, content.Split("ui:ToggleSwitch", StringSplitOptions.None).Length - 1);
        Assert.Contains("ui:CardExpander", content);
        Assert.Contains("Appearance=\"Primary\"", content);
        Assert.Contains("Appearance=\"Secondary\"", content);
        Assert.Contains("ArrowSync24", content);
        Assert.Contains("TabIndicator", content);
        Assert.Contains("TabIndicatorCanvas", content);
        Assert.Contains("SelectedTabIndicator", content);
        Assert.DoesNotContain("SelectionChanged=\"OnSettingsTabSelectionChanged\"", content);
        Assert.DoesNotContain("OnSettingsTabSelectionChanged", File.ReadAllText(
            Path.Combine(ProjectDir, "src", "UmamusumeWpfGui", "Views", "SettingsView.xaml.cs")));
    }

    [Fact]
    public void SettingsView_UsesOneSharedIndicatorAndKeepsSelectedIndexBinding()
    {
        var content = File.ReadAllText(SettingsViewPath);
        var codeBehind = File.ReadAllText(Path.Combine(
            ProjectDir, "src", "UmamusumeWpfGui", "Views", "SettingsView.xaml.cs"));

        Assert.Contains("SelectedIndex=\"{Binding SelectedMenuIndex, Mode=TwoWay}\"", content);
        Assert.Contains("TabIndicatorCanvas", content);
        Assert.Contains("SelectedTabIndicator", content);
        Assert.Contains("NavigationIndicatorAnimator.SetBounds", codeBehind);
        Assert.Contains("HandleTabSelectionChanged", codeBehind);
        Assert.Contains("indicatorLength = 16", codeBehind);
        Assert.Contains("headerRightPadding = 18", codeBehind);
        Assert.Contains("HachimiSettingCardGroup", content);
        Assert.Contains("HachimiShopSettings.Enabled", content);
        Assert.Contains("HachimiShopSettings.SelectAll", content);
    }

    [Fact]
    public void SettingsView_UsesValidWpfUiSymbols()
    {
        var symbols = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "SymbolIcon")
            .Select(element => element.Attribute("Symbol")?.Value)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol));

        foreach (var symbol in symbols)
            Assert.True(Enum.TryParse<SymbolRegular>(symbol, out _), $"Unknown WPF-UI symbol: {symbol}");
    }

    [Fact]
    public void SettingsView_ExpandableCardPlacesToggleBeforeFarRightChevron()
    {
        var document = LoadXaml();
        var card = document.Descendants()
            .Single(element => element.Name.LocalName == "CardExpander");
        var cardIcon = card.Elements()
            .Single(element => element.Name.LocalName == "CardExpander.Icon")
            .Descendants()
            .Single(element => element.Name.LocalName == "SymbolIcon");
        var header = card.Elements()
            .Single(element => element.Name.LocalName == "CardExpander.Header");
        var toggle = header.Descendants()
            .Single(element => element.Name.LocalName == "ToggleSwitch");

        Assert.Contains("DraftAutoStartEmulator", toggle.Attribute("IsChecked")?.Value);
        Assert.DoesNotContain("IsExpanded", toggle.Attribute("IsChecked")?.Value);
        Assert.Equal("Games24", cardIcon.Attribute("Symbol")?.Value);
        Assert.Equal("{StaticResource SettingIconStyle}", cardIcon.Attribute("Style")?.Value);

        var style = document.Descendants()
            .Single(element => element.Name.LocalName == "Style"
                && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                    == "ExpandableSettingCardStyle");
        Assert.Equal("{x:Type ui:CardExpander}", style.Attribute("TargetType")?.Value);

        var template = style.Descendants()
            .Single(element => element.Name.LocalName == "ControlTemplate");
        var templateGrid = template.Descendants()
            .Single(element => element.Name.LocalName == "Grid"
                && element.Elements().Any(child => child.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                    == "HeaderPresenter"));
        var headerChildren = templateGrid.Elements()
            .Where(element => element.Attribute("Grid.Row")?.Value == "0")
            .Select(element => element.Name.LocalName)
            .ToArray();

        Assert.Equal(
            "ContentPresenter|ContentPresenter|ToggleButton",
            string.Join("|", headerChildren));

        var headerPresenter = template.Descendants()
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                == "HeaderPresenter");
        var chevronButton = template.Descendants()
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                == "ExpanderChevronButton");
        var contentPresenter = template.Descendants()
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                == "ContentPresenter");

        Assert.Equal("1", headerPresenter.Attribute("Grid.Column")?.Value);
        Assert.Equal("2", chevronButton.Attribute("Grid.Column")?.Value);
        Assert.Equal("0", chevronButton.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", contentPresenter.Attribute("Grid.Row")?.Value);
        var expandedContent = card.Elements()
            .Single(element => element.Name.LocalName == "StackPanel"
                && element.ToString().Contains("EmulatorExecutablePathLabel", StringComparison.Ordinal));
        Assert.Equal("22,8,0,0", expandedContent.Attribute("Margin")?.Value);
        Assert.Contains("IsExpanded", chevronButton.Attribute("IsChecked")?.Value);
        Assert.Contains("TemplatedParent", chevronButton.Attribute("IsChecked")?.Value);
        Assert.Equal("{DynamicResource ExpanderChevronButtonStyle}", chevronButton.Attribute("Style")?.Value);

        var chevronStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style"
                && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                    == "ExpanderChevronButtonStyle");
        var chevronSymbols = chevronStyle.Descendants()
            .Where(element => element.Name.LocalName == "SymbolIcon")
            .Select(element => element.Attribute("Symbol")?.Value)
            .ToArray();
        Assert.Contains("ChevronDown24", chevronSymbols);
        Assert.Contains("ChevronUp24", chevronSymbols);
    }

    [Fact]
    public void SettingsView_PreservesConnectionCommandsAndFields()
    {
        var content = File.ReadAllText(SettingsViewPath);
        var expected = new[]
        {
            "DraftAdbPath", "DraftConnectAddress", "DraftConnectConfig",
            "ConnectConfigOptions", "ConnectAddressHistory",
            "ConnectCommand", "CancelConnectCommand", "DisconnectCommand",
            "SaveCommand", "DetectAdbConfigCommand", "DraftAlwaysAutoDetect",
            "DraftAutoStartEmulator", "DraftAutoStartEmulatorWaitSeconds",
            "DraftEmulatorExecutablePath", "LastVerified", "ForgetCommand",
            "StatusText",
        };

        foreach (var binding in expected)
            Assert.Contains(binding, content);
    }

    [Fact]
    public void SettingsView_PreservesConnectionLanguageAndSystemSections()
    {
        var content = File.ReadAllText(SettingsViewPath);
        foreach (var key in new[]
        {
            "SettingsTitle", "ConnectionTitle", "LanguageTitle", "SystemTitle",
            "SelectedLanguage", "LanguageHint", "CoreVersion",
            "ResourcePath", "LastDetectedEmulator", "UpdatesTitle",
            "UpdateStatus", "StartupUpdateCheck", "CheckForUpdatesCommand",
            "DownloadUpdateCommand", "InstallUpdateCommand",
            "CancelUpdateCommand", "ClearUpdateCacheCommand", "SkipUpdateCommand",
        })
        {
            Assert.Contains(key, content);
        }
    }

    [Fact]
    public void SettingsView_DeviceInformationIsReadOnlyAndOptional()
    {
        var content = File.ReadAllText(SettingsViewPath);
        Assert.Contains("NotNullToVisibility", content);
        Assert.Contains("LastVerified.Serial", content);
        Assert.Contains("LastVerified.AndroidId", content);
        Assert.Contains("LastVerified.AndroidVersion", content);
        var editable = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName is "TextBox" or "ComboBox"
                && element.ToString().Contains("LastVerified", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(editable);
    }

    [Fact]
    public void SettingsView_ControlReadinessRemainsDisabled()
    {
        var document = LoadXaml();
        var readiness = document.Descendants()
            .First(element => element.Name.LocalName == "StackPanel"
                && element.Attribute("IsEnabled")?.Value == "False"
                && element.ToString().Contains("ControlReadinessTitle", StringComparison.Ordinal));

        Assert.Equal("False", readiness.Attribute("IsEnabled")?.Value);
        Assert.Equal("0.6", readiness.Attribute("Opacity")?.Value);

        var buttons = readiness.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToList();
        Assert.Equal(4, buttons.Count);
    }

    [Fact]
    public void SettingsView_AllStaticTextBlocksUseResourcesOrBindings()
    {
        var textBlocks = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        var hardcodedLabels = textBlocks
            .Where(text => !text!.StartsWith("{DynamicResource", StringComparison.Ordinal)
                && !text.StartsWith("{Binding", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hardcodedLabels);
    }
}
