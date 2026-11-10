using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

/// <summary>Game screen decisions without Godot (B6 R5, R7, R12, R15).</summary>
public sealed class ScreenModelTests
{
    private const string Unnamed = "Bewohner {number}";

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
        var hint = Assert.Single(ScreenModel.Hints(events, rows, Unnamed));
        Assert.Equal("msg.hint.question", hint.TextKey);
        Assert.Equal("question", hint.Group);
        Assert.Equal("2", hint.Args["number"]);
    }

    [Theory]
    [InlineData("Toma", "Toma")]
    [InlineData(null, "Bewohner 2")]
    [InlineData("  ", "Bewohner 2")]
    public void TheScreenShowsTheOwnNameAndTheNumberOnlyWithoutOne(string? name, string expected)
    {
        // W1698: the list, card, logs and hints show the real name; "Inhabitant N" is the fallback only.
        var row = new InhabitantRow("inh-000002", 2, 80, false, null, 1, Name: name);
        Assert.Equal(expected, ScreenModel.DisplayName(row, Unnamed));
        var hint = Assert.Single(ScreenModel.Hints([new WorldEvent("question_opened", 5, "inh-000002", null, "q-1")], [row], Unnamed));
        Assert.Equal(expected, hint.Args["name"]);
    }

    [Fact]
    public void TheSameQuestionIsHintedOnceAndLeadsToTheInhabitant()
    {
        var rows = new[] { new InhabitantRow("inh-000002", 2, 80, false, null, 1) };
        var hinted = new HashSet<string>(StringComparer.Ordinal);
        string? Subject(string id) => "noticed_player:player";
        var first = Assert.Single(ScreenModel.Hints([new WorldEvent("question_opened", 5, "inh-000002", null, "q-1")], rows, Unnamed, Subject, hinted));
        Assert.Equal("inh-000002", first.LookAt);
        Assert.Empty(ScreenModel.Hints([new WorldEvent("question_opened", 9, "inh-000002", null, "q-2")], rows, Unnamed, Subject, hinted));
    }

    /// <summary>An expired queued answer (F5275) belongs to a question already answered: no question hint.</summary>
    [Fact]
    public void AnExpiredAnswerIsNoQuestion()
    {
        var rows = new[] { new InhabitantRow("inh-000002", 2, 80, false, null, 0) };
        var events = new[] { new WorldEvent(InhabitantsSystem.AnswerExpired, 7, "inh-000002", "sleep", null) { Detail = InhabitantsSystem.NeedNeglected } };
        Assert.Empty(ScreenModel.Hints(events, rows, Unnamed));
    }

    [Fact]
    public void NoAnswerToTalkingGivesOneCalmHint()
    {
        // B6 R16: the service is off or too slow; several unanswered sentences bundle into one hint.
        var rows = new[] { new InhabitantRow("inh-000001", 1, 80, false, null, 0) };
        var events = new[] { new WorldEvent("no_answer", 9, "inh-000001", null, null), new WorldEvent("no_answer", 9, "inh-000002", null, null) };
        var hint = Assert.Single(ScreenModel.Hints(events, rows, Unnamed));
        Assert.Equal("msg.hint.no_answer", hint.TextKey);
    }

    [Fact]
    public void ACallForHelpBecomesAHintNamingTheNeed()
    {
        var rows = new[] { new InhabitantRow("inh-000003", 3, 80, false, null, 0) };
        var events = new[] { new WorldEvent(Simulation.Player.PlayerSystem.HelpNeeded, 9, "inh-000003", null, null) { Detail = "thirst" } };
        var hint = Assert.Single(ScreenModel.Hints(events, rows, Unnamed));
        Assert.Equal("msg.hint.help_needed", hint.TextKey);
        Assert.Equal("vocab.need.thirst", hint.Args["need"]);
        Assert.Equal("3", hint.Args["number"]);

        // Without an own name the hint reads "Bewohner 3 ...", the filled unnamed text, never a bare "3" (one rule).
        Assert.Equal("Bewohner 3", hint.Args["name"]);
        Assert.Equal("Bewohner 3 findet nichts gegen Durst.", Ui.TextFill.Fill("{name} findet nichts gegen Durst.", hint.Args));
    }

    [Fact]
    public void PickTakesTheNearestFigureWithinTheRadius()
    {
        var figures = new[] { ("a", 100.0, 100.0), ("b", 130.0, 100.0), ("c", 400.0, 400.0) };
        Assert.Equal("b", ScreenModel.Pick(figures, 125, 100, 48));
        Assert.Null(ScreenModel.Pick(figures, 250, 250, 48));
    }

    [Theory]
    [InlineData(1920, 1400, 500, 16, 1400)]
    [InlineData(960, 500, 480, 16, 960)]
    [InlineData(960, 512, 480, 16, 512)]
    public void CardStaysLeftOfThePanelsOrBecomesForeground(double area, double free, double card, double margin, double expected)
        => Assert.Equal(expected, ScreenModel.CardRoom(area, free, card, margin));

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "?")]
    [InlineData(2, "??")]
    [InlineData(7, "???")]
    public void QuestionMarksShowHowManyUpToThree(int open, string expected) => Assert.Equal(expected, ScreenModel.QuestionMarks(open));
}
