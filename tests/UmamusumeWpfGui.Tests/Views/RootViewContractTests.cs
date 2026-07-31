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
        Assert.Contains("BreadcrumbBar", content);
        Assert.DoesNotContain("AutoSuggestBox", content);
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
    public void RootView_CachesEachTabViewThroughStylet()
    {
        var contentControls = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "ContentControl")
            .ToList();
        var content = string.Join(Environment.NewLine, contentControls);

        Assert.Equal(4, contentControls.Count);
        Assert.Contains("GrassViewModel", content);
        Assert.Contains("OverviewViewModel", content);
        Assert.Contains("LogViewModel", content);
        Assert.Contains("SettingsViewModel", content);
        Assert.All(contentControls, control =>
            Assert.Contains("View.Model", control.ToString()));
    }

    [Fact]
    public void RootView_PlacesHachimiBeforeOverview()
    {
        var menuItems = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Take(3)
            .ToList();

        Assert.Equal("3", menuItems[0].Attribute("Tag")?.Value);
        Assert.Contains("TabHachimi", menuItems[0].Attribute("Content")?.Value);
        Assert.Equal("0", menuItems[1].Attribute("Tag")?.Value);
        Assert.Contains("NavOverview", menuItems[1].Attribute("Content")?.Value);
    }

    [Fact]
    public void RootNavigation_OnlySelectsActiveContent()
    {
        var root = LoadXaml().Root!;
        var navigationItems = root.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToList();

        Assert.NotEmpty(navigationItems);
        Assert.All(navigationItems, item =>
            Assert.Null(item.Attribute("TargetPageType")));
        Assert.All(navigationItems, item =>
            Assert.Equal("OnNavigationItemClick", item.Attribute("Click")?.Value));
        Assert.All(navigationItems, item =>
            Assert.Equal(
                "OnNavigationItemPreviewMouseDown",
                item.Attribute("PreviewMouseLeftButtonDown")?.Value));
        Assert.DoesNotContain("NavigationCacheMode", root.ToString());
    }

    [Fact]
    public void RootViewModel_ImplementsPropertyChangedNotification()
    {
        Assert.Contains(typeof(INotifyPropertyChanged),
            typeof(RootViewModel).GetInterfaces());
    }
}
