using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2687 K1 (A1451 part 1, plan D932): using things together in the core. The combination rules of the
/// data decide what comes of it by the things' TRUE properties; the core offers a try by reach only (R39),
/// checks the partner at the start and at the end, and the actor learns what it made.
/// </summary>
public sealed class CombineTests
{
    private const string Combine = "combine";
    private const string Actor = "inh-000001";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void PyriteOnFlintGivesASparkAndASparkOnDryGrassAFire()
    {
        var (host, events) = World();
        var pyrite = host.Objects.Spawn("pyrite", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(2, 0));

        RunCombine(host, pyrite, flint);
        var spark = Assert.Single(host.Objects.All, o => o.TypeId == "spark");
        Assert.True(host.Objects.TryGet(pyrite.Id, out _));
        Assert.True(host.Objects.TryGet(flint.Id, out _));
        var made = Assert.Single(events, e => e.Kind == InhabitantsSystem.Combined);
        Assert.Equal(spark.Id, made.TargetId);
        Assert.Equal("spark_from_pyrite_and_flint", made.Detail);
        Assert.True(host.Inhabitants.Get(Actor).Beliefs.TryGet("spark", "hot", out var hot) && hot.Value);

        RunCombine(host, spark, grass);
        Assert.Single(host.Objects.All, o => o.TypeId == "fire");
        Assert.False(host.Objects.TryGet(spark.Id, out _));
        Assert.False(host.Objects.TryGet(grass.Id, out _));
        Assert.Equal(2, events.Count(e => e.Kind == InhabitantsSystem.Combined));
    }

    [Fact]
    public void TheOrderOfTargetAndPartnerDoesNotMatter()
    {
        var (host, _) = World();
        var pyrite = host.Objects.Spawn("pyrite", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));

