namespace UmamusumeWpfGui.Models;

/// <summary>
/// Grayscale image used by the lightweight template matcher. Pixels are row
/// major, one byte per pixel, and never expose WPF bitmap objects to task code.
/// </summary>
public sealed record GrayImage(int Width, int Height, byte[] Pixels);
