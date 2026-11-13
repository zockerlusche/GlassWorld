using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// W1936 (Marcel live, B2640 the world for the eye): a fruit grows at its plant - never loose on open ground - and
/// whoever looks at, picks or eats a thing stands in reach of it. The headless check the TL asked for (#22539):
/// generate the island, run three days, count the violations; the plants and radii come from the data.
/// </summary>
public sealed class FruitAtPlantTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void TheDataBindsEveryFruitToAPlantTheIslandSpawns()
    {
        var fruits = Content.WorldGen.Spawns.Spawns.Where(r => Content.Objects.HasProperty(r.ObjectType, "fruit")).ToList();
        Assert.NotEmpty(fruits);
        Assert.All(fruits, r => Assert.NotNull(r.GrowsOn));
        Assert.All(fruits, r => Assert.Contains(Content.WorldGen.Spawns.Spawns, p => p.ObjectType == r.GrowsOn!.ObjectType && p.GrowsOn is null));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void EveryFruitLiesAtItsPlantOnTheNewIslandAndAfterThreeDays(ulong seed)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", "start_island");
        Assert.Empty(Loose(host));

        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        host.World.Run(host.World.Calendar.TicksPerDay * 3);
        var loose = Loose(host);
        Assert.True(loose.Count == 0, $"seed {seed}: {loose.Count} loose fruits, e.g. {string.Join("; ", loose.Take(3))}");
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionStarted);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void WhoActsOnAThingStandsWithinReachOfIt(ulong seed)
    {
        // Every action on a thing starts with the inhabitant within the action's reach (data: reach_m).
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", "start_island");
        var violations = new List<string>();
        var checkedStarts = 0;
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind != InhabitantsSystem.ActionStarted || e.TargetId is null || !host.Objects.TryGet(e.TargetId, out var thing))
            {
                return;
            }

            var who = host.Inhabitants.All.Single(i => i.Id == e.InhabitantId);
            var reach = host.Content.Actions.Timing(e.Action!).ReachM;
            var distance = Math.Sqrt(Math.Pow(who.Position.X - thing.Position.X, 2) + Math.Pow(who.Position.Z - thing.Position.Z, 2));
            checkedStarts++;
            if (distance > reach + 1e-6)
            {
                violations.Add($"{e.InhabitantId} {e.Action} {thing.TypeId} at {distance:0.00} m (reach {reach:0.00})");
            }
        };

        host.World.Run(host.World.Calendar.TicksPerDay * 3);
        Assert.True(checkedStarts > 0);
        Assert.True(violations.Count == 0, $"seed {seed}: {violations.Count} of {checkedStarts} out of reach, e.g. {string.Join("; ", violations.Take(3))}");
    }

    [Fact]
    public void AFirstTryIsMarkedAndTheSecondIsNot()
    {
        // GW3 #22738: 'trinkt: Apfel' is learning by trying; the core marks it, so the card can say so.
        var host = SimulationHost.CreateEmpty(ContentDirectory, 5, new MockDecisionPort(), "de");
        var model = Content.InhabitantModel;
        var who = new Inhabitant("inh-000001", "female", 1, new Position(0, 0), model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal), model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal), model.WillToLive.Start);
        host.Inhabitants.Add(who);
        var apple = host.Objects.Spawn("apple", new Position(1, 0));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        who.Activity = new Activity("drink", apple.Id, apple.Position, null);
        for (var i = 0; i < 200 && who.Activity is not null; i++)
        {
            host.World.Step();
            if (who.Activity is { RemainingTicks: not null })
            {
                Assert.True(host.Inhabitants.IsTrying(who));
            }
        }

        Assert.Equal(InhabitantsSystem.Try, events.First(e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "drink").Detail);
        Assert.True(who.Beliefs.TryGet("apple", "drinkable", out var learned) && !learned.Value);

        events.Clear();
        who.Activity = new Activity("drink", apple.Id, apple.Position, null);
        host.World.Step();
        Assert.Null(events.First(e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "drink").Detail);
        Assert.False(host.Inhabitants.IsTrying(who));
    }

    /// <summary>Fruits of a grows_on rule that lie farther than its radius (cells, each axis) from every instance of their plant.</summary>
    private static List<string> Loose(SimulationHost host)
    {
        var map = host.Map!;
        var rules = Content.WorldGen.Spawns.Spawns.Where(r => r.GrowsOn is not null).GroupBy(r => r.ObjectType).ToDictionary(g => g.Key, g => g.First().GrowsOn!);
        var all = host.Objects.All.ToList();
        var loose = new List<string>();
        foreach (var fruit in all.Where(o => rules.ContainsKey(o.TypeId)))
        {
            var grows = rules[fruit.TypeId];
            var at = map.CellOf(fruit.Position)!.Value;
            var near = all.Any(p => p.TypeId == grows.ObjectType && map.CellOf(p.Position) is { } c && Math.Abs(c.X - at.X) <= grows.WithinCells && Math.Abs(c.Z - at.Z) <= grows.WithinCells);
            if (!near)
            {
                loose.Add($"{fruit.TypeId} {fruit.Id} at cell {at}");
            }
        }

        return loose;
    }
}