        // The flint as the target and the pyrite as its partner: the rule's slots are matched by properties.
        RunCombine(host, flint, pyrite);
        Assert.Single(host.Objects.All, o => o.TypeId == "spark");
    }

    [Fact]
    public void ThingsThatFitNoRuleGiveNothingAndUseNothingUp()
    {
        var (host, events) = World();
        var flint = host.Objects.Spawn("flint", new Position(1, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.5, 0));
        var before = host.Objects.All.Count();

        RunCombine(host, flint, grass);
        Assert.Equal(before, host.Objects.All.Count());
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.Combined);
        var finished = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == Combine);
        Assert.True(ActionOutcomes.WithoutEffect(finished.Outcome!));
    }

    [Fact]
    public void APartnerThatIsGoneBeforeTheEndCancelsTheTryWithoutUsingAnythingUp()
    {
        var (host, events) = World();
        var spark = host.Objects.Spawn("spark", new Position(1, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.5, 0));
        Start(host, spark, grass);

        // Started, then the grass is taken away while the inhabitant is at it.
        for (var i = 0; i < 200 && !events.Any(e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == Combine); i++)
        {
            host.World.Run(1);
        }

        host.Objects.Remove(grass.Id);

        // Only until the try ends: a spark burns down after its minutes (K3), which is not what this test is about.
        for (var i = 0; i < 2000 && host.Inhabitants.Get(Actor).Activity is { Action: Combine }; i++)
        {
            host.World.Run(1);
        }

        var cancelled = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.Action == Combine);
        Assert.Equal(InhabitantsSystem.PartnerGone, cancelled.Detail);
        Assert.True(host.Objects.TryGet(spark.Id, out _));
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == "fire");
    }

    [Fact]
    public void APartnerOutOfReachOfTheTargetIsRefusedAtTheStart()
    {
        var (host, events) = World();
        var pyrite = host.Objects.Spawn("pyrite", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(9, 0));

        RunCombine(host, pyrite, flint);
        Assert.Equal(InhabitantsSystem.PartnerGone, Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionCancelled).Detail);
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == "spark");
    }

    [Fact]
    public void TheDeciderIsOfferedEveryThingNearbyNotOnlyThoseThatFit()
    {
        // A decider that never answers: the free moments keep coming and nothing is used up meanwhile.
        var port = new RecordingPort(answers: false);
        var (host, _) = World(port);
        var pyrite = host.Objects.Spawn("pyrite", new Position(2, 0));
        host.Objects.Spawn("flint", new Position(3.5, 0));
        host.Objects.Spawn("dry_grass", new Position(2.5, 0));
        host.Objects.Spawn("flint", new Position(30, 0));

        // known_only (R39, GW2 D979): the inhabitant believes something about these kinds of things.
        var beliefs = host.Inhabitants.Get(Actor).Beliefs;
        beliefs.Set("pyrite", "hard", true, 0.8);
        beliefs.Set("flint", "hard", true, 0.8);
        beliefs.Set("dry_grass", "burnable", true, 0.8);

        host.World.Run(host.World.Calendar.TicksPerDay);
        var offers = port.Requests
            .SelectMany(r => r.Options.Where(o => o.Action == Combine).Select(o => (Request: r, Option: o)))
            .ToList();
        Assert.NotEmpty(offers);
        foreach (var (_, option) in offers)
        {
            Assert.NotEqual(option.Target, Assert.Single(option.Partners));
        }

        // Per question and target: only partners within reach (the nearest are chosen; their order in the request is
        // shuffled, D1025); the far flint never.
        var positions = host.Objects.All.ToDictionary(o => o.Id, o => o.Position, StringComparer.Ordinal);
        foreach (var group in offers.GroupBy(o => (o.Request.RequestId, o.Option.Target)))
        {
            var target = positions[group.Key.Target!];
            var distances = group.Select(o => Distance(target, positions[o.Option.Partners.Single()])).ToList();
            Assert.All(distances, d => Assert.True(d <= 2.0));
        }

        // Offered by reach, not by truth (R39): a pair that fits no rule is offered as well.
        Assert.Contains(offers, o => TypeOf(o.Option.Target!) != "pyrite" && TypeOf(o.Option.Partners.Single()) != "pyrite");
        Assert.Contains(offers, o => o.Option.Target == pyrite.Id || o.Option.Partners.Contains(pyrite.Id));

        string TypeOf(string id) => host.Objects.All.Single(o => o.Id == id).TypeId;
        static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
    }

    [Fact]
    public void ThingsTheInhabitantKnowsNothingAboutAreNotOfferedTogether()
    {
        // known_only (R39: the inhabitant can come to it from what it knows; GW2 D979): no belief, no try.
        // A decider that never answers, so the inhabitant learns nothing about the flint meanwhile.
        var port = new RecordingPort(answers: false);
        var (host, _) = World(port);
        var pyrite = host.Objects.Spawn("pyrite", new Position(2, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(2.5, 0));
        host.Objects.Spawn("flint", new Position(3, 0));
        host.Inhabitants.Get(Actor).Beliefs.Set("pyrite", "hard", true, 0.8);
        host.Inhabitants.Get(Actor).Beliefs.Set("dry_grass", "burnable", true, 0.8);

        host.World.Run(host.World.Calendar.TicksPerDay / 4);
        var offers = port.Requests.SelectMany(r => r.Options.Where(o => o.Action == Combine)).ToList();
        Assert.NotEmpty(port.Requests);
        Assert.All(offers, o => Assert.Contains(o.Target, new[] { pyrite.Id, grass.Id }));
        Assert.All(offers, o => Assert.Contains(Assert.Single(o.Partners), new[] { pyrite.Id, grass.Id }));
    }

    [Fact]
    public void AFreeMomentOffersKnownThingsTogether()
    {
        // TL TH142 / GW2 D979: without a subject, pairs of known things near the asker. (An unknown thing would
        // open a question of its own first; that unknown things are never offered is ThingsTheInhabitantKnowsNothingAbout...)
        var port = new RecordingPort(answers: false);
        var (host, _) = World(port);
        var pyrite = host.Objects.Spawn("pyrite", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));
        var beliefs = host.Inhabitants.Get(Actor).Beliefs;
        beliefs.Set("pyrite", "hard", true, 0.8);
        beliefs.Set("flint", "hard", true, 0.8);

        host.World.Run(host.World.Calendar.TicksPerDay);
        var offers = port.Requests.Where(r => r.Occasion == "leisure").SelectMany(r => r.Options.Where(o => o.Action == Combine)).ToList();
        Assert.NotEmpty(offers);
        Assert.Contains(offers, o => (o.Target == pyrite.Id && o.Partners.Contains(flint.Id)) || (o.Target == flint.Id && o.Partners.Contains(pyrite.Id)));
    }

    [Fact]
    public void AnActivityWithAPartnerSurvivesSavingAndLoadingWithTheSameHash()
    {
        var (host, _) = World();
        var pyrite = host.Objects.Spawn("pyrite", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));
        Start(host, pyrite, flint);
        host.World.Run(3);
        Assert.Equal([flint.Id], host.Inhabitants.Get(Actor).Activity!.Partners);

        var path = Path.Combine(Path.GetTempPath(), "gw-combine-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal([flint.Id], loaded.Inhabitants.Get(Actor).Activity!.Partners);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ActivitiesWithoutPartnersKeepTheirHash()
    {
        var plain = new Activity("eat", "obj-000001", new Position(1, 0), 5, DecidedTick: 3);
        var empty = plain with { Partners = [] };
        Assert.Equal(Hash(plain), Hash(empty));
        Assert.NotEqual(Hash(plain), Hash(plain with { Partners = ["obj-000002"] }));

        static string Hash(Activity activity)
        {
            var (host, _) = World();
            host.Inhabitants.Get(Actor).Activity = activity;
            return host.World.StateHash();
        }
    }

    private static (SimulationHost Host, List<WorldEvent> Events) World(IDecisionPort? decisions = null)
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 11, decisions ?? new MockDecisionPort(), "de");
        var model = Content.Value.InhabitantModel;
        var needs = model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        var moods = model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant(Actor, "female", 1, new Position(0, 0), needs, moods, traits, model.WillToLive.Start));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return (host, events);
    }

    private static void Start(SimulationHost host, WorldObject target, WorldObject partner) =>
        host.Inhabitants.Get(Actor).Activity = new Activity(Combine, target.Id, target.Position, null, Partners: [partner.Id]);

    private static void RunCombine(SimulationHost host, WorldObject target, WorldObject partner)
    {
        Start(host, target, partner);
        var inhabitant = host.Inhabitants.Get(Actor);
        for (var i = 0; i < 2000 && inhabitant.Activity is { Action: Combine }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: Combine }, "the try did not end");
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }

    private sealed class RecordingPort(bool answers = true) : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();

        public List<DecisionRequest> Requests { get; } = [];

        public void Send(string requestId, string requestJson)
        {
            Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));
            _mock.Send(requestId, requestJson);
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => answers ? _mock.Receive(tick) : [];
    }
}
