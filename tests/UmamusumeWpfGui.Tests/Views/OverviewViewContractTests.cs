using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class OverviewViewContractTests
{
    private static string OverviewViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "OverviewView.xaml"));

    [Fact]
    public void OverviewView_UsesOfficialPageGeometryAndResources()
    {
        var document = XDocument.Load(OverviewViewPath);
        var content = document.ToString();
        var stackPanel = document.Root!.Descendants()
            .First(element => element.Name.LocalName == "StackPanel");

        Assert.Equal("42", stackPanel.Attribute("Margin")?.Value);
        Assert.Contains("ApplicationBackgroundBrush", content);
        Assert.Contains("TextFillColorPrimaryBrush", content);
        Assert.DoesNotContain("SurfaceCanvasBrush", content);
        Assert.DoesNotContain("ui:Card", content);
    }

    [Fact]
    public void OverviewView_PreservesConnectionAndCoreBindingsWithoutCommands()
    {
        var content = File.ReadAllText(OverviewViewPath);
        Assert.Contains("OverviewConnectionStatus", content);
        Assert.Contains("LastVerifiedConnection", content);
        Assert.Contains("CoreVersion", content);
        Assert.DoesNotContain("ConnectCommand", content);
        Assert.DoesNotContain("CancelCommand", content);
    }
}
