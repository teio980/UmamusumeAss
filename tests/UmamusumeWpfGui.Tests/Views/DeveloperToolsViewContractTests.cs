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
        var captureCoordinateBadge = document.Descendants()
            .Single(element => HasName(element, "CaptureCoordinateBadge"));
        var captureRoiBadge = document.Descendants()
            .Single(element => HasName(element, "CaptureRoiBadge"));
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
        Assert.Equal(
            "OnCapturePanMouseMiddleButtonDown",
            captureOverlay.Attribute("MouseDown")?.Value);
        Assert.Equal(
            "OnCapturePanMouseMiddleButtonUp",
            captureOverlay.Attribute("MouseUp")?.Value);
        Assert.Equal(
            "OnCapturePreviewMouseWheel",
            captureOverlay.Attribute("MouseWheel")?.Value);
        Assert.Equal(
            "OnCaptureCropMouseLeave",
            captureOverlay.Attribute("MouseLeave")?.Value);
        Assert.Equal("CaptureCoordinateText", GetDescendantName(captureCoordinateBadge));
        Assert.Equal("CaptureRoiText", GetDescendantName(captureRoiBadge));
        Assert.Contains("SaveCroppedCommand", content);
    }

    private static bool HasName(XElement element, string name) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name);

    private static string? GetDescendantName(XElement element) =>
        element.Descendants()
            .SelectMany(descendant => descendant.Attributes())
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Name")
            ?.Value;
}
