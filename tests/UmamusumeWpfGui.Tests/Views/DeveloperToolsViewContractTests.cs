using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class DeveloperToolsViewContractTests
{
    private static string DeveloperToolsViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "DeveloperToolsView.xaml"));

    [Fact]
    public void CaptureScreenPreview_keeps_crop_interaction_wired()
    {
        var document = XDocument.Load(DeveloperToolsViewPath);
        var captureSurface = document.Descendants()
            .Single(element => HasName(element, "CapturePreviewSurface"));
        var captureOverlay = document.Descendants()
            .Single(element => HasName(element, "CaptureCropOverlay"));
        var content = document.ToString();

        Assert.Equal(
            "OnCapturePreviewSurfaceSizeChanged",
            captureSurface.Attribute("SizeChanged")?.Value);
        Assert.Equal(
            "OnCaptureCropMouseLeftButtonDown",
            captureOverlay.Attribute("MouseLeftButtonDown")?.Value);
        Assert.Equal(
            "OnCaptureCropMouseMove",
            captureOverlay.Attribute("MouseMove")?.Value);
        Assert.Equal(
            "OnCaptureCropMouseLeftButtonUp",
            captureOverlay.Attribute("MouseLeftButtonUp")?.Value);
        Assert.Contains("SaveCroppedCommand", content);
    }

    private static bool HasName(XElement element, string name) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name);
}
