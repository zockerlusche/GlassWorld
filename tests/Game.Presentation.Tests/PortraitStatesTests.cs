using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// A1752 (B2792, Marcel W2166/W2167): the states of a portrait in the inhabitants corner - knock, worry, aha, question -
/// one at a time, the most important; they rank the corner; values and words come from the data.
/// </summary>
public sealed class PortraitStatesTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));
    private static readonly UiPortraitStates Data = Tokens.PortraitStates;
    private static readonly string[] StateKeys = ["ui.corner.knocking", "ui.corner.worry", "ui.corner.aha", "ui.corner.brooding"];

    private static InhabitantRow Row(int i) => new($"inh-{i:000000}", i, 80, false, null, 0, Name: $"Name{i}");

    [Fact]
    public void TheAhaEventsAreTheInhabitantsOwnSuccessesFromTheVocabulary()
    {
        // GW1 #27065: invented, built, a new word - name_learned and knowledge_gained would come far too often.
        Assert.Equal(["invented", "built", "word_heard"], Data.AhaEvents);
        var events = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "events.json")))!["entries"]!.AsArray().Select(e => (string)e!["id"]!).ToHashSet(StringComparer.Ordinal);
        Assert.All(Data.AhaEvents, e => Assert.Contains(e, events));

        // B2792: twice short, then a pause; an aha stays about one game day.
        Assert.Equal(2, Data.KnockTaps);
        Assert.Equal(24, Data.AhaHours);
    }

    [Fact]
    public void OneStateAtATimeTheMostImportant()
    {
        var brooding = Row(1) with { PuzzlingQuestions = 1 };
        var neglected = brooding with { NeglectHours = Data.WorryAfterHours };

        Assert.Equal(PortraitState.None, CornerModel.State(Row(1), knocking: false, aha: false, Data));
        Assert.Equal(PortraitState.Question, CornerModel.State(brooding, knocking: false, aha: false, Data));
        Assert.Equal(PortraitState.Aha, CornerModel.State(brooding, knocking: false, aha: true, Data));
        Assert.Equal(PortraitState.Worry, CornerModel.State(neglected, knocking: false, aha: true, Data));
        Assert.Equal(PortraitState.Knock, CornerModel.State(neglected, knocking: true, aha: true, Data));
    }

    [Fact]
    public void WorryIsALongNeglectOrFading()
    {
        Assert.False(CornerModel.Worried(Row(1) with { NeglectHours = Data.WorryAfterHours - 0.5 }, Data));
        Assert.True(CornerModel.Worried(Row(1) with { NeglectHours = Data.WorryAfterHours }, Data));
        Assert.True(CornerModel.Worried(Row(1) with { Fading = true }, Data));
    }

    [Fact]
    public void TheStatesRankTheCornerBeforeTheFavourites()
    {
        // B2792: knock > worry > aha > question > favourites - with many inhabitants all four states stay in the corner.
        var rows = Enumerable.Range(1, 50).Select(i => i <= 10 ? Row(i) with { Favourite = true } : Row(i)).ToList();
        var states = new Dictionary<string, PortraitState>(StringComparer.Ordinal)
        {
            [rows[44].Id] = PortraitState.Question,
            [rows[45].Id] = PortraitState.Aha,
            [rows[46].Id] = PortraitState.Worry,
            [rows[47].Id] = PortraitState.Knock,
        };
        var (shown, more) = CornerModel.Shown(rows, Tokens.Zones, null, r => states.GetValueOrDefault(r.Id));
        Assert.Equal(Tokens.Zones.PortraitsImportantMax, shown.Count);
        Assert.All(states.Keys, id => Assert.Contains(shown, r => r.Id == id));
        Assert.Equal(50 - shown.Count, more);

        // Still in the order of the list, so nobody jumps.
        Assert.Equal(shown.OrderBy(r => r.Number).Select(r => r.Id), shown.Select(r => r.Id));
    }

    [Fact]
    public void TheKnockRingPulsesTwiceShortThenRestsAndStandsStillWithReducedMotion()
    {
        var tap = Data.KnockTapS;
        Assert.Equal(1, CornerModel.KnockPulse(Data, tap / 2, reduceMotion: false), 3);
        Assert.Equal(0, CornerModel.KnockPulse(Data, tap + (Data.KnockGapS / 2), reduceMotion: false), 3);
        Assert.Equal(1, CornerModel.KnockPulse(Data, tap + Data.KnockGapS + (tap / 2), reduceMotion: false), 3);
        Assert.Equal(0, CornerModel.KnockPulse(Data, (2 * tap) + Data.KnockGapS + (Data.KnockPauseS / 2), reduceMotion: false), 3);

        // Reduced motion (XAG 117): a fixed ring, at every moment.
        foreach (var t in (double[])[0.0, tap / 2, tap + (Data.KnockGapS / 2), 5.0])
        {
            Assert.Equal(1, CornerModel.KnockPulse(Data, t, reduceMotion: true));
        }
    }

    [Fact]
    public void AnAhaStaysUntilClickedOrAGameDayPassed()
    {
        const int TicksPerHour = 60;
        var marks = new AhaMarks(Data, TicksPerHour);
        marks.Take([new WorldEvent("invented", 100, "inh-000001", null, null), new WorldEvent("said", 100, "inh-000002", null, null)]);
        Assert.True(marks.Shows("inh-000001", 101));
        Assert.False(marks.Shows("inh-000002", 101));
        Assert.True(marks.Shows("inh-000001", 100 + (long)((Data.AhaHours - 1) * TicksPerHour)));
        Assert.False(marks.Shows("inh-000001", 100 + (long)(Data.AhaHours * TicksPerHour)));

        marks.Take([new WorldEvent("built", 200, "inh-000003", null, null)]);
        marks.Seen("inh-000003");
        Assert.False(marks.Shows("inh-000003", 201));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryStateHasItsWords(string lang)
    {
        var keys = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", lang + ".json")))!["entries"]!.AsArray().Select(e => (string)e!["key"]!).ToHashSet(StringComparer.Ordinal);
        Assert.All(StateKeys, k => Assert.Contains(k, keys));
    }
}
