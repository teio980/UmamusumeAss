using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

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
    public void SettingsView_UsesOfficialSimplePageLayout()
    {
        var document = LoadXaml();
        var content = document.ToString();
        var stackPanel = document.Root!.Descendants()
            .First(element => element.Name.LocalName == "StackPanel");

        Assert.Equal("42", stackPanel.Attribute("Margin")?.Value);
        Assert.Contains("ScrollViewer", content);
        Assert.Contains("http://schemas.lepo.co/wpfui/2022/xaml", content);
        Assert.DoesNotContain("SurfaceCanvasBrush", content);
        Assert.DoesNotContain("AccentPrimaryBrush", content);
        Assert.DoesNotContain("ControlTemplate", content);
        Assert.DoesNotContain("ui:Card", content);
    }

    [Fact]
    public void SettingsView_UsesOfficialWpfUiControls()
    {
        var content = File.ReadAllText(SettingsViewPath);
        Assert.Contains("ui:Button", content);
        Assert.Contains("ui:TextBox", content);
        Assert.Contains("Appearance=\"Primary\"", content);
        Assert.Contains("Appearance=\"Secondary\"", content);
        Assert.Contains("ArrowSync24", content);
    }

    [Fact]
    public void SettingsView_PreservesConnectionCommandsAndFields()
    {
        var content = File.ReadAllText(SettingsViewPath);
        var expected = new[]
        {
            "DraftAdbPath", "DraftConnectAddress", "ConnectAddressHistory",
            "ConnectCommand", "CancelConnectCommand", "DisconnectCommand",
            "SaveCommand", "DetectAdbConfigCommand", "DraftAlwaysAutoDetect",
            "DraftAutoStartEmulator", "DraftAutoStartEmulatorWaitSeconds",
            "LastVerified", "ForgetCommand", "StatusText",
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
            "ConnectionTitle", "LanguageTitle", "SystemTitle",
            "SelectedLanguage", "LanguageHint", "CoreVersion",
            "ResourcePath", "LastDetectedEmulator",
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
            .Where(element => element.Name.LocalName == "TextBox"
                && element.ToString().Contains("LastVerified"))
            .ToList();
        Assert.Empty(editable);
    }

    [Fact]
    public void SettingsView_ControlReadinessRemainsDisabled()
    {
        var document = LoadXaml();
        var readiness = document.Descendants()
            .First(element => element.Name.LocalName == "StackPanel"
                && element.Attribute("IsEnabled") is not null
                && element.ToString().Contains("ControlReadinessTitle"));

        Assert.Equal("False", readiness.Attribute("IsEnabled")?.Value);
        Assert.Equal("0.6", readiness.Attribute("Opacity")?.Value);

        var buttons = readiness.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToList();
        Assert.Equal(4, buttons.Count);
    }

    [Fact]
    public void SettingsView_AllStaticLabelsUseResourcesOrOfficialSampleText()
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
