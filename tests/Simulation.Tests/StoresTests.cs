using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1816 first step (D1203 v3 layer 2, E10; A1799 D1236): the body's stores behind the switch body.stores.enabled -
/// stamina by gait and load, rest and sleep up to a cap from the sleep need and health, health by the events of the data,
/// the carrying strength per life phase; saved and hashed, and not in the world while the switch is off.
/// </summary>
public sealed class StoresTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void WalkingUsesStaminaByTheGaitOfTheWalkAndMoreWithALoad()
    {
        var (world, objects, inhabitants, stores, content) = Bare();
        var free = inhabitants.Get("inh-000001");
        var loaded = inhabitants.Get("inh-000002");
        foreach (var walker in new[] { free, loaded })
        {
            walker.Activity = new Activity("explore", null, new Position(10_000, 0), null);
        }

        // The heaviest thing of the catalog it can still lift: a load share near 1.
        var carry = stores.CarryKg(loaded);
        var heavy = content.Objects.TypeIds
            .Where(t => content.Objects.Params(t).WeightKg is { } kg && kg <= carry)
            .OrderByDescending(t => content.Objects.Params(t).WeightKg)
            .ThenBy(t => t, StringComparer.Ordinal)
            .First();
        loaded.Held = objects.Spawn(heavy, loaded.Position).Id;
        Assert.InRange(stores.LoadShare(loaded), 0.01, 1.0);
        Assert.Equal(0, stores.LoadShare(free));

        world.Run(world.Calendar.Settings.TicksPerHour);

        var rule = content.Body!.Stores!.Stamina;
        var gait = content.Gaits.GaitFor(free, "explore");
        Assert.Equal(1 - ((rule.GaitPerHour.TryGetValue(gait, out var rate) ? rate : 0) / 1000.0), stores.Stamina(free.Id), 9);
        Assert.True(stores.Stamina(loaded.Id) < stores.Stamina(free.Id), $"{stores.Stamina(loaded.Id):0.000} vs {stores.Stamina(free.Id):0.000}");
    }

    [Fact]
    public void RestBringsStaminaBackFasterAsleepButOnlyUpToTheCapOfTheSleepNeed()
    {
        var (world, _, inhabitants, stores, content) = Bare();
        var awake = inhabitants.Get("inh-000001");
        var asleep = inhabitants.Get("inh-000002");
        awake.Activity = new Activity("idle", null, awake.Position, 100_000);
        asleep.Activity = new Activity(content.InhabitantModel.Togetherness!.SleepAction, null, asleep.Position, 100_000);
        Assert.True(inhabitants.IsAsleep(asleep));
        stores.Restore([new StoredBody(awake.Id, 100, 1000), new StoredBody(asleep.Id, 100, 1000)]);

        world.Run(world.Calendar.Settings.TicksPerHour / 4);
        Assert.True(stores.Stamina(asleep.Id) > stores.Stamina(awake.Id), $"{stores.Stamina(asleep.Id):0.000} vs {stores.Stamina(awake.Id):0.000}");

        // Sleepy: the cap is 1000 - 500 x (1 - 0.2) = 600 - it recovers to that and no further.
        awake.Needs[content.Body!.Stores!.Stamina.SleepNeed] = 0.2;
        Assert.Equal(600, stores.Cap(awake));
        world.Run(world.Calendar.Settings.TicksPerHour * 3);
        Assert.Equal(0.6, stores.Stamina(awake.Id), 9);
    }

    [Fact]
    public void AFallCostsHealthByTheDataAndLowersTheCapOfStamina()
    {
        var (world, _, inhabitants, stores, content) = Bare();
        var faller = inhabitants.Get("inh-000001");
        var change = content.Body!.Stores!.Health.Events!["slipped"];

        inhabitants.Report(new WorldEvent(BodySystem.Slipped, world.Tick, faller.Id, null, null));

        Assert.Equal(1 + (change / 1000.0), stores.Health(faller.Id), 9);
        Assert.Equal(1000 + (content.Body.Stores.Stamina.CapHealthPermille * change / 1000), stores.Cap(faller));
        Assert.Equal(1.0, stores.Health("inh-000002"));
    }

    [Fact]
    public void TheCarryingStrengthIsTheDataOfTheLifePhase()
    {
        var (_, _, inhabitants, stores, content) = Bare();
        var who = inhabitants.Get("inh-000001");
        var data = content.Body!.Stores!;
        Assert.Equal(data.DefaultCarryKg, stores.CarryKg(who));
        foreach (var (phase, kg) in data.CarryKg)
        {
            who.LifeStage = phase;
            Assert.Equal(kg, stores.CarryKg(who));
        }
    }

    [Fact]
    public void SwitchedOffTheStoresAreNotInTheWorldSwitchedOnTheySurviveSavingAndLoading()
    {
        var off = SimulationHost.CreateEmpty(ContentDirectory, 5, new MockDecisionPort(), "de");
        Assert.False(off.Content.Body!.Stores!.Enabled);
        Assert.Null(off.Stores);

        var content = On();
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var host = SimulationHost.Assemble(content, settings, new World(5, content.Calendar), new MockDecisionPort(), "de");
        host.Inhabitants.SpawnStartInhabitants(host.World, [new(0, 0), new(30, 0)]);
        var first = host.Inhabitants.All.First();
        host.Stores!.Restore([new StoredBody(first.Id, 400, 900)]);
        host.World.Run(5);

        var path = Path.Combine(Path.GetTempPath(), "gw-stores-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(new SimulationHostFactory(content, settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Stores.Stamina(first.Id), loaded.Stores!.Stamina(first.Id));
            Assert.Equal(host.Stores.Health(first.Id), loaded.Stores.Health(first.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The content with the switch on.</summary>
    private static ContentSet On()
    {
        var content = ContentSet.Load(ContentDirectory);
        content.Body!.Stores!.Enabled = true;
        return content;
    }

    /// <summary>Two inhabitants that stand still (only the stores run), no life phase.</summary>
    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, StoresSystem Stores, ContentSet Content) Bare()
    {
        var content = On();
        var world = new World(3, content.Calendar);
        var objects = new WorldObjects(content.Objects);
        var inhabitants = new InhabitantsSystem(content.InhabitantModel, content.Actions, content.Brain, content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var stores = new StoresSystem(content.Body!.Stores!, inhabitants, objects, content.Objects, content.Gaits, world.Calendar.Settings.TicksPerHour);
        world.Add(objects).Add(stores);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(5, 0)]);
        return (world, objects, inhabitants, stores, content);
    }
}
