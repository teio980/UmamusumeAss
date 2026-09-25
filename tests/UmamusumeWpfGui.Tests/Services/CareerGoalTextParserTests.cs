using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerGoalTextParserTests
{
    [Theory]
    [InlineData("7 turn(s) left", 7)]
    [InlineData("0 turns left", 0)]
    [InlineData("12 turns left", 12)]
    [InlineData("1", 1)]
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
    [InlineData("Participate in the Junior Make Debut")]
    public void Classifies_race_goal_from_visible_goal_text(string text)
    {
        Assert.Equal(CareerGoalTextParser.Race, CareerGoalTextParser.Classify(text));
    }

    [Theory]
    [InlineData("In G1, place within the top 3 2 time(s) Progress 2 time(s) left", 2)]
    [InlineData("Place 3rd or better in 2 G1 races Progress 1 time(s) left", 1)]
    [InlineData("In GI , place within the top 3 2 time(s) Detai <JIV2 time(s) left Progress", 2)]
    [InlineData("Place 3rd or better in 2 GI races Progress 1 time(s) left", 1)]
    public void Reads_race_count_grade_and_progress_from_ocr(string text, int remaining)
    {
        Assert.Equal(CareerGoalTextParser.GradeRaceCount, CareerGoalTextParser.Classify(text));
        Assert.Equal("G1", CareerGoalTextParser.ParseRaceGrade(text));
        Assert.Equal(remaining, CareerGoalTextParser.ParseRaceCountLeft(text));
    }

    [Theory]
    [InlineData("G1", "G1")]
    [InlineData("GI", "G1")]
    [InlineData("Gl", "G1")]
    [InlineData("G2", "G2")]
    [InlineData("GII", "G2")]
    [InlineData("G3", "G3")]
    [InlineData("GIII", "G3")]
    public void Reads_visible_race_grade(string text, string expected)
    {
        Assert.Equal(expected, CareerGoalTextParser.ParseRaceGrade(text));
    }

    [Fact]
    public void Race_count_without_a_readable_grade_does_not_become_a_grade_goal()
    {
        const string text = "Place within top 3. Progress 3 time(s) left";
        Assert.Equal(CareerGoalTextParser.Race, CareerGoalTextParser.Classify(text));
        Assert.Null(CareerGoalTextParser.ParseRaceGrade(text));
    }

    [Fact]
    public void Goal_achieved_replaces_the_remaining_race_count()
    {
        const string text = "In G1, place within the top 3 2 time(s) Goal Achieved!";
        Assert.Equal(CareerGoalTextParser.Completed, CareerGoalTextParser.Classify(text));
        Assert.Null(CareerGoalTextParser.ParseRaceCountLeft(text));
    }

    [Fact]
    public void Unknown_text_does_not_become_a_race()
    {
        Assert.Equal(CareerGoalTextParser.Unknown, CareerGoalTextParser.Classify("Career"));
    }
}
