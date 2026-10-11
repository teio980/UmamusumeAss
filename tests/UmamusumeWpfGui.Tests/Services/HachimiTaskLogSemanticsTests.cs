using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class HachimiTaskLogSemanticsTests
{
    [Fact]
    public void TeamRace_OnlyDescribesUserFacingSteps()
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.TeamRace,
            "raceagain",
            out var raceAgain));
        Assert.Contains("next race", raceAgain.StartMessage, StringComparison.OrdinalIgnoreCase);

        Assert.False(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.TeamRace,
            "teamShopProbe",
            out _));
        Assert.False(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.TeamRace,
            "shopBuy1",
            out _));
    }

    [Fact]
    public void MissionCollection_DescribesAvailabilityWithoutLeakingProbeNames()
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.MissionCollection,
            "dailyRed",
            out var availability));
        Assert.Contains("rewards", availability.StartMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No Daily Mission rewards", availability.SkippedMessage, StringComparison.OrdinalIgnoreCase);

        Assert.False(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.MissionCollection,
            "homeVerify",
            out _));
    }

    [Fact]
    public void IndependentTraining_DoesNotDisplayTechnicalActionIds()
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribeIndependentAction(
            "independent.agenda.slot.first_year.07_02",
            out var slot));
        Assert.Contains("agenda slot", slot.StartMessage, StringComparison.OrdinalIgnoreCase);

        Assert.False(HachimiTaskLogSemantics.TryDescribeIndependentAction(
            "independent.agenda.race.card.verify",
            out _));
        Assert.False(HachimiTaskLogSemantics.TryDescribeIndependentAction(
            "independent.skills.search.reset",
            out _));
    }

    [Theory]
    [InlineData("independent_mode_select", "Independent Training mode")]
    [InlineData("independent_strategy_option_late_click", "late race strategy")]
    [InlineData("independent_agenda_year_second_year_click", "second year agenda")]
    [InlineData("independent_agenda_slot_third_year_07_02", "third year agenda slot 07 02")]
    [InlineData("independent_training_start", "Starting Independent Training")]
    [InlineData("independent_post_start_home_probe", "Confirming the Home screen")]
    public void NativeIndependentTaskProgress_MapsUserFacingActions(
        string taskName,
        string expectedText)
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.Career,
            taskName,
            out var step));
        Assert.Contains(expectedText, step.StartMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(taskName, step.StartMessage, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(step.CompletedMessage));
        Assert.False(string.IsNullOrWhiteSpace(step.FailedMessage));
    }

    [Theory]
    [InlineData("career_main.training.select", "Career training")]
    [InlineData("training_selection.training.select", "Career training")]
    [InlineData("race_day.race.progress", "Career race")]
    [InlineData("career_main.rest.select", "rest")]
    public void NativeCareerActionEvents_MapScreenAndActionSemantics(
        string taskName,
        string expectedText)
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.Career,
            taskName,
            out var step));
        Assert.Contains(expectedText, step.StartMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(taskName, step.StartMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("career_main")]
    [InlineData("independent_agenda_year_first_year_probe")]
    [InlineData("independent_skills_search_reset")]
    [InlineData("independent_agenda_race_card_verify")]
    [InlineData("career_main.unknown.action")]
    public void NativeCareerProgress_StillHidesScreenAndInternalProbeNames(string taskName)
    {
        Assert.False(HachimiTaskLogSemantics.TryDescribe(
            HachimiTaskLogProfile.Career,
            taskName,
            out _));
    }

    [Fact]
    public void TechnicalFailures_AreCollapsedToUserFacingMessages()
    {
        var message = HachimiTaskLogSemantics.ToUserFacingFailure(
            "Timed out waiting for JSON task 'shopBuy1' after 10000ms.");

        Assert.Equal(
            "The game did not show the required screen or action in time.",
            message);
        Assert.DoesNotContain("shopBuy1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Localize_PreservesCareerSelectionDetailsAndActionState()
    {
        Assert.True(HachimiTaskLogSemantics.TryDescribeCareerAction(
            "career_main.training.select", out var action));
        var localized = HachimiTaskLogSemantics.Localize(action, new DictionaryLocalizationService(
            new Dictionary<string, string>
            {
                ["HachimiTaskLog_Selecting_Career_training"] = "正在选择 Career 训练。",
                ["HachimiTaskLog_Selecting_Training"] = "正在选择{0}训练。",
                ["HachimiTaskLog_Training_Career"] = "Career",
                ["HachimiTaskLog_Career_training_action_sent"] = "Career 训练操作已发送，正在确认结果。",
                ["HachimiTaskLog_Career_training_confirmed"] = "Career 训练已确认。",
                ["HachimiTaskLog_Could_not_send_the_Career_training_action"] = "无法发送 Career 训练操作。",
            }));

        Assert.Equal("正在选择 Career 训练。", localized.StartMessage);
        Assert.Equal("Career 训练操作已发送，正在确认结果。", localized.SentMessage);
        Assert.Equal("Career 训练已确认。", localized.CompletedMessage);
        Assert.Equal("无法发送 Career 训练操作。", localized.FailedMessage);
    }

    [Fact]
    public void Localize_PreservesAgendaYearAndSlotAndConfiguredStrategy()
    {
        var localization = new DictionaryLocalizationService(new Dictionary<string, string>
        {
            ["HachimiTaskLog_Selecting_Agenda_Slot"] = "正在选择{0}第 {1} 月、第 {2} 个时间段。",
            ["HachimiTaskLog_Year_third"] = "第三年",
            ["HachimiTaskLog_Selecting_Race_Strategy"] = "正在选择{0}比赛策略。",
            ["HachimiTaskLog_Strategy_late"] = "差し",
        });

        Assert.True(HachimiTaskLogSemantics.TryDescribeIndependentAction(
            "independent.agenda.slot.third_year.07_02", out var agenda));
        Assert.True(HachimiTaskLogSemantics.TryDescribeIndependentAction(
            "independent.strategy.option.late", out var strategy));

        Assert.Equal(
            "正在选择第三年第 07 月、第 02 个时间段。",
            HachimiTaskLogSemantics.Localize(agenda, localization).StartMessage);
        Assert.Equal(
            "正在选择差し比赛策略。",
            HachimiTaskLogSemantics.Localize(strategy, localization).StartMessage);
    }

    private sealed class DictionaryLocalizationService(IReadOnlyDictionary<string, string> strings)
        : ILocalizationService
    {
        public string CurrentCulture => "zh-CN";
        public event EventHandler<string>? LanguageChanged { add { } remove { } }
        public void Initialize() { }
        public void SwitchLanguage(string culture) { }
        public string GetString(string key) => strings.TryGetValue(key, out var value) ? value : key;
    }
}
