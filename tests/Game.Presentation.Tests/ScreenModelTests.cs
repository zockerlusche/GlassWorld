using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

/// <summary>Game screen decisions without Godot (B6 R5, R7, R12, R15).</summary>
public sealed class ScreenModelTests
{
    private static readonly Contracts.WorldTimeSettings Time =
        ContractSerializer.Deserialize<Contracts.WorldTimeSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    [Fact]
    public void TimeChoicesComeFromTheData()
    {
        var choices = ScreenModel.TimeChoices(Time);
        Assert.Equal(0, choices[0].Speed);
        Assert.Equal(1, choices[1].Speed);
        Assert.Equal(Time.FastForwardFactors.Order().Select(f => (double)f), choices.Skip(2).Select(c => c.Speed));
        Assert.All(choices.Skip(2), c => Assert.Equal("ui.time.fast", c.TooltipKey));
        Assert.All(choices, c => Assert.DoesNotContain(c.Symbol, char.IsLetter));
    }

    [Fact]
    public void OnlyNewQuestionsBecomeHintsInOneGroup()
    {
        var rows = new[] { new InhabitantRow("inh-000002", 2, 80, false, null, 1) };
        var events = new[]
        {
            new WorldEvent("question_opened", 5, "inh-000002", null, "q-1"),
            new WorldEvent("question_answered", 6, "inh-000002", null, "q-1"),
            new WorldEvent(InhabitantsSystem.ActionStarted, 6, "inh-000002", "eat", null),
        };
        var hint = Assert.Single(ScreenModel.Hints(events, rows));
        Assert.Equal("msg.hint.question", hint.TextKey);
        Assert.Equal("question", hint.Group);
        Assert.Equal("2", hint.Args["number"]);
    }

    [Fact]
    public void PickTakesTheNearestFigureWithinTheRadius()
    {
        var figures = new[] { ("a", 100.0, 100.0), ("b", 130.0, 100.0), ("c", 400.0, 400.0) };
        Assert.Equal("b", ScreenModel.Pick(figures, 125, 100, 48));
        Assert.Null(ScreenModel.Pick(figures, 250, 250, 48));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "?")]
    [InlineData(2, "??")]
    [InlineData(7, "???")]
    public void QuestionMarksShowHowManyUpToThree(int open, string expected) => Assert.Equal(expected, ScreenModel.QuestionMarks(open));
}
