using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Xml.Linq;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingScanLogTests
{
    [Theory]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("zh-CN", false)]
    [InlineData("zh-CN", true)]
    public void Frontend_card_follows_app_language_even_when_thread_language_differs(string culture, bool allUnsafe)
    {
        var localization = CreateLocalization(culture);
        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture == "en-US" ? "zh-CN" : "en-US");
        var english = culture == "en-US";
        string Expected(string en, string zh) => english ? en : zh;
        var log = new HachimiTaskLogViewModel();
        log.BeginRun([("career", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        try
        {
            var date = "Classic July, first half";
            var energy = 80;
            var failure = allUnsafe ? 28 : 0;
            var payload = new
            {
                date,
                energy,
                chosen = allUnsafe ? null : "speed",
                candidates = new object[]
                {
                    new { type = "speed", speed = 21, stamina = 0, power = 9, guts = 0, wit = 0, skill_points = 6, failure_rate = failure, score = allUnsafe ? (double?)null : 36.5, exclusion_reason = allUnsafe ? "failure rate exceeds 5%" : null },
                    new { type = "stamina", speed = 0, stamina = 13, power = 0, guts = 7, wit = 0, skill_points = 4, failure_rate = failure, score = allUnsafe ? (double?)null : 24.0, exclusion_reason = allUnsafe ? "failure rate exceeds 5%" : null },
                    new { type = "power", speed = (int?)null, stamina = (int?)null, power = (int?)null, guts = (int?)null, wit = (int?)null, skill_points = (int?)null, failure_rate = (int?)null, score = (double?)null, exclusion_reason = "no_data" },
                    new { type = "guts", speed = 12, stamina = 0, power = 9, guts = 16, wit = 0, skill_points = 7, failure_rate = failure, score = allUnsafe ? (double?)null : 35.0, exclusion_reason = allUnsafe ? "failure rate exceeds 5%" : null },
                    new { type = "wit", speed = 4, stamina = 0, power = 0, guts = 0, wit = 12, skill_points = 5, failure_rate = 6, score = (double?)null, exclusion_reason = "failure rate exceeds 5%" },
                }
            };
            var json = JsonSerializer.Serialize(payload);

            UraSmartTrainingScanLog.WriteJson(log.ForTask("career"), json, localization);

            var entry = Assert.Single(Assert.Single(log.Tasks).Entries);
            Assert.Equal(HachimiTaskLogEventKind.Detection, entry.Kind);
            Assert.Equal(Expected("Smart training scan", "智能训练扫描"), entry.Step);
            Assert.Contains(date, entry.Message);
            Assert.Contains(Expected("Energy: 80%", "体力：80%"), entry.Message);
            Assert.Contains(Expected("Speed training:", "速度训练："), entry.Message);
            Assert.Contains(Expected("Stamina training:", "耐力训练："), entry.Message);
            Assert.Contains(Expected("Power training:", "力量训练："), entry.Message);
            Assert.Contains(Expected("Guts training:", "根性训练："), entry.Message);
            Assert.Contains(Expected("Wit training:", "智力训练："), entry.Message);
            Assert.Contains(Expected("Skill points", "技能点"), entry.Message);
            Assert.Contains(Expected("Failure rate: Unknown", "失败率：未知"), entry.Message);
            Assert.Contains(Expected("Excluded:", "排除："), entry.Message);
            Assert.Contains(Expected("Failure rate exceeds 5%", "失败率超过 5%"), entry.Message);
            Assert.Contains(allUnsafe
                ? Expected("return to the Career main screen and rest", "返回主界面休息")
                : Expected("Speed training (pending confirmation)", "速度训练（待确认）"), entry.Message);
            if (english)
            {
                Assert.DoesNotContain(entry.Message, character => character is >= '\u4e00' and <= '\u9fff');
                Assert.DoesNotContain("GrassSmartTraining", entry.Message);
            }
        }
        finally
        {
            log.EndRun();
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void A_language_switch_applies_to_the_next_card_without_restarting_the_scan_logger()
    {
        var localization = CreateLocalization("en-US");
        var log = new HachimiTaskLogViewModel();
        log.BeginRun([("career", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        try
        {
            var sink = log.ForTask("career");
            var json = JsonSerializer.Serialize(new
            {
                date = "Classic Year",
                energy = 80,
                chosen = "speed",
                candidates = new[]
                {
                    new { type = "speed", speed = 17, stamina = 0, power = 10, guts = 0, wit = 0, skill_points = 6, failure_rate = 0, score = (double?)28.5, exclusion_reason = (string?)null }
                }
            });

            UraSmartTrainingScanLog.WriteJson(sink, json, localization);
            localization.SwitchLanguage("zh-CN");
            UraSmartTrainingScanLog.WriteJson(sink, json, localization);
            localization.SwitchLanguage("en-US");
            UraSmartTrainingScanLog.WriteJson(sink, json, localization);

            var entries = Assert.Single(log.Tasks).Entries;
            Assert.Equal(3, entries.Count);
            Assert.Equal("Smart training scan", entries[0].Step);
            Assert.Equal("智能训练扫描", entries[1].Step);
            Assert.Equal("Smart training scan", entries[2].Step);
        }
        finally
        {
            log.EndRun();
        }
    }

    [Fact]
    public void Missing_localization_resources_fall_back_to_English()
    {
        var log = new HachimiTaskLogViewModel();
        log.BeginRun([("career", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                date = (string?)null,
                energy = (int?)null,
                chosen = (string?)null,
                candidates = Array.Empty<object>()
            });

            UraSmartTrainingScanLog.WriteJson(log.ForTask("career"), json, null, key => key);
            var entry = Assert.Single(Assert.Single(log.Tasks).Entries);
            Assert.Equal("Smart training scan", entry.Step);
            Assert.Contains("Energy: Unknown", entry.Message);
            Assert.DoesNotContain(entry.Message, character => character is >= '\u4e00' and <= '\u9fff');
        }
        finally
        {
            log.EndRun();
        }
    }

    private static LocalizationService CreateLocalization(string culture)
    {
        var service = new LocalizationService(new LanguageSettings(culture), new ResourceDictionary(), selected =>
        {
            var path = Path.Combine(CareerTestResourceResolver.FindWorkspaceRoot(), "src",
                "UmamusumeWpfGui", "Resources", $"Strings.{selected}.xaml");
            var dictionary = new ResourceDictionary();
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var element in XDocument.Load(path).Root!.Elements())
                if ((string?)element.Attribute(x + "Key") is { } key)
                    dictionary[key] = element.Value;
            return dictionary;
        });
        service.Initialize();
        return service;
    }

    private sealed class LanguageSettings(string culture) : ISettingsService
    {
        private ConnectionSettings _settings = new() { Language = culture };
        public ConnectionSettings Load() => _settings;
        public void Save(ConnectionSettings settings) => _settings = settings;
    }
}
