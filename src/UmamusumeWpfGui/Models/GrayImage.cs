namespace UmamusumeWpfGui.Models;





public sealed record GrayImage(
    int Width,
    int Height,
    byte[] Pixels,
    byte[]? RgbaPixels = null)
{
    public FrameProvenance? SourceFrame { get; init; }
    public Umamusume.CoreBridge.TemplatePreparation Preparation { get; init; }
}
