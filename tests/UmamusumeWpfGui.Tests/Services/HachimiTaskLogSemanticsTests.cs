using UmamusumeWpfGui.Services.Tasks;

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
}
