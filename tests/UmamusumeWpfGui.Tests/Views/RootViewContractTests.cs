using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class RootViewContractTests
{
    private static string PathToRootView => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "UmamusumeWpfGui", "Views", "RootView.xaml"));

    private static XDocument LoadXaml()
    {
        Assert.True(File.Exists(PathToRootView));
        return XDocument.Load(PathToRootView);
    }

    [Fact]
    public void RootView_UsesNavigationRailInsteadOfTabs()
    {
        var xaml = LoadXaml();
        Assert.DoesNotContain(xaml.Descendants(), element => element.Name.LocalName is "TabControl" or "TabItem");
        Assert.Contains(xaml.Descendants(), element => element.Name.LocalName == "ListBox" && element.Attribute("ItemsSource")?.Value.Contains("NavigationItems") == true);
    }

    [Fact]
    public void RootView_HostsActiveContentThroughStylet()
    {
        var contentControl = LoadXaml().Descendants().Single(element => element.Name.LocalName == "ContentControl");
        Assert.Contains("ActiveContent", contentControl.ToString());
        Assert.Contains("View.Model", contentControl.ToString());
    }

    [Fact]
    public void RootView_UsesDesignSystemSurfaces()
    {
        var content = File.ReadAllText(PathToRootView);
        Assert.Contains("SurfaceCanvasBrush", content);
        Assert.Contains("SurfaceSidebarBrush", content);
        Assert.Contains("BorderDefaultBrush", content);
    }

    [Fact]
    public void RootView_HasExpectedWindowDimensions()
    {
        var root = LoadXaml().Root!;
        Assert.Equal("960", root.Attribute("Width")?.Value);
        Assert.Equal("680", root.Attribute("Height")?.Value);
    }
}
