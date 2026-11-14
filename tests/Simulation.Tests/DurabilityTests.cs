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
}
