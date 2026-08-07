using System.Windows.Media.Imaging;

namespace UmamusumeWpfGui.Models;

public sealed class DeveloperToolsImageItem
{
    public DeveloperToolsImageItem(
        int traineeId,
        string name,
        string path,
        BitmapSource thumbnail)
    {
        TraineeId = traineeId;
        Name = name;
        Path = path;
        Thumbnail = thumbnail;
    }

    public int TraineeId { get; }

    public string Name { get; }

    public string Path { get; }

    public BitmapSource Thumbnail { get; }

    public string DisplayName => $"{Name} ({TraineeId})";
}
