using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
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

    private static readonly string[] SupportedTypes = ["speed", "stamina", "power", "guts", "wit"];

    public static void WriteJson(
        IHachimiTaskLogSink? sink,
        string jsonOrText,
        ILocalizationService? localizationService = null,
        Func<string, string?>? lookup = null)
    {
        if (sink is null)
            return;

        var text = ReadCurrentUiText(localizationService, lookup);
        string Format(string key, params object[] values) =>
            string.Format(CultureInfo.InvariantCulture, text[key], values);

        if (string.IsNullOrWhiteSpace(jsonOrText))
            return;

        if (!jsonOrText.TrimStart().StartsWith('{'))
        {
            sink.Add(text["GrassSmartTrainingScanTitle"], jsonOrText, HachimiTaskLogEventKind.Detection);
            return;
        }

        try
        {
            var root = JsonNode.Parse(jsonOrText);
            if (root is null)
            {
                sink.Add(text["GrassSmartTrainingScanTitle"], jsonOrText, HachimiTaskLogEventKind.Detection);
                return;
            }

            var date = root["date"]?.GetValue<string>() ?? text["GrassSmartTrainingScanUnknown"];
            var energyNode = root["energy"];
            var energy = energyNode is not null ? Percent(energyNode.GetValue<int>(), text) : text["GrassSmartTrainingScanUnknown"];
            var chosen = root["chosen"]?.GetValue<string>();

            var message = new StringBuilder();
            message.AppendLine(Format("GrassSmartTrainingScanHeader", date, energy));

            var candidatesArray = root["candidates"]?.AsArray();
            var candidatesMap = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            if (candidatesArray is not null)
            {
                foreach (var item in candidatesArray)
                {
                    if (item?["type"]?.GetValue<string>() is { } t)
                        candidatesMap[t] = item;
                }
            }

            foreach (var type in SupportedTypes)
            {
                candidatesMap.TryGetValue(type, out var candNode);
                message.AppendLine();
                message.AppendLine(Format("GrassSmartTrainingScanRow", Name(type, text),
                    Gain(candNode?["speed"]?.GetValue<int?>(), text),
                    Gain(candNode?["stamina"]?.GetValue<int?>(), text),
                    Gain(candNode?["power"]?.GetValue<int?>(), text),
                    Gain(candNode?["guts"]?.GetValue<int?>(), text),
                    Gain(candNode?["wit"]?.GetValue<int?>(), text),
                    Gain(candNode?["skill_points"]?.GetValue<int?>(), text)));

                var exclusion = candNode?["exclusion_reason"]?.GetValue<string?>();
                var score = candNode?["score"]?.GetValue<double?>();
                var failureRate = candNode?["failure_rate"]?.GetValue<int?>();

                string status;
                if (exclusion == "no_data" || candNode is null)
                {
                    status = Format("GrassSmartTrainingScanExcluded", text["GrassSmartTrainingScanNoData"]);
                }
                else if (!string.IsNullOrEmpty(exclusion) && exclusion != "not_scored")
                {
                    status = Format("GrassSmartTrainingScanExcluded", ExclusionText(exclusion, failureRate, text));
                }
                else if (string.Equals(type, chosen, StringComparison.OrdinalIgnoreCase))
                {
                    status = text["GrassSmartTrainingScanWinner"];
                }
                else
                {
                    status = text["GrassSmartTrainingScanSafe"];
                }

                message.AppendLine(Format("GrassSmartTrainingScanOutcome",
                    Percent(failureRate, text),
                    score is { } s ? s.ToString("0.00", CultureInfo.InvariantCulture) : text["GrassSmartTrainingScanNotScored"],
                    status));
            }

            message.AppendLine();
            message.Append(!string.IsNullOrWhiteSpace(chosen)
                ? Format("GrassSmartTrainingScanChoice", Name(chosen, text))
                : text["GrassSmartTrainingScanRest"]);

            sink.Add(text["GrassSmartTrainingScanTitle"], message.ToString(), HachimiTaskLogEventKind.Detection);
        }
        catch
        {
            sink.Add(text["GrassSmartTrainingScanTitle"], jsonOrText, HachimiTaskLogEventKind.Detection);
        }
    }

    private static Dictionary<string, string> ReadCurrentUiText(
        ILocalizationService? localizationService,
        Func<string, string?>? lookup)
    {
        Dictionary<string, string> Snapshot(Func<string, string?>? resolve) =>
            EnglishFallback.ToDictionary(item => item.Key, item =>
            {
                var localized = resolve?.Invoke(item.Key);
                return string.IsNullOrWhiteSpace(localized) || localized == item.Key ? item.Value : localized;
            });

        if (lookup is not null)
            return Snapshot(lookup);

        if (localizationService is not null)
            return Snapshot(localizationService.GetString);

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

    private static string ExclusionText(string reason, int? failureRate, Dictionary<string, string> text) => reason switch
    {
        "core gains are unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownGains"],
        "failure rate is unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownFailure"],
        "expected gains are zero" => text["GrassSmartTrainingScanZeroGains"],
        _ when failureRate > 5 || reason.Contains("exceeds 5%") =>
            string.Format(CultureInfo.InvariantCulture, text["GrassSmartTrainingScanHighFailure"], 5),
        _ => reason,
    };
}
