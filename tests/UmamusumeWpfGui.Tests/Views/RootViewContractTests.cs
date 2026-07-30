using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class RootViewContractTests
{
    private static string PathToRootView => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "RootView.xaml"));

    private static XDocument LoadXaml()
    {
        Assert.True(File.Exists(PathToRootView));
        return XDocument.Load(PathToRootView);
    }

    [Fact]
    public void RootView_UsesOfficialWpfUiWindowAndNavigation()
    {
        var root = LoadXaml().Root!;
        var content = root.ToString();

        Assert.Equal("FluentWindow", root.Name.LocalName);
        Assert.Contains("http://schemas.lepo.co/wpfui/2022/xaml", content);
        Assert.Contains("NavigationView", content);
        Assert.Contains("TitleBar", content);
        Assert.Contains("AutoSuggestBox", content);
        Assert.Contains("BreadcrumbBar", content);
    }

    [Fact]
    public void RootView_MatchesOfficialSimpleDemoWindowSize()
    {
        var root = LoadXaml().Root!;
        Assert.Equal("1100", root.Attribute("Width")?.Value);
        Assert.Equal("650", root.Attribute("Height")?.Value);
    }

    [Fact]
    public void RootView_UsesOfficialNavigationHeaderGeometry()
    {
        var breadcrumb = LoadXaml().Descendants()
            .Single(element => element.Name.LocalName == "BreadcrumbBar");

        Assert.Equal("42,32,0,0", breadcrumb.Attribute("Margin")?.Value);
        Assert.Equal("28", breadcrumb.Attribute("FontSize")?.Value);
        Assert.Equal("DemiBold", breadcrumb.Attribute("FontWeight")?.Value);
    }

    [Fact]
    public void RootView_HostsActiveContentThroughStylet()
    {
        var contentControl = LoadXaml().Descendants()
            .Single(element => element.Name.LocalName == "ContentControl");
        Assert.Contains("ActiveContent", contentControl.ToString());
        Assert.Contains("View.Model", contentControl.ToString());
    }

    [Fact]
    public void RootViewModel_ImplementsPropertyChangedNotification()
    {
        Assert.Contains(typeof(INotifyPropertyChanged),
            typeof(RootViewModel).GetInterfaces());
    }
}
