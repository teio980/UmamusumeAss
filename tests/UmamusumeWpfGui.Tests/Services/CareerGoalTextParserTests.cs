using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerGoalTextParserTests
{
    [Theory]
    [InlineData("7 turn(s) left", 7)]
    [InlineData("0 turns left", 0)]
    [InlineData("12 turns left", 12)]
    public void Parses_remaining_turns(string text, int expected)
    {
        Assert.Equal(expected, CareerGoalTextParser.ParseTurnsLeft(text));
    }

    [Fact]
    public void Classifies_fan_goal_from_visible_goal_text()
    {
        Assert.Equal(
            CareerGoalTextParser.Fans,
            CareerGoalTextParser.Classify("Earn 3000 fans Progress 1731 fan(s) to go"));
    }

    [Theory]
    [InlineData("Earn 3000 fans Progress 1731 fan(s) to go", 1731)]
    [InlineData("Progress 1,731 fans to go", 1731)]
    [InlineData("Progress 0 fan(s) to go", 0)]
    public void Parses_remaining_fans_from_visible_goal_text(string text, int expected)
    {
        Assert.Equal(expected, CareerGoalTextParser.ParseFansToGo(text));
    }

    [Theory]
    [InlineData("Place 3rd or better in the Arima Kinen")]
    [InlineData("Place 3rd or better in 2 G1 races")]
    [InlineData("Participate in the Junior Make Debut")]
    public void Classifies_race_goal_from_visible_goal_text(string text)
    {
        Assert.Equal(CareerGoalTextParser.Race, CareerGoalTextParser.Classify(text));
    }

    [Fact]
    public void Unknown_text_does_not_become_a_race()
    {
        Assert.Equal(CareerGoalTextParser.Unknown, CareerGoalTextParser.Classify("Career"));
    }
}
