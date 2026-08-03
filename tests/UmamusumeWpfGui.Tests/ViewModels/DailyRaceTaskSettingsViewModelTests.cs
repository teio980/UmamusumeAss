using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class DailyRaceTaskSettingsViewModelTests
{
    [Theory]
    [InlineData("0", 1)]
    [InlineData("3", 3)]
    [InlineData("6", 6)]
    [InlineData("9", 6)]
    public void RaceCount_is_clamped_to_the_daily_ticket_limit(string text, int expected)
    {
        var settings = new DailyRaceTaskSettingsViewModel
        {
            RaceCountText = text,
        };

        Assert.Equal(expected, settings.RaceCount);
    }

    [Fact]
    public void Mode_normalizes_to_one_of_the_two_daily_race_rewards()
    {
        var settings = new DailyRaceTaskSettingsViewModel();

        settings.Mode = "supportpoint";
        Assert.Equal(DailyRaceTaskSettingsViewModel.SupportPointMode, settings.Mode);

        settings.Mode = "unknown";
        Assert.Equal(DailyRaceTaskSettingsViewModel.MoniesMode, settings.Mode);
    }
}
