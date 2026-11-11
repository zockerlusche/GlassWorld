using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1526, max_requests_per_step: at most N requests go to the decider in one step; what is over waits with its
/// question mark and goes out in the next steps. Nothing is lost and nothing is asked that would not have been.
/// </summary>
public sealed class RequestsPerStepTests
{
    private const string Thing = "flint";

    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly Position[] Places = [new(0, 0), new(5000, 5000), new(10000, 0), new(0, 10000)];

    [Fact]
    public void TheDataBoundTheRequestsOfOneStep()
    {
        Assert.True(Settings().MaxRequestsPerStep >= 1);
    }

    [Fact]
    public void WithoutTheValueEveryQuestionOfAStepGoesOutInIt()
    {
        var w = NewWorld(Settings(s => s.MaxRequestsPerStep = null));
        w.SpawnAThingBesideEveryone();
        w.World.Step();

        Assert.Equal(Places.Length, w.Requests);
        Assert.All(w.Decisions.Open, q => Assert.NotEqual(string.Empty, q.RequestId));
    }

    [Fact]
    public void WhatIsOverTheLimitWaitsAndGoesOutInTheNextStepsInOrder()
    {
        var w = NewWorld(Settings(s => s.MaxRequestsPerStep = 1));
        w.SpawnAThingBesideEveryone();
        w.World.Step();

        // One request; every other question is open with its question mark and asks nobody yet.
        Assert.Equal(1, w.Requests);
        Assert.Equal(Places.Length, w.Decisions.Open.Count());
        Assert.Equal(Places.Length - 1, w.Decisions.Open.Count(q => q.RequestId.Length == 0));
        Assert.Equal(Places.Length, w.Events.Count(e => e.Kind == "question_opened"));

        var order = w.Decisions.Open.OrderBy(q => q.Id, StringComparer.Ordinal).Select(q => q.Id).ToList();
        var sent = new List<string> { w.Decisions.Open.Single(q => q.RequestId.Length > 0).Id };
        for (var step = 1; step < Places.Length; step++)
        {
            w.World.Step();
            Assert.Equal(step + 1, w.Requests);
            sent.Add(w.Decisions.Open.Where(q => q.RequestId.Length > 0).Select(q => q.Id).Except(sent).Single());
        }

        // Every question went out, one a step, in the order they were opened; none lapsed, none twice.
        Assert.Equal(order, sent);
        Assert.All(w.Decisions.Open, q => Assert.NotEqual(string.Empty, q.RequestId));
        Assert.DoesNotContain(w.Events, e => e.Kind == "question_failed");
        Assert.All(w.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).GroupBy(e => e.Tick), g => Assert.Single(g));
        Assert.Equal(w.Decisions.Open.Select(q => q.InhabitantId).Order(StringComparer.Ordinal), w.Inhabitants.All.Select(i => i.Id).Order(StringComparer.Ordinal));

        w.World.Run(10);
        Assert.Equal(Places.Length, w.Requests);
    }

    [Fact]
    public void AfterLoadingTheOpenQuestionsGoOutWithinTheLimit()
    {
        // A decider that never answers: what is asked stays out with it, and a load sends it all again.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, new SilentPort(), "de", "start_island");
        var limit = 2 * host.World.Calendar.TicksPerDay;
        while (host.World.Tick < limit && host.Decisions.Open.Count(q => q.RequestId.Length > 0) < 2)
        {
            host.World.Step();
        }

        Assert.True(host.Decisions.Open.Count(q => q.RequestId.Length > 0) >= 2, "the run needs at least two questions out with the decider");

        var state = StateCodec.Capture(host);
        var limited = Settings(s => s.MaxRequestsPerStep = 1);
        var restored = StateCodec.Restore(new SimulationHostFactory(Content, limited, new SilentPort(), "de"), state);
        // What the game does after loading (WorldPresenter): every question that was out goes again.
        restored.Decisions.ResendPendingAfterLoad(restored.World);
        var before = restored.Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request);
        restored.World.Step();
        Assert.Equal(before + 1, restored.Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request));
    }

    [Fact]
    public void TheSameSeedGivesTheSameHashesWithTheLimit()
    {
        static List<string> Run()
        {
            var w = NewWorld(Settings(s => s.MaxRequestsPerStep = 1));
            w.SpawnAThingBesideEveryone();
            var hashes = new List<string>();
            for (var t = 0; t < 30; t++)
            {
                w.World.Step();
                hashes.Add(w.World.StateHash());
            }

            return hashes;
        }

        Assert.Equal(Run(), Run());
    }

    private static DeciderSettings Settings(Action<DeciderSettings>? change = null)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        change?.Invoke(settings);
        return settings;
    }

    /// <summary>The start inhabitants far apart on an empty plain, with a decider that never answers.</summary>
    private static TestWorld NewWorld(DeciderSettings settings)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], new SilentPort(), Content.Inventions.Guard, "de");
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, Places);
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return new TestWorld(world, objects, inhabitants, decisions, events);
    }

    private sealed record TestWorld(World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions, List<WorldEvent> Events)
    {
        public int Requests => Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request);

        /// <summary>A thing nobody understands 10 m beside every inhabitant: each raises a question in the same step.</summary>
        public void SpawnAThingBesideEveryone()
        {
            foreach (var place in Places)
            {
                Objects.Spawn(Thing, new Position(place.X, place.Z + 10));
            }
        }
    }

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
