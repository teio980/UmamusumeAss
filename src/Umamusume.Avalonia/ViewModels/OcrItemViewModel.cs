using CommunityToolkit.Mvvm.ComponentModel;

namespace Umamusume.Avalonia.ViewModels;

public sealed class OcrItemViewModel : ObservableObject
{
    public string Text { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public double Confidence { get; }

    public string BoundsText => $"[{X}, {Y}, {Width}x{Height}]";
    public string ConfidenceText => $"{Confidence:P1}";

    public OcrItemViewModel(string text, int x, int y, int width, int height, double confidence)
    {
        Text = text;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Confidence = confidence;
    }
}
