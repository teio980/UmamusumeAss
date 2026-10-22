using Umamusume.CoreBridge.Logging;
using UmamusumeWpfGui.Services;
using SharedSemantics = Umamusume.CoreBridge.Logging.HachimiTaskLogSemantics;
using WpfProfile = UmamusumeWpfGui.Models.HachimiTaskLogProfile;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>Windows localization adapter for the shared execution-log rules.</summary>
internal static class HachimiTaskLogSemantics
{
    public static HachimiTaskLogSemanticStep Localize(
        HachimiTaskLogSemanticStep step, ILocalizationService? localizationService) =>
        SharedSemantics.Localize(step,
            localizationService is null ? null : localizationService.GetString,
            localizationService?.CurrentCulture ?? "en-US");

    public static string ToUserFacingFailure(string message) => SharedSemantics.ToUserFacingFailure(message);

    public static bool TryDescribe(WpfProfile profile, string taskName, out HachimiTaskLogSemanticStep step) =>
        SharedSemantics.TryDescribe(profile switch
        {
            WpfProfile.StartGame => HachimiTaskLogProfile.StartGame,
            WpfProfile.TeamRace => HachimiTaskLogProfile.TeamRace,
            WpfProfile.DailyRace => HachimiTaskLogProfile.DailyRace,
            WpfProfile.MailCollection => HachimiTaskLogProfile.MailCollection,
            WpfProfile.MissionCollection => HachimiTaskLogProfile.MissionCollection,
            WpfProfile.Shop => HachimiTaskLogProfile.Shop,
            WpfProfile.Career => HachimiTaskLogProfile.Career,
            _ => HachimiTaskLogProfile.None,
        }, taskName, out step);

    public static bool TryDescribeIndependentAction(string action, out HachimiTaskLogSemanticStep step) =>
        SharedSemantics.TryDescribeIndependentAction(action, out step);

    public static bool TryDescribeCareerAction(string action, out HachimiTaskLogSemanticStep step) =>
        SharedSemantics.TryDescribeCareerAction(action, out step);
}
