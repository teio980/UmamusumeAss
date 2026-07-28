using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class OverviewViewContractTests
{
    private static string OverviewViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "UmamusumeWpfGui", "Views", "OverviewView.xaml"));

    [Fact]
    public void OverviewView_UsesTokenDrivenStatusAndDevicePanels()
    {
        Assert.True(File.Exists(OverviewViewPath));
        var content = File.ReadAllText(OverviewViewPath);
        Assert.Contains("SurfaceCanvasBrush", content);
        Assert.Contains("SurfacePanelBrush", content);
        Assert.Contains("OverviewConnectionStatus", content);
        Assert.Contains("LastVerifiedConnection", content);
        Assert.Contains("CoreVersion", content);
    }

    [Fact]
    public void OverviewView_DoesNotExposeConnectionCommands()
    {
        var xaml = XDocument.Load(OverviewViewPath).ToString();
        Assert.DoesNotContain("ConnectCommand", xaml);
        Assert.DoesNotContain("CancelCommand", xaml);
    }
}
