using System.Windows.Media.Imaging;

namespace UmamusumeWpfGui.Models;

public enum DeveloperToolsImageKind
{
    RaceOutfit,
    SchoolUniform,
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

    public bool IsSchoolUniform => Kind == DeveloperToolsImageKind.SchoolUniform;

    public string VariantDisplayName => IsSchoolUniform ? "School uniform" : "Race outfit";

    public string Key => IsSchoolUniform
        ? $"uniform:{BaseCharacterId}"
        : $"race:{TraineeId}";

    public string DisplayName => IsSchoolUniform
        ? $"{Name} ({BaseCharacterId}) · {VariantDisplayName}"
        : $"{Name} ({TraineeId}) · {VariantDisplayName}";
}
