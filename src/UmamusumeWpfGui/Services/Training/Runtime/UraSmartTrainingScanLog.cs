using System.Globalization;
using System.Text;
using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>One Hachimi log card for a completed smart training scan; no additional OCR.</summary>
internal static class UraSmartTrainingScanLog
{
    private static readonly IReadOnlyDictionary<string, string> EnglishFallback = new Dictionary<string, string>
    {
        ["GrassSmartTrainingScanTitle"] = "Smart training scan",
        ["GrassSmartTrainingScanUnknown"] = "Unknown",
        ["GrassSmartTrainingScanHeader"] = "Date: {0} | Energy: {1}",
        ["GrassSmartTrainingScanRow"] = "{0} training: Speed {1}, Stamina {2}, Power {3}, Guts {4}, Wit {5}, Skill points {6}",
        ["GrassSmartTrainingScanOutcome"] = "Failure rate: {0} | Score: {1} | {2}",
        ["GrassSmartTrainingScanNotScored"] = "Not scored",
        ["GrassSmartTrainingScanExcluded"] = "Excluded: {0}",
        ["GrassSmartTrainingScanNoData"] = "No reliable scan data",
        ["GrassSmartTrainingScanWinner"] = "Selected candidate",
        ["GrassSmartTrainingScanSafe"] = "Safe candidate",
        ["GrassSmartTrainingScanChoice"] = "Choice: {0} training (pending confirmation)",
        ["GrassSmartTrainingScanRest"] = "Choice: no reliable safe candidate; return to the Career main screen and rest",
        ["GrassSmartTrainingScanUnknownGains"] = "Gains are unknown, unstable or below the confidence threshold",
        ["GrassSmartTrainingScanUnknownFailure"] = "Failure rate is unknown, unstable or below the confidence threshold",
        ["GrassSmartTrainingScanZeroGains"] = "Expected gains are zero",
        ["GrassSmartTrainingScanHighFailure"] = "Failure rate exceeds {0}%",
        ["GrassNormalTrainingSpeedLabel"] = "Speed",
        ["GrassNormalTrainingStaminaLabel"] = "Stamina",
        ["GrassNormalTrainingPowerLabel"] = "Power",
        ["GrassNormalTrainingGutsLabel"] = "Guts",
        ["GrassNormalTrainingWitLabel"] = "Wit",
    };

    public static void Write(IHachimiTaskLogSink? sink, UraCareerSessionState state,
        UraSmartTrainingDecision decision, Func<string, string?>? lookup = null)
    {
        if (sink is null)
            return;
        var text = ReadCurrentUiText(lookup);
        string Format(string key, params object[] values) =>
            string.Format(CultureInfo.InvariantCulture, text[key], values);
        var message = new StringBuilder();
        message.AppendLine(Format("GrassSmartTrainingScanHeader",
            state.TurnPositionLabel ?? text["GrassSmartTrainingScanUnknown"], Percent(state.Energy.Value, text)));
        foreach (var type in UraTrainingTypeCatalog.SupportedTypes)
        {
            var item = decision.ScoredCandidates.FirstOrDefault(item =>
                string.Equals(item.Candidate.TrainingType, type, StringComparison.OrdinalIgnoreCase));
            var candidate = item.Candidate;
            message.AppendLine();
            message.AppendLine(Format("GrassSmartTrainingScanRow", Name(type, text),
                Gain(candidate?.SpeedGain, text), Gain(candidate?.StaminaGain, text),
                Gain(candidate?.PowerGain, text), Gain(candidate?.GutsGain, text),
                Gain(candidate?.WitGain, text), Gain(candidate?.SkillPointGain, text)));
            var status = candidate is null
                ? Format("GrassSmartTrainingScanExcluded", text["GrassSmartTrainingScanNoData"])
                : item.Score.ExclusionReason is { } reason
                    ? Format("GrassSmartTrainingScanExcluded", Exclusion(reason, candidate, text))
                    : string.Equals(type, decision.Candidate?.TrainingType, StringComparison.OrdinalIgnoreCase)
                        ? text["GrassSmartTrainingScanWinner"] : text["GrassSmartTrainingScanSafe"];
            message.AppendLine(Format("GrassSmartTrainingScanOutcome", Percent(candidate?.FailureRatePercent, text),
                item.Score is { ExclusionReason: null } score
                    ? score.TotalScore.ToString("0.00", CultureInfo.InvariantCulture)
                    : text["GrassSmartTrainingScanNotScored"], status));
        }
        message.AppendLine();
        message.Append(decision.Candidate is { } chosen
            ? Format("GrassSmartTrainingScanChoice", Name(chosen.TrainingType, text))
            : text["GrassSmartTrainingScanRest"]);
        // The existing Hachimi entry template supplies the border. One Add preserves
        // all five rows inside a single card, including unknown and excluded candidates.
        sink.Add(text["GrassSmartTrainingScanTitle"], message.ToString(), HachimiTaskLogEventKind.Detection);
    }

    private static Dictionary<string, string> ReadCurrentUiText(Func<string, string?>? lookup)
    {
        // LocalizationService switches resource dictionaries without changing the OS/thread
        // culture. Read the active application resources, once per card, on the UI thread.
        Dictionary<string, string> Snapshot(Func<string, string?>? resolve) =>
            EnglishFallback.ToDictionary(item => item.Key, item =>
            {
                var localized = resolve?.Invoke(item.Key);
                return string.IsNullOrWhiteSpace(localized) || localized == item.Key ? item.Value : localized;
            });
        if (lookup is not null)
            return Snapshot(lookup);
        var application = Application.Current;
        return application is null ? Snapshot(null)
            : application.Dispatcher.Invoke(() => Snapshot(key => application.TryFindResource(key) as string));
    }

    private static string Gain(int? value, Dictionary<string, string> text) => value is { } number
        ? "+" + number.ToString(CultureInfo.InvariantCulture) : text["GrassSmartTrainingScanUnknown"];

    private static string Percent(int? value, Dictionary<string, string> text) => value is { } number
        ? number.ToString(CultureInfo.InvariantCulture) + "%" : text["GrassSmartTrainingScanUnknown"];

    private static string Name(string type, Dictionary<string, string> text) => type.Trim().ToLowerInvariant() switch
    {
        "speed" => text["GrassNormalTrainingSpeedLabel"],
        "stamina" => text["GrassNormalTrainingStaminaLabel"],
        "power" => text["GrassNormalTrainingPowerLabel"],
        "guts" => text["GrassNormalTrainingGutsLabel"],
        "wit" => text["GrassNormalTrainingWitLabel"],
        _ => type,
    };

    private static string Exclusion(string reason, UraTrainingCandidate candidate,
        Dictionary<string, string> text) => reason switch
    {
        "core gains are unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownGains"],
        "failure rate is unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownFailure"],
        "expected gains are zero" => text["GrassSmartTrainingScanZeroGains"],
        _ when candidate.FailureRatePercent > UraSmartTrainingScorer.MaximumFailureRatePercent =>
            string.Format(CultureInfo.InvariantCulture, text["GrassSmartTrainingScanHighFailure"],
                UraSmartTrainingScorer.MaximumFailureRatePercent),
        _ => reason,
    };
}
