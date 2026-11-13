using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1379 (TL #23264, review GW1 #23273): a fire can be fed with fuel and burns longer, up to its most; a
/// spark takes none; a fire's end is seen by those near; the request says how warm it feels, from the same
/// felt temperature the warmth need uses.
/// </summary>
public sealed class FuelTests
{
    private const string AddFuel = "add_fuel";
    private const string Actor = "inh-000001";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void FuelPutIntoAFireMakesItBurnLongerAndIsUsedUp()
    {
        var (host, _) = World();
        var fire = host.Objects.Spawn("fire", new Position(1, 0));
        var wood = host.Objects.Spawn("dry_wood", new Position(1.5, 0));
        host.World.Run(1);
        var before = host.Fire.Burning.Single().Value;

        RunFeed(host, fire, wood);
        var minutes = Content.Value.Objects.Params("dry_wood").FuelMinutes!.Value;
        Assert.Equal(before + ((long)minutes * host.World.Calendar.Settings.TicksPerHour / 60), host.Fire.Burning.Single().Value);
        Assert.False(host.Objects.TryGet(wood.Id, out _));
    }

    [Fact]
    public void FeedingNeverMakesAFireBurnLongerThanItsMost()
    {
        var (host, _) = World();
        var fire = host.Objects.Spawn("fire", new Position(1, 0));
        host.World.Run(1);
        var most = (long)Content.Value.Objects.Params("fire").BurnMaxMinutes!.Value * host.World.Calendar.Settings.TicksPerHour / 60;
        for (var i = 0; i < 6; i++)
        {
            var peat = host.Objects.Spawn("peat", new Position(1.2, 0));
            Assert.True(host.Fire.Feed(host.World, fire.Id, peat.Id));
        }

        Assert.Equal(host.World.Tick + most, host.Fire.Burning.Single().Value);
    }

    [Fact]
    public void WhatIsNoFuelChangesNothingAndASparkTakesNone()
    {
        var (host, _) = World();
        var fire = host.Objects.Spawn("fire", new Position(1, 0));
        var spark = host.Objects.Spawn("spark", new Position(3, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(3.5, 0));
        host.World.Run(1);
        var before = host.Fire.Burning.ToDictionary(b => b.Key, b => b.Value);

        Assert.False(host.Fire.Feed(host.World, fire.Id, flint.Id));
        Assert.True(host.Objects.TryGet(flint.Id, out _));
        Assert.True(host.Fire.TakesFuel(fire));
        Assert.False(host.Fire.TakesFuel(spark));
        Assert.False(host.Fire.Feed(host.World, spark.Id, grass.Id));
        Assert.True(host.Objects.TryGet(grass.Id, out _));
        Assert.Equal(before, host.Fire.Burning.ToDictionary(b => b.Key, b => b.Value));
    }

    [Fact]
    public void WhoIsNearSeesAFireBurnDown()
    {
        var (host, events) = World();
        host.Objects.Spawn("fire", new Position(2, 0));
        var minutes = Content.Value.Objects.Params("fire").BurnMinutes!.Value;
        host.World.Run(((long)minutes * host.World.Calendar.Settings.TicksPerHour / 60) + 2);

        var seen = Assert.Single(events, e => e.Kind == FireSystem.BurnedOut);
        Assert.Equal(Actor, seen.InhabitantId);
        Assert.Equal("fire", seen.TargetType);
    }

    [Fact]
    public void ItFeelsWarmerNextToAFire()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var inhabitant = host.Inhabitants.All.First();
        var levels = Content.Value.Vocabularies[Contracts.VocabularyKind.Comfort_level].Entries.Select(e => e.Id).ToList();
        var without = host.Inhabitants.ComfortLevelOf(host.World, inhabitant);
        Assert.NotNull(without);

        for (var i = 0; i < 3; i++)
        {
            host.Objects.Spawn("fire", inhabitant.Position);
        }

        var with = host.Inhabitants.ComfortLevelOf(host.World, inhabitant);
        Assert.True(levels.IndexOf(with!) > levels.IndexOf(without!) || with == levels[^1], $"{without} -> {with}");
    }

    private static (SimulationHost Host, List<WorldEvent> Events) World()
    {
        // A decider that never answers: no answer replaces the try set by the test.
        var host = SimulationHost.CreateEmpty(ContentDirectory, 19, new SilentPort(), "de");
        var model = Content.Value.InhabitantModel;
        var needs = model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        var moods = model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant(Actor, "female", 1, new Position(0, 0), needs, moods, traits, model.WillToLive.Start));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return (host, events);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private static void RunFeed(SimulationHost host, WorldObject fire, WorldObject fuel)
    {
        var inhabitant = host.Inhabitants.Get(Actor);
        inhabitant.Activity = new Activity(AddFuel, fire.Id, fire.Position, null, Partners: [fuel.Id]);
        for (var i = 0; i < 2000 && inhabitant.Activity is { Action: AddFuel }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: AddFuel }, "the feeding did not end");
    }
}
