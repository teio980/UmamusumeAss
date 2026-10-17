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
        ["GrassSmartTrainingScanHeader"] = "{0} | Energy: {1} -> {2}",
        ["GrassSmartTrainingScanChoice"] = "Choice: {0} training",
        ["GrassSmartTrainingScanRest"] = "Return to Career main screen and rest",
        ["GrassSmartTrainingScanRow"] = "• {0}: {1}",
        ["GrassSmartTrainingScanScoredWinner"] = "{0} | Fail {1} | Score {2} [Selected]",
        ["GrassSmartTrainingScanScoredCandidate"] = "{0} | Fail {1} | Score {2}",
        ["GrassSmartTrainingScanExcludedWithGains"] = "{0} | Fail {1} (Excluded: {2})",
        ["GrassSmartTrainingScanNotScoredWithGains"] = "{0} | Fail {1} (Not scored)",
        ["GrassSmartTrainingScanNoDataExcluded"] = "No reliable scan data (Excluded)",
        ["GrassSmartTrainingScanNoGains"] = "No gain",
        ["GrassSmartTrainingScanUnknownGains"] = "Gains unknown",
        ["GrassSmartTrainingStatSpeed"] = "Spd",
        ["GrassSmartTrainingStatStamina"] = "Sta",
        ["GrassSmartTrainingStatPower"] = "Pwr",
        ["GrassSmartTrainingStatGuts"] = "Gut",
        ["GrassSmartTrainingStatWit"] = "Wit",
        ["GrassSmartTrainingStatSkillPoints"] = "Pt",
        ["GrassSmartTrainingScanUnknownGainsReason"] = "Gains below confidence threshold",
        ["GrassSmartTrainingScanUnknownFailure"] = "Failure rate below confidence threshold",
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

            var choiceText = !string.IsNullOrWhiteSpace(chosen)
                ? Format("GrassSmartTrainingScanChoice", Name(chosen, text))
                : text["GrassSmartTrainingScanRest"];

            var message = new StringBuilder();
            message.AppendLine(Format("GrassSmartTrainingScanHeader", date, energy, choiceText));

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

            for (var i = 0; i < SupportedTypes.Length; i++)
            {
                var type = SupportedTypes[i];
                candidatesMap.TryGetValue(type, out var candNode);

                var exclusion = candNode?["exclusion_reason"]?.GetValue<string?>();
                var score = candNode?["score"]?.GetValue<double?>();
                var failureRate = candNode?["failure_rate"]?.GetValue<int?>();

                string detail;
                if (exclusion == "no_data" || candNode is null)
                {
                    detail = text["GrassSmartTrainingScanNoDataExcluded"];
                }
                else if (!string.IsNullOrEmpty(exclusion) && exclusion != "not_scored")
                {
                    var gains = FormatGains(candNode, text);
                    var reason = ExclusionText(exclusion, failureRate, text);
                    detail = Format("GrassSmartTrainingScanExcludedWithGains",
                        gains,
                        Percent(failureRate, text),
                        reason);
                }
                else if (score is null)
                {
                    var gains = FormatGains(candNode, text);
                    detail = Format("GrassSmartTrainingScanNotScoredWithGains",
                        gains,
                        Percent(failureRate, text));
                }
                else if (string.Equals(type, chosen, StringComparison.OrdinalIgnoreCase))
                {
                    var gains = FormatGains(candNode, text);
                    detail = Format("GrassSmartTrainingScanScoredWinner",
                        gains,
                        Percent(failureRate, text),
                        score.Value.ToString("0.00", CultureInfo.InvariantCulture));
                }
                else
                {
                    var gains = FormatGains(candNode, text);
                    detail = Format("GrassSmartTrainingScanScoredCandidate",
                        gains,
                        Percent(failureRate, text),
                        score.Value.ToString("0.00", CultureInfo.InvariantCulture));
                }

                var row = Format("GrassSmartTrainingScanRow", Name(type, text), detail);
                if (i < SupportedTypes.Length - 1)
                    message.AppendLine(row);
                else
                    message.Append(row);
            }

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

    private static string FormatGains(JsonNode? node, Dictionary<string, string> text)
    {
        if (node is null)
            return text["GrassSmartTrainingScanUnknownGains"];

        var list = new List<string>(6);
        void CheckStat(string key, string statLabelKey)
        {
            var val = node[key]?.GetValue<int?>();
            if (val > 0)
                list.Add($"{text[statLabelKey]}+{val.Value.ToString(CultureInfo.InvariantCulture)}");
            else if (val < 0)
                list.Add($"{text[statLabelKey]}{val.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        CheckStat("speed", "GrassSmartTrainingStatSpeed");
        CheckStat("stamina", "GrassSmartTrainingStatStamina");
        CheckStat("power", "GrassSmartTrainingStatPower");
        CheckStat("guts", "GrassSmartTrainingStatGuts");
        CheckStat("wit", "GrassSmartTrainingStatWit");
        CheckStat("skill_points", "GrassSmartTrainingStatSkillPoints");

        if (list.Count > 0)
            return string.Join(" ", list);

        bool hasAny = node["speed"] != null || node["stamina"] != null || node["power"] != null
            || node["guts"] != null || node["wit"] != null || node["skill_points"] != null;

        return hasAny ? text["GrassSmartTrainingScanNoGains"] : text["GrassSmartTrainingScanUnknownGains"];
    }

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
        "core gains are unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownGainsReason"],
        "failure rate is unknown or below confidence threshold" => text["GrassSmartTrainingScanUnknownFailure"],
        "expected gains are zero" => text["GrassSmartTrainingScanZeroGains"],
        _ when failureRate > 5 || reason.Contains("exceeds 5%") =>
            string.Format(CultureInfo.InvariantCulture, text["GrassSmartTrainingScanHighFailure"], 5),
        _ => reason,
    };
}
