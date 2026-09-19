using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Converts the editable UI model into immutable pipeline contracts.
/// </summary>
public static class CareerTaskSettingsMapper
{
    public static CareerTrainingSettings ToNormalSettings(CareerTrainingTaskSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CareerTrainingSettings(
            settings.ManifestPath,
            settings.TraineeId ?? throw new InvalidOperationException("No trainee is configured."),
            !settings.DeleteExistingCareerData,
            settings.ParseSupportCardIds(),
            settings.SupportDeckMode,
            settings.SupportDeckPreset,
            settings.FriendSupportCardId,
            settings.StrategyId,
            settings.PauseOnUnknownOutcome,
            settings.AllowOptionalRaces,
            settings.LegacySelectionMode,
            settings.UseLegacyGuest,
            settings.UseCachedLegacy,
            settings.ParseLegacyAttributeSparks(),
            settings.ParseLegacyAptitudeSparks(),
            settings.NormalLineupStrategy);
    }

    public static IndependentTrainingSettings ToIndependentSettings(CareerTrainingTaskSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new IndependentTrainingSettings(
            settings.ManifestPath,
            settings.TraineeId ?? throw new InvalidOperationException("No trainee is configured."),
            !settings.DeleteExistingCareerData,
            settings.ParseSupportCardIds(),
            settings.SupportDeckMode,
            settings.SupportDeckPreset,
            settings.FriendSupportCardId,
            settings.LegacySelectionMode,
            settings.UseLegacyGuest,
            settings.UseCachedLegacy,
            settings.ParseLegacyAttributeSparks(),
            settings.ParseLegacyAptitudeSparks(),
            settings.IndependentTrainingFocus,
            settings.IndependentLineupStrategy,
            settings.ParseIndependentAgendaSelections(),
            settings.ParseIndependentSkillIds());
    }
}
