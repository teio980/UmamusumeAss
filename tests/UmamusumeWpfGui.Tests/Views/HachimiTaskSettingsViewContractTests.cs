using System;
using System.IO;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class HachimiTaskSettingsViewContractTests
{
    private static string ViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "HachimiTaskSettingsView.xaml"));

    [Fact]
    public void TaskSettingsView_IsMarch7thStyleFullPageAndDoesNotHostShopEditor()
    {
        var content = File.ReadAllText(ViewPath);
        var document = XDocument.Parse(content);

        Assert.Contains("ScrollViewer", content);
        Assert.Contains("SettingCardStyle", content);
        Assert.Contains("GrassTaskSettingsBack", content);
        Assert.Contains("SelectedTask.Settings", content);
        Assert.Contains("IsSelectedShopTask", content);
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TabControl");
        Assert.DoesNotContain("GrassTaskSettingsGeneral", content);
        Assert.DoesNotContain("GrassTaskSettingsDetails", content);
        Assert.DoesNotContain("TaskSettingsTabs", content);
        Assert.DoesNotContain("HachimiShopSettingsView", content);
    }

    [Fact]
    public void TaskSettingsView_UsesOnlyExistingDynamicThemeResources()
    {
        var content = File.ReadAllText(ViewPath);
        var projectDirectory = Directory.GetParent(Path.GetDirectoryName(ViewPath)!)!.FullName;
        var sharedStyles = File.ReadAllText(Path.Combine(
            projectDirectory, "Res", "SettingStyles.xaml"));

        Assert.Contains("SurfaceCanvasBrush", File.ReadAllText(Path.Combine(
            projectDirectory, "Views", "GrassView.xaml")));
        Assert.Contains("SurfacePanelBrush", sharedStyles);
        Assert.Contains("SurfaceRaisedBrush", sharedStyles);
        Assert.Contains("BorderSubtleBrush", sharedStyles);
        Assert.Contains("AccentPrimaryBrush", sharedStyles);
        Assert.Contains("CareerTrainingTaskSettingsView", content);
        Assert.DoesNotContain("#", content);
        Assert.DoesNotContain("#", sharedStyles);

        Assert.Contains("SettingCardStyle", sharedStyles);
        Assert.Contains("SettingsTabControlStyle", sharedStyles);
        Assert.Contains("SettingStyles.xaml", File.ReadAllText(Path.Combine(
            projectDirectory, "App.xaml")));
    }
}
