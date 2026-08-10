using System.Windows.Media.Imaging;

namespace UmamusumeWpfGui.Models;

public enum DeveloperToolsImageKind
{
    RaceOutfit,
    LiveOutfit,
}

public sealed class DeveloperToolsImageItem
{
    public DeveloperToolsImageItem(
        int? traineeId,
        int baseCharacterId,
        string name,
        string path,
        BitmapSource thumbnail,
        DeveloperToolsImageKind kind)
    {
        TraineeId = traineeId;
        BaseCharacterId = baseCharacterId;
        Name = name;
        Path = path;
        Thumbnail = thumbnail;
        Kind = kind;
    }

    public int? TraineeId { get; }

    public int BaseCharacterId { get; }

    public string Name { get; }

    public string Path { get; }

    public BitmapSource Thumbnail { get; }

    public DeveloperToolsImageKind Kind { get; }

    public bool IsLiveOutfit => Kind == DeveloperToolsImageKind.LiveOutfit;

    public string VariantDisplayName => IsLiveOutfit ? "Live outfit" : "Race outfit";

    public string Key => IsLiveOutfit
        ? $"live:{BaseCharacterId}"
        : $"race:{TraineeId}";

    public string DisplayName => IsLiveOutfit
        ? $"{Name} ({BaseCharacterId}) · {VariantDisplayName}"
        : $"{Name} ({TraineeId}) · {VariantDisplayName}";
}
