using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The indexes the systems keep beside their state so a tick need not walk everything (objects by type,
/// the types an inhabitant has a belief about, the open questions of each inhabitant): each must say what
/// a pass over the state itself says, at any time and after loading.
/// </summary>
public sealed class IndexTests : IDisposable
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public IndexTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void AssertObjectsByType(WorldObjects objects)
    {
        var expected = objects.All.GroupBy(o => o.TypeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var filled = objects.ByType.Where(t => t.Objects.Count > 0).ToList();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), filled.Select(t => t.TypeId));
        Assert.All(filled, t => Assert.Equal(expected[t.TypeId], t.Objects));
        Assert.Equal(objects.ByType.Select(t => t.TypeId).Order(StringComparer.Ordinal), objects.ByType.Select(t => t.TypeId));
        Assert.Equal(objects.All.Select(o => o.Id).Order(StringComparer.Ordinal), objects.All.Select(o => o.Id));
    }

    [Fact]
    public void ObjectsByTypeFollowSpawningRemovingAndRestoring()
    {
        var objects = new WorldObjects(Content.Objects);
        Assert.Empty(objects.ByType);
        var apple = objects.Spawn("apple", new Position(1, 1));
        var flint = objects.Spawn("flint", new Position(2, 2));
        var second = objects.Spawn("apple", new Position(3, 3));
        AssertObjectsByType(objects);
        Assert.Equal(["apple", "flint"], objects.ByType.Select(t => t.TypeId));
        Assert.Equal([apple, second], objects.ByType[0].Objects);

        Assert.True(objects.Remove(apple.Id));
        Assert.False(objects.Remove(apple.Id));
        Assert.False(objects.TryGet(apple.Id, out _));
        AssertObjectsByType(objects);
        Assert.True(objects.Remove(flint.Id));

        // A type whose objects are all gone stays listed, empty.
        Assert.Equal(["apple", "flint"], objects.ByType.Select(t => t.TypeId));
        Assert.Empty(objects.ByType[1].Objects);

        // Restoring (save games) may come in any order; the lists stay in id order.
        objects.Restore(new WorldObject("obj-000009", "apple", new Position(9, 9)), 10);
        objects.Restore(new WorldObject("obj-000001", "apple", new Position(1, 1)), 10);
        objects.Restore(new WorldObject("obj-000005", "clay", new Position(5, 5)), 10);
        AssertObjectsByType(objects);
        Assert.Equal(["obj-000001", "obj-000003", "obj-000009"], objects.ByType[0].Objects.Select(o => o.Id));
        Assert.Equal(["obj-000001", "obj-000003", "obj-000005", "obj-000009"], objects.All.Select(o => o.Id));
        Assert.Equal(4, objects.Count);
    }

    [Fact]
    public void KnowingATypeIsHavingABeliefAboutIt()
    {
        var beliefs = new BeliefSet();
        Assert.False(beliefs.Knows("apple"));
        beliefs.Set("apple", "edible", value: false, confidence: 0.1);
        Assert.True(beliefs.Knows("apple"));
        Assert.False(beliefs.Knows("flint"));
        Assert.Equal(beliefs.About("flint").Any(), beliefs.Knows("flint"));
    }

    [Fact]
    public void BeliefsAreFoundByKeyByTypeAndInOrder()
    {
        var beliefs = new BeliefSet();
        beliefs.Set("flint", "sharp", true, 0.4);
        beliefs.Set("apple", "poisonous", false, 0.9);
        beliefs.Set("apple", "edible", true, 0.3);
        beliefs.Set("apple", "fruit", true, 0.8, KnowledgeSources.Seen);
        beliefs.Set("apple", "edible", true, 0.5, KnowledgeSources.Heard, "inh-000002", 7);
        AssertBeliefs(beliefs);
        Assert.Equal(4, beliefs.Count);
        Assert.Equal(["edible", "fruit", "poisonous"], beliefs.About("apple").Select(b => b.PropertyId));
        Assert.True(beliefs.TryGet("apple", "edible", out var edible));
        Assert.Equal(new Belief(true, 0.5, KnowledgeSources.Heard, "inh-000002", 7), edible);
        Assert.Equal(0.9, beliefs.Surest("apple"));
        Assert.Equal(0.0, beliefs.Surest("clay"));
        Assert.Empty(beliefs.About("clay"));
        Assert.False(beliefs.TryGet("clay", "edible", out _));
    }

    private static void AssertBeliefs(BeliefSet beliefs)
    {
        var all = beliefs.All.ToList();
        Assert.Equal(beliefs.Count, all.Count);
        Assert.Equal(all.OrderBy(b => b.TypeId, StringComparer.Ordinal).ThenBy(b => b.PropertyId, StringComparer.Ordinal), all);
        foreach (var (typeId, propertyId, belief) in all)
        {
            Assert.True(beliefs.TryGet(typeId, propertyId, out var found));
            Assert.Equal(belief, found);
        }

        foreach (var type in all.Select(b => b.TypeId).Distinct(StringComparer.Ordinal))
        {
            Assert.Equal(all.Where(b => b.TypeId == type).Select(b => (b.PropertyId, b.Belief)), beliefs.About(type));
            Assert.Equal(all.Where(b => b.TypeId == type).Max(b => b.Belief.Confidence), beliefs.Surest(type));
            Assert.True(beliefs.Knows(type));
        }
    }

    [Fact]
    public void AnIdSetIsReadInOrderAndSaysWhenItChanged()
    {
        var ids = new IdSet();
        Assert.Empty(ids);
        Assert.True(ids.Add("inh-000007"));
        Assert.True(ids.Add("inh-000002"));
        Assert.False(ids.Add("inh-000007"));
        Assert.Equal(["inh-000002", "inh-000007"], ids);
        Assert.Equal(2, ids.Count);
        Assert.True(ids.Contains("inh-000002"));
        Assert.False(ids.Contains("inh-000003"));

        // The version moves with every change and only with a change.
        var version = ids.Version;
        Assert.False(ids.Add("inh-000002"));
        Assert.False(ids.Remove("inh-000003"));
        ids.UnionWith(["inh-000002", "inh-000007"]);
        Assert.Equal(version, ids.Version);
        ids.UnionWith(["inh-000001", "inh-000002"]);
        Assert.NotEqual(version, ids.Version);
        version = ids.Version;
        Assert.True(ids.Remove("inh-000002"));
        Assert.NotEqual(version, ids.Version);
        Assert.Equal(["inh-000001", "inh-000007"], ids);
        Assert.False(ids.Contains("inh-000002"));
    }

    [Fact]
    public void WhoHasMetWhomFollowsTheSetAlsoWhenItIsChangedFromOutside()
    {
        // Everybody in sight of each other: after the first tick all have met. Forgetting someone (as a test
        // or a later rule may do) is seen by the meeting pass at once: they meet again, without a first meeting.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new SilentPort(), "de", Preset, inhabitants: 12);
        host.World.Run(3);
        var everyone = host.Inhabitants.All.ToList();
        var a = everyone[0];
        var near = everyone.Where(o => o != a && a.Met.Contains(o.Id)).ToList();
        Assert.True(near.Count > 1);
        Assert.All(near, o => Assert.Contains(a.Id, o.Met));

        var forgotten = near[^1];
        Assert.True(a.Met.Remove(forgotten.Id));
        var events = host.Inhabitants.LastEventSeq;
        host.World.Step();
        Assert.Contains(forgotten.Id, a.Met);
        Assert.DoesNotContain(host.Inhabitants.EventsSince(events), e => e.Event.Kind == Inhabitants.InhabitantsSystem.FirstMeeting);

        // Both forget each other: that is a first meeting again.
        Assert.True(a.Met.Remove(forgotten.Id));
        Assert.True(forgotten.Met.Remove(a.Id));
        events = host.Inhabitants.LastEventSeq;
        host.World.Step();
        Assert.Contains(forgotten.Id, a.Met);
        Assert.Contains(a.Id, forgotten.Met);
        Assert.Single(host.Inhabitants.EventsSince(events), e => e.Event.Kind == Inhabitants.InhabitantsSystem.FirstMeeting);
    }

    [Fact]
    public void RelationshipsStayInIdOrderAndFadeTowardsTheirTargets()
    {
        var start = new Relation(0.3, 0.5, 0.5);
        var relations = new RelationshipSet();
        Assert.Equal(start, relations.Get("inh-000002", start));
        relations.Set("inh-000005", new Relation(0.6, 0.7, 0.5));
        relations.Set("inh-000002", new Relation(1.4, -0.2, 0.3));
        relations.Set("inh-000009", new Relation(0.1, 0.2, 0.3));
        relations.Set("inh-000005", new Relation(0.61, 0.7, 0.5));
        Assert.Equal(3, relations.Count);
        Assert.Equal(["inh-000002", "inh-000005", "inh-000009"], relations.All.Select(r => r.Other));
        Assert.Equal(new Relation(1.0, 0.0, 0.3), relations.Get("inh-000002", start));
        Assert.Equal(new Relation(0.61, 0.7, 0.5), relations.Get("inh-000005", start));

        // Every value above its target sinks by the step, never below the target; values at or below stay.
        // Towards someone who does not count nothing fades.
        relations.Fade(start, 0.25, other => other != "inh-000005");
        Assert.Equal(new Relation(0.75, 0.0, 0.3), relations.Get("inh-000002", start));
        Assert.Equal(new Relation(0.61, 0.7, 0.5), relations.Get("inh-000005", start));
        Assert.Equal(new Relation(0.1, 0.2, 0.3), relations.Get("inh-000009", start));
        relations.Fade(start, 0.25, _ => true);
        relations.Fade(start, 0.25, _ => true);
        Assert.Equal(new Relation(0.3, 0.0, 0.3), relations.Get("inh-000002", start));
        Assert.Equal(new Relation(0.3, 0.5, 0.5), relations.Get("inh-000005", start));

        Assert.True(relations.Remove("inh-000005"));
        Assert.False(relations.Remove("inh-000005"));
        Assert.Equal(["inh-000002", "inh-000009"], relations.All.Select(r => r.Other));
        Assert.Equal(start, relations.Get("inh-000005", start));
        Assert.Throws<ArgumentException>(() => relations.Set(string.Empty, start));
    }

    [Fact]
    public void TheBestCandidateIsTheFirstOfTheSortedOptions()
    {
        // A world that has lived a while: beliefs, needs and things in sight differ from inhabitant to inhabitant.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, inhabitants: 12);
        host.World.Run(400);
        var brain = host.Content.Brain;
        var scratch = new Behaviour.UtilityBrain.Scratch();
        var none = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var perceived = host.Inhabitants.InAim(inhabitant);
            var others = host.Inhabitants.All.Where(o => o != inhabitant).ToList();
            var sorted = brain.Score(inhabitant, perceived, none, null, others);
            var candidates = brain.Candidates(inhabitant, perceived, none, null, others, scratch);
            Assert.Equal(sorted.Count, candidates.Count);
            Assert.NotEmpty(sorted);

            // The sorted list is the candidates in the order of Compare, which never calls two of them equal.
            for (var i = 1; i < sorted.Count; i++)
            {
                Assert.True(sorted[i - 1].Score >= sorted[i].Score);
            }

            var best = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                Assert.NotEqual(0, Behaviour.UtilityBrain.Compare(candidates[i], i, candidates[best], best));
                best = Behaviour.UtilityBrain.Compare(candidates[i], i, candidates[best], best) < 0 ? i : best;
            }

            Assert.Equal((sorted[0].Action, sorted[0].TargetId, sorted[0].Score, sorted[0].SearchFor), (candidates[best].Action, candidates[best].TargetId, candidates[best].Score, candidates[best].SearchFor));
            Assert.Equal(
                sorted.Select(o => (o.Action, o.TargetId, o.Score)).Order(),
                candidates.Select(o => (o.Action, o.TargetId, o.Score)).Order());
        }
    }

    [Fact]
    public void TheIndexesAgreeWithTheStateDuringARunAndAfterLoading()
    {
        // Twenty inhabitants and a decider that answers: questions open and close, things are eaten and grow back.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, inhabitants: 20);
        for (var step = 0; step < 12; step++)
        {
            host.World.Run(40);
            AssertIndexes(host);
        }

        // A silent decider leaves every question open: everybody runs out of room for another one.
        var waiting = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new SilentPort(), "de", Preset, inhabitants: 20);
        waiting.World.Run(200);
        AssertIndexes(waiting);
        Assert.Contains(waiting.Inhabitants.All, i => waiting.Decisions.OpenQuestionIds(i.Id).Count == waiting.DeciderSettings.MaxOpenQuestions);

        var path = Path.Combine(_directory, "indexes.db");
        SaveGameStore.Save(path, StateCodec.Capture(waiting));
        var loaded = StateCodec.Restore(new SimulationHostFactory(waiting.Content, waiting.DeciderSettings, new SilentPort(), "de"), SaveGameStore.Load(path));
        Assert.Equal(waiting.World.StateHash(), loaded.World.StateHash());
        AssertIndexes(loaded);
        waiting.World.Run(120);
        loaded.World.Run(120);
        Assert.Equal(waiting.World.StateHash(), loaded.World.StateHash());
        AssertIndexes(loaded);
    }

    [Fact]
    public void ForgettingWhatIsWorkedOutFromTheStateChangesNothing()
    {
        // The decisions system keeps the open questions per inhabitant and who asked about whom beside its
        // state. One world throws that away before every tick and builds it anew from the state (as a load
        // does), the other never: if anything were kept that the state does not say, they would part.
        var kept = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, inhabitants: 24);
        var forgetful = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, inhabitants: 24);
        for (var tick = 1; tick <= 600; tick++)
        {
            forgetful.Decisions.ForgetDerived();
            kept.World.Step();
            forgetful.World.Step();
            if (tick % 50 == 0)
            {
                Assert.True(kept.World.StateHash() == forgetful.World.StateHash(), $"the worlds part by tick {tick}");
            }
        }

        // The run asked who others are, again and again, and questions stayed open: both tables were in use.
        var asked = kept.Decisions.CaptureState().LastAsked;
        Assert.Contains(asked, a => a.Key.StartsWith("asks|", StringComparison.Ordinal) && a.Value > 1);
        Assert.Contains(asked, a => !a.Key.StartsWith("asks|", StringComparison.Ordinal) && a.Key.Contains("|type:", StringComparison.Ordinal));
        AssertIndexes(kept);
        AssertIndexes(forgetful);

        // The same with a decider that never answers: everybody runs out of room and stays there.
        var waiting = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new SilentPort(), "de", Preset, inhabitants: 24);
        var waitingForgetful = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new SilentPort(), "de", Preset, inhabitants: 24);
        for (var tick = 1; tick <= 200; tick++)
        {
            waitingForgetful.Decisions.ForgetDerived();
            waiting.World.Step();
            waitingForgetful.World.Step();
        }

        Assert.Equal(waiting.World.StateHash(), waitingForgetful.World.StateHash());
    }

    private static void AssertIndexes(SimulationHost host)
    {
        AssertObjectsByType(host.Objects);
        var types = host.Objects.ByType.Select(t => t.TypeId).ToList();
        var open = host.Decisions.Open.ToList();
        foreach (var inhabitant in host.Inhabitants.All)
        {
            Assert.All(types, type => Assert.Equal(inhabitant.Beliefs.About(type).Any(), inhabitant.Beliefs.Knows(type)));
            AssertBeliefs(inhabitant.Beliefs);
            Assert.All(types, type => Assert.Equal(
                open.Any(q => q.InhabitantId == inhabitant.Id && q.Subject == "type:" + type),
                host.Decisions.HasOpenQuestion(inhabitant.Id, type)));
            Assert.Equal(open.Where(q => q.InhabitantId == inhabitant.Id).Select(q => q.Id), host.Decisions.OpenQuestionIds(inhabitant.Id));
        }
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
