using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// View models against the real core (placeholder start world, mock decider): what the inhabitant
/// list, card, clock and open-questions window show (B6 R5, R6, R12; ADR-0008).
/// </summary>
public sealed class WorldFeedTests
{
    private static SimulationHost NewWorld(ulong seed = 1) =>
        SimulationHost.CreatePlaceholderWorld(Path.Combine(TestPaths.Root, "content"), seed, new MockDecisionPort(), "de");

    [Fact]
    public void ListShowsEveryInhabitantWithNeutralNumber()
    {
        var host = NewWorld();
        var rows = new WorldFeed(host).Inhabitants();
        Assert.Equal(host.Inhabitants.All.Count(), rows.Count);
        Assert.All(rows, r =>
        {
            Assert.InRange(r.WillToLivePercent, 0, 100);
            Assert.True(r.Number > 0);
        });
        Assert.Equal(rows.Select(r => r.Number).Distinct().Count(), rows.Count);
    }

    [Fact]
    public void ListCarriesTheOwnNames()
    {
        // W1698: every inhabitant has its own name (#171), the list shows it instead of the number.
        var host = NewWorld();
        var feed = new WorldFeed(host);
        var rows = feed.Inhabitants();
        Assert.All(rows, r => Assert.Equal(host.Inhabitants.All.Single(i => i.Id == r.Id).Name, r.Name));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
        Assert.All(rows, r => Assert.Equal(r.Name, feed.OwnName(r.Id)));
        Assert.Null(feed.OwnName("inh-999999"));
    }

    [Fact]
    public void CardGroupsValuesAsTextKeys()
    {
        var host = NewWorld();
        var feed = new WorldFeed(host);
        var card = feed.Card(feed.Inhabitants()[0].Id);
        Assert.NotNull(card);
        Assert.NotEmpty(card.Needs);
        Assert.All(card.Needs, v => Assert.StartsWith("vocab.need.", v.Key, StringComparison.Ordinal));
        Assert.All(card.Traits, v => Assert.StartsWith("vocab.trait.", v.Key, StringComparison.Ordinal));
        Assert.All(card.Feelings, v => Assert.True(v.Percent > 0));
        Assert.Equal(card.Needs.Select(v => v.Key).Order(StringComparer.Ordinal), card.Needs.Select(v => v.Key));
    }

    [Fact]
    public void ANeedAnInventionBroughtShowsItsNameAndFadesWhenEmpty()
    {
        // A1595 (#290): such a need has no text key - the card takes its name from the need register; empty, it fades.
        var host = NewWorld();
        Assert.Null(host.Needs.Add(new GlassWorld.Contracts.NewNeed { Id = "glow", NameDe = "Leuchten", NameEn = "Glow", DecayPerDay = 0.2, Actions = ["use"] }, "inv_lamp"));
        Assert.Null(host.Needs.Add(new GlassWorld.Contracts.NewNeed { Id = "rhythm", NameDe = "Takt", NameEn = "Rhythm", DecayPerDay = 0.2, Actions = ["use"] }, "inv_drum"));
        var inhabitant = host.Inhabitants.All.First();
        inhabitant.Needs["glow"] = 0.6;
        inhabitant.Needs["rhythm"] = 0.0;

        var card = new WorldFeed(host).Card(inhabitant.Id)!;

        var glow = Assert.Single(card.Needs, v => v.Key == "vocab.need.glow");
        Assert.Equal((("Leuchten", "Glow"), false, 60), (glow.Name!.Value, glow.Faded, glow.Percent));
        Assert.True(Assert.Single(card.Needs, v => v.Key == "vocab.need.rhythm").Faded);
        Assert.All(card.Needs.Where(v => v.Key is not ("vocab.need.glow" or "vocab.need.rhythm")), v => Assert.Equal(((string, string)?)null, v.Name));
        Assert.DoesNotContain(card.Needs, v => v.Name is null && v.Faded);
    }

    [Fact]
    public void CardOfUnknownInhabitantIsNull() => Assert.Null(new WorldFeed(NewWorld()).Card("inh-999999"));

    [Fact]
    public void ClockUsesTimeKeys()
    {
        var clock = new WorldFeed(NewWorld()).Clock();
        Assert.StartsWith("time.season.", clock.SeasonKey, StringComparison.Ordinal);
        Assert.StartsWith("time.phase.", clock.PhaseKey, StringComparison.Ordinal);
        Assert.Matches("^[0-2][0-9]:[0-5][0-9]$", clock.Time);
        Assert.True(clock.Year >= 1 && clock.Day >= 1);
    }

