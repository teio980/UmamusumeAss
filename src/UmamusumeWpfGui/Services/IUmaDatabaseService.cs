using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public interface IUmaDatabaseService
{
    bool IsLoaded { get; }

    string? Region { get; }

    IReadOnlyCollection<UmaBaseCharacterRecord> BaseCharacters { get; }

    IReadOnlyCollection<UmaTraineeRecord> Trainees { get; }

    IReadOnlyCollection<UmaSupportCardRecord> SupportCards { get; }

    Task LoadAsync(string resourceRoot, CancellationToken cancellationToken = default);

    bool TryGetTrainee(int traineeId, out UmaTraineeRecord? trainee);

    bool TryGetSupportCard(int supportCardId, out UmaSupportCardRecord? supportCard);

    IReadOnlyList<UmaTraineeRecord> FindTraineesByName(string name);

    IReadOnlyList<UmaSupportCardRecord> FindSupportCardsByName(string name);

    IReadOnlyList<UmaSupportCardRecord> GetSupportCardsForCharacter(int baseCharacterId);

    string GetTraineeTemplateDirectory(int traineeId);

    string GetSupportCardTemplateDirectory(int supportCardId);
}
