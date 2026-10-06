using System.Globalization;
using System.IO;
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
            var state = new UraCareerSessionState
            {
                TurnPositionLabel = "Classic July, first half",
                Energy = UraObservedValueFactory.FromObservation(80, 1),
            };
            var failure = allUnsafe ? 28 : 0;
            UraTrainingCandidate[] candidates =
            [
                UraTrainingCandidate.Reliable("speed", 21, 0, 9, 0, 0, 6, failure),
                UraTrainingCandidate.Reliable("stamina", 0, 13, 0, 7, 0, 4, failure),
                new("power", null, null, null, null, null, null, null),
                UraTrainingCandidate.Reliable("guts", 12, 0, 9, 16, 0, 7, failure),
                UraTrainingCandidate.Reliable("wit", 4, 0, 0, 0, 12, 5, 6),
            ];
            var decision = UraSmartTrainingScorer.Choose(candidates, UraTrainingDistance.Mile, "classic", 80);
            UraSmartTrainingScanLog.Write(log.ForTask("career"), state, decision, localization.GetString);

            var entry = Assert.Single(Assert.Single(log.Tasks).Entries);
            Assert.Equal(HachimiTaskLogEventKind.Detection, entry.Kind);
            Assert.Equal(Expected("Smart training scan", "智能训练扫描"), entry.Step);
            Assert.Contains(state.TurnPositionLabel, entry.Message);
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
            // Logging must not turn a selection into an executed training or advance a turn.
            Assert.False(state.TrainingTurnCommitPending);
            Assert.Null(state.TrainingClickIssuedType);
            Assert.Equal(0, state.TurnIndex);
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
            var state = new UraCareerSessionState();
            var decision = UraSmartTrainingScorer.Choose(
                [UraTrainingCandidate.Reliable("speed", 17, 0, 10, 0, 0, 6, 0)],
                UraTrainingDistance.Mile, "classic", 80);
            UraSmartTrainingScanLog.Write(sink, state, decision, localization.GetString);
            localization.SwitchLanguage("zh-CN");
            UraSmartTrainingScanLog.Write(sink, state, decision, localization.GetString);
            localization.SwitchLanguage("en-US");
            UraSmartTrainingScanLog.Write(sink, state, decision, localization.GetString);
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
            var decision = UraSmartTrainingScorer.Choose([], UraTrainingDistance.Mile, "classic", null);
            UraSmartTrainingScanLog.Write(log.ForTask("career"), new UraCareerSessionState
            {
                Energy = UraObservedValueFactory.Unknown<int>(),
            }, decision, key => key);
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