    [Fact]
    public void SnapshotIsReusedWithinOneTick()
    {
        var host = NewWorld();
        var feed = new WorldFeed(host);
        var first = feed.Snapshot;
        Assert.Same(first, feed.Snapshot);
        host.World.Step();
        Assert.NotSame(first, feed.Snapshot);
    }

    [Fact]
    public void NoEventIsLostWhenSeveralTicksPassPerFrame()
    {
        var host = NewWorld(3);
        var feed = new WorldFeed(host);
        var seen = new List<WorldEvent>();
        for (var frame = 0; frame < 20; frame++)
        {
            for (var tick = 0; tick < 64; tick++)
            {
                host.World.Step();
            }

            seen.AddRange(feed.NewEvents());
            Assert.False(feed.MissedEvents);
        }

        Assert.NotEmpty(seen);
        Assert.Equal(host.Inhabitants.EventsSince(0).Count, seen.Count);
    }

    [Fact]
    public void ClickingAnInhabitantShowsAnOpenQuestionInTheWindow()
    {
        var host = NewWorld();
        var feed = new WorldFeed(host);
        var id = feed.Inhabitants()[0].Id;
        Assert.True(host.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, id)).Accepted);
        host.World.Step();

        var window = feed.OpenQuestions();
        Assert.Contains(window, q => q.InhabitantId == id && q.Count >= 1);
        Assert.Contains(feed.NewEvents(), e => e.Kind == "question_opened" && e.InhabitantId == id);
        Assert.True(feed.Inhabitants().Single(r => r.Id == id).OpenQuestions >= 1);
    }

    [Fact]
    public void AFreeMomentIsNotListedAsAQuestion()
    {
        // #309 follow-up: the questions window listed every free moment with a whisper field.
        var host = NewWorld();
        var feed = new WorldFeed(host);
        var seen = false;
        for (var i = 0; i < 2000 && !seen; i++)
        {
            host.World.Step();
            var open = host.Decisions.Open.Where(q => q.Kind == DecisionsSystem.Leisure).Select(q => q.Id).ToList();
            seen = open.Count > 0;
            Assert.DoesNotContain(feed.Questions(), q => open.Contains(q.Id));
        }

        Assert.True(seen);
    }

    [Fact]
    public void EveryDayPhaseHasATextKeyAndAVocabularyId()
    {
        var keys = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", "en.json")))!["entries"]!.AsArray()
            .Select(e => (string)e!["key"]!).ToHashSet(StringComparer.Ordinal);
        var vocabulary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "time_of_day.json")))!["entries"]!.AsArray()
            .Select(e => (string)e!["id"]!).ToHashSet(StringComparer.Ordinal);
        foreach (var phase in Enum.GetValues<GlassWorld.Simulation.Time.DayPhase>())
        {
            Assert.Contains($"time.phase.{WorldFeed.Phase(phase)}", keys);
            Assert.Contains(WorldFeed.Phase(phase), vocabulary);
        }
    }

    [Fact]
    public void FeedIsBoundToItsHost()
    {
        var host = NewWorld();
        Assert.Same(host, new WorldFeed(host).Host);
    }

    [Fact]
    public void OpenQuestionsCountAllButPuzzlingOnesDropThoseWhereOnlyTheEffectIsOpen()
    {
        // TL #19921 / review GW3 #142: list, card and window show every open question; only the head mark
        // leaves out a question about a thing the inhabitant has looked at since.
        var host = NewWorld();
        OpenQuestion? question = null;
        for (var i = 0; i < 200 && question is null; i++)
        {
            host.World.Step();
            question = host.Decisions.Open.FirstOrDefault(q => q.Subject.StartsWith("type:", StringComparison.Ordinal));
        }

        Assert.NotNull(question);
        var id = question.InhabitantId;
        var open = host.Decisions.OpenQuestionIds(id).Count;
        var before = new WorldFeed(host).Inhabitants().Single(r => r.Id == id);
        Assert.Equal((open, open), (before.OpenQuestions, before.PuzzlingQuestions));

        var seen = host.Content.Knowledge.Looking!.VisibleProperties.First();
        host.Inhabitants.Get(id).Beliefs.Set(question.Subject["type:".Length..], seen, false, 0.8);
        var feed = new WorldFeed(host);
        var after = feed.Inhabitants().Single(r => r.Id == id);
        Assert.Equal((open, open - 1), (after.OpenQuestions, after.PuzzlingQuestions));
        Assert.Equal(open, feed.OpenQuestions().Single(q => q.InhabitantId == id).Count);
    }

    [Theory]
    [InlineData("inh-000004", 4)]
    [InlineData("inh-000120", 120)]
    [InlineData("x", 0)]
    public void NumberComesFromTheId(string id, int expected) => Assert.Equal(expected, WorldFeed.NumberOf(id));
}
