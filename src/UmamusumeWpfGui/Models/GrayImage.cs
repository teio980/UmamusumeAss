namespace UmamusumeWpfGui.Models;





public sealed record GrayImage(
    int Width,
    int Height,
    byte[] Pixels,
    byte[]? RgbaPixels = null);
