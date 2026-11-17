using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D711 section 5 (plan D1008 H1, review GW1 #24077, values TH147): things wear away by material class - fresh
/// food spoils in days, stone lasts; things of the world start have a seeded age so they do not all end at once.
/// </summary>
public sealed class DurabilityTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AnAppleSpoilsAfterTheHoursOfItsClassAndAStoneLasts()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");

        // After the first tick: things put into the running world, away from the inhabitants (nobody eats the apple).
        host.World.Run(1);
        var far = host.Inhabitants.All.First().Position;
        var apple = host.Objects.Spawn("apple", new Position(far.X + 40, far.Z));
        var stone = host.Objects.Spawn("fieldstone", new Position(far.X + 41, far.Z));
        var hours = Content.Value.Durability!.Classes.First(c => c.Id == "firm_fruit").BaseHours!.Value;
        var life = (long)hours * host.World.Calendar.Settings.TicksPerHour;

        // Wear is counted in permille and rain and heat age it faster - gone at the latest after its plain life.
        host.World.Run(life / 4);
        Assert.True(host.Objects.TryGet(apple.Id, out _));
        Assert.InRange(host.Durability!.WearPermille(apple, host.World.Tick)!.Value, 200, 999);
        host.World.Run(life);
        Assert.False(host.Objects.TryGet(apple.Id, out _));
        Assert.True(host.Objects.TryGet(stone.Id, out _));
        Assert.Null(host.Durability.WearPermille(stone, host.World.Tick));
    }

    [Fact]
    public void ThingsOfTheWorldStartHaveDifferentAgesSoTheyDoNotAllEndAtOnce()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");

        // Measured as if nothing grew in place (berries on their bushes do not wear at all).
        host.Durability!.GrowsInPlace = null;
        var fresh = host.Objects.All.Where(o => host.Durability!.WearPermille(o, 0) is not null
            && Content.Value.Objects.TrueProperties(o.TypeId).Contains("edible")
            && !Content.Value.Objects.TrueProperties(o.TypeId).Contains("long_lasting")).Take(20).ToList();
        Assert.True(fresh.Count >= 5, $"{fresh.Count} fresh things on the island");
        var wears = fresh.Select(o => host.Durability!.WearPermille(o, 0)!.Value).ToList();
        Assert.All(wears, w => Assert.InRange(w, 0, 999));
        Assert.True(wears.Distinct().Count() > wears.Count / 2, string.Join(",", wears));
        Assert.False(host.Durability!.HasState);
    }

    [Fact]
    public void APlacedThingSurvivesSavingAndLoadingWithTheSameHash()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(5);
        // Dry grass, away from the inhabitants (a berry might be eaten): a placed thing that wears.
        var berry = host.Objects.Spawn("dry_grass", new Position(host.Inhabitants.All.First().Position.X + 30, host.Inhabitants.All.First().Position.Z));
        host.World.Run(5);
        Assert.True(host.Durability!.HasState);

        var path = Path.Combine(Path.GetTempPath(), "gw-durability-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Durability.WearPermille(berry, host.World.Tick), loaded.Durability!.WearPermille(berry, loaded.World.Tick));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UseWearsAThingOutAndItsEndIsToldOnlyForMadeOrCarriedThings()
    {
        // A1576 (plan D1008 H2): each end of a use action wears its target by use_permille; at 1000 it breaks.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var who = host.Inhabitants.All.First();
        var at = new Position(who.Position.X + 30, who.Position.Z);
        var carried = host.Objects.Spawn("dry_grass", at);
        var lying = host.Objects.Spawn("dry_grass", new Position(at.X + 1, at.Z));
        host.Objects.SetHeld(carried.Id, who.Id);
        var material = Content.Value.Durability!.Classes.First(c => c.Id == "fibre");
        var uses = (int)Math.Ceiling(1000.0 / material.UsePermille!.Value);
        var events = new List<Inhabitants.WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        var before = host.Durability!.WearPermille(carried, host.World.Tick)!.Value;
        Use(host, carried.Id, 1);
        Assert.Equal(material.UsePermille.Value, host.Durability.ExtraWearOf(carried.Id));
        Assert.InRange(host.Durability.WearPermille(carried, host.World.Tick)!.Value, before + material.UsePermille.Value, before + material.UsePermille.Value + 1);
        Assert.Equal(0, host.Durability.ExtraWearOf(lying.Id)); // a thing nobody used stays as it was

        Use(host, carried.Id, uses);
        Use(host, lying.Id, uses);
        Assert.False(host.Objects.TryGet(carried.Id, out _));
        Assert.False(host.Objects.TryGet(lying.Id, out _));
        var told = Assert.Single(events, e => e.Kind == material.EndEvent);
        Assert.Equal((who.Id, carried.Id, "dry_grass"), (told.InhabitantId, told.TargetId, told.TargetType)); // the lying one ends in silence
    }

    [Fact]
    public void AThingOfAnyQualityEndsAtTheSameTickInARunningAndALoadedWorld()
    {
        // A1842 (review GW1 #600): the queue's due tick is a true lower bound at every quality, so where the queue began
        // (world start or load) does not move the end.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var near = host.Inhabitants.All.First().Position;
        var solid = Content.Value.Durability!.Quality!.OrderByDescending(q => q.LifePermille).First(q => q.LifePermille > 1000);
        string? Quality(string type) => type == "dry_wood" ? solid.Tier : null;
        var thing = host.Objects.Spawn("dry_wood", new Position(near.X + 40, near.Z));
        host.Durability!.QualityOf = Quality;
        host.Durability.AddWear(thing.Id, solid.LifePermille - 4);
        host.World.Step();
        Assert.True(host.Objects.TryGet(thing.Id, out _), "it ended at once");

        var path = Path.Combine(Path.GetTempPath(), "gw-durability-end-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            loaded.Durability!.QualityOf = Quality;
            Assert.Equal(EndOf(host, thing.Id), EndOf(loaded, thing.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static long EndOf(SimulationHost host, string id)
    {
        for (var i = 0; i < host.World.Calendar.TicksPerDay * 20 && host.Objects.TryGet(id, out _); i++)
        {
            host.World.Step();
        }

        Assert.False(host.Objects.TryGet(id, out _), "it did not end within 20 days");
        return host.World.Tick;
    }

    [Fact]
    public void AStormHourWearsWhatWasBuiltAndCalmWeatherDoesNot()
    {
        // A1382 S2 (D998, R24): each full storm hour wears every built thing by the data's permille; its quality
        // stretches the life as always (QualityStretchesTheLife...).
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var built = host.Objects.Spawn("dry_grass", new Position(host.Inhabitants.All.First().Position.X + 30, host.Inhabitants.All.First().Position.Z));
        host.Durability!.QualityOf = t => t == "dry_grass" ? "simple" : null;
        var perHour = Content.Value.Durability!.StormDamage!.PerHourPermille;
        var hour = host.World.Calendar.Settings.TicksPerHour;

        host.WeatherSystem!.Force("clear", host.World.Tick);
        RunToNextHour(host, hour);
        Assert.Equal(0, host.Durability.ExtraWearOf(built.Id));

        host.WeatherSystem.Force("storm", host.World.Tick);
        RunToNextHour(host, hour);
        Assert.Equal("storm", host.WeatherSystem.Current);
        Assert.Equal(perHour, host.Durability.ExtraWearOf(built.Id));
    }

    private static void RunToNextHour(SimulationHost host, int ticksPerHour)
    {
        do
        {
            host.World.Step();
        }
        while (host.World.Tick % ticksPerHour != 1);
    }

    [Fact]
    public void QualityStretchesTheLifeAndUsesSurviveSavingAndLoading()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var thing = host.Objects.Spawn("dry_grass", new Position(host.Inhabitants.All.First().Position.X + 30, host.Inhabitants.All.First().Position.Z));
        Use(host, thing.Id, 10);
        var plain = host.Durability!.WearPermille(thing, host.World.Tick)!.Value;
        var best = Content.Value.Durability!.Quality!.OrderByDescending(q => q.LifePermille).First();
        host.Durability.QualityOf = _ => best.Tier;
        Assert.InRange(host.Durability.WearPermille(thing, host.World.Tick)!.Value, (plain * 1000 / best.LifePermille) - 1, (plain * 1000 / best.LifePermille) + 1);
        host.Durability.QualityOf = null;

        var path = Path.Combine(Path.GetTempPath(), "gw-durability-uses-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Durability.ExtraWearOf(thing.Id), loaded.Durability!.ExtraWearOf(thing.Id));
            Assert.True(loaded.Durability.ExtraWearOf(thing.Id) > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DamageWearsEvenAThingThatTimeDoesNotWear()
    {
        // A1576 (review GW1 #26993 d): AddWear for damage (a storm on a building, A1382 S2) - a stone breaks only that way.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var stone = host.Objects.Spawn("fieldstone", new Position(host.Inhabitants.All.First().Position.X + 30, host.Inhabitants.All.First().Position.Z));
        Assert.Null(host.Durability!.WearPermille(stone, host.World.Tick));
        host.Durability.AddWear(stone.Id, 400);
        host.World.Run(1);
        Assert.Equal(400, host.Durability.WearPermille(stone, host.World.Tick));
        host.Durability.AddWear(stone.Id, 600);
        host.World.Run(1);
        Assert.False(host.Objects.TryGet(stone.Id, out _));
    }

    /// <summary>Ends a use action on the thing <paramref name="times"/> times, then one step (the use counts in it).</summary>
    private static void Use(SimulationHost host, string id, int times)
    {
        for (var i = 0; i < times; i++)
        {
            host.Durability!.Observe(new Inhabitants.WorldEvent(Inhabitants.InhabitantsSystem.ActionFinished, host.World.Tick, "inh-test", "use", id));
        }

        host.World.Run(1);
    }

    [Fact]
    public void AProcessedThingKeepsItsAgeAndHolderAndEndsAtTheSameTickAfterSavingAndLoading()
    {
        // GW2 #492: a processed thing (WorldObjects.ChangeType) left the world and came back - the running world forgot its
        // age, a loaded one started it anew, and they ended it at different ticks; the holder lost it from the hand too.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var who = host.Inhabitants.All.First();
        var apple = host.Objects.Spawn("apple", new Position(who.Position.X + 30, who.Position.Z));
        host.World.Run(30);
        host.Objects.ChangeType(apple.Id, "egg"); // firm fruit -> fresh food: another material class
        Assert.True(host.Objects.TryGet(apple.Id, out var changed));
        Assert.Equal("egg", changed.TypeId);
        host.World.Run(5);

        var path = Path.Combine(Path.GetTempPath(), "gw-durability-change-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.Durability!.WearPermille(changed, host.World.Tick), loaded.Durability!.WearPermille(changed, loaded.World.Tick));
            long? Gone(SimulationHost world)
            {
                for (var i = 0; i < 20_000; i++)
                {
                    if (!world.Objects.TryGet(apple.Id, out _))
                    {
                        return world.World.Tick;
                    }

                    world.World.Step();
                }

                return null;
            }

            var running = Gone(host);
            Assert.NotNull(running);
            Assert.Equal(running, Gone(loaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AProcessedThingStaysInTheHandOfWhoHoldsIt()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var who = host.Inhabitants.All.First();
        var apple = host.Objects.Spawn("apple", who.Position);
        host.Objects.SetHeld(apple.Id, who.Id);
        who.Held = apple.Id;
        host.Objects.ChangeType(apple.Id, "egg");
        Assert.Equal(apple.Id, who.Held);
        Assert.Equal(who.Id, host.Objects.HeldBy(apple.Id));
    }
}
