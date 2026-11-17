using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1578 (B4b K5, R33, D711 section 6): digging where one stands - the top soil by hand, deeper only with a tool, the
/// material as a thing beside the pit, a well at the groundwater and nothing deeper; saved and hashed.
/// </summary>
public sealed class DiggingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void BareHandsDigTheTopSoilAndTheFirstDigMakesThePit()
    {
        var (host, who, cell) = Digger();
        var events = Record(host);
        var data = Content.Value.Digging!;

        Dig(host, who);
        var digging = host.Digging!;
        Assert.Equal(data.StepCm, digging.DepthAt(cell.X, cell.Z));
        var centre = host.Map!.CentreOf(cell.X, cell.Z);
        var pit = Assert.Single(host.Objects.All, o => o.TypeId == data.PitType);
        Assert.Equal(centre, pit.Position);
        var dug = Assert.Single(events, e => e.Kind == DiggingSystem.Dug);
        Assert.Equal((who.Id, pit.Id), (dug.InhabitantId, dug.TargetId));

        // What came up is the cell's soil as the data says, lying beside the hole.
        var soil = host.Map.Map.Underground!.SoilAt(cell.X, cell.Z);
        var top = data.Layers.First();
        var expected = top.BySoil is { } bySoil && bySoil.TryGetValue(soil, out var fromSoil) ? fromSoil : top.Material;
        Assert.Equal(expected, dug.Detail);
        Assert.Contains(host.Objects.All, o => o.TypeId == expected && o.Id != pit.Id);
    }

    [Fact]
    public void BelowTheTopSoilBareHandsStopAToolDigsOnToTheWellAndNoDeeper()
    {
        var (host, who, cell) = Digger();
        var events = Record(host);
        var data = Content.Value.Digging!;
        var byHand = data.Layers.TakeWhile(l => l.ByHand == true).Last().UntilCm;
        while (host.Digging!.DepthAt(cell.X, cell.Z) < byHand)
        {
            Dig(host, who);
        }

        Dig(host, who);
        Assert.Equal(DiggingSystem.NoTool, events.Last(e => e.Kind == DiggingSystem.DigFailed).Detail);
        Assert.Equal(byHand, host.Digging.DepthAt(cell.X, cell.Z));

        // A digging stick (a made thing with the tool property) goes on, down to the groundwater.
        host.Types.Register("test_digging_stick", [data.ToolProperty, "portable"]);
        var stick = host.Objects.Spawn("test_digging_stick", who.Position);
        host.Objects.SetHeld(stick.Id, who.Id);
        who.Held = stick.Id;
        for (var i = 0; i < 400 && !events.Any(e => e.Kind == DiggingSystem.WellDug); i++)
        {
            Dig(host, who);
        }

        var well = Assert.Single(events, e => e.Kind == DiggingSystem.WellDug);
        Assert.Equal(data.WellType, well.TargetType);
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == data.PitType);
        Assert.True(host.Content.Objects.HasProperty(data.WellType, "drinkable"));

        Dig(host, who);
        Assert.Equal(DiggingSystem.WaterReached, events.Last(e => e.Kind == DiggingSystem.DigFailed).Detail);
    }

    [Fact]
    public void ADugCellSurvivesSavingAndLoadingWithTheSameHash()
    {
        var (host, who, cell) = Digger();
        Dig(host, who);
        Assert.True(host.Digging!.HasState);
        var path = Path.Combine(Path.GetTempPath(), "gw-digging-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Digging.DepthAt(cell.X, cell.Z), loaded.Digging!.DepthAt(cell.X, cell.Z));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RockIsNotDugByHandAndWetGroundGivesAWaterholeAtOnce()
    {
        // Review GW4 #506: stone topsoil needs a tool; where the groundwater lies at the surface the first dig finds water.
        var (host, who, _) = Digger();
        var events = Record(host);
        var map = host.Map!;
        var underground = map.Map.Underground!;
        var rock = Cells(map).FirstOrDefault(c => underground.SoilAt(c.X, c.Z) == "stone" && underground.Cell(c.X, c.Z).GroundwaterDepth > 2);
        if (rock != default)
        {
            who.Position = map.CentreOf(rock.X, rock.Z);
            Dig(host, who);
            Assert.Equal(DiggingSystem.NoTool, events.Last().Detail);
        }

        var wet = Cells(map).FirstOrDefault(c => underground.Cell(c.X, c.Z).GroundwaterDepth == 0);
        Assert.True(wet != default, "the island has a cell with groundwater at the surface");
        who.Position = map.CentreOf(wet.X, wet.Z);
        Dig(host, who);
        Assert.Equal(DiggingSystem.WellDug, events.Last().Kind);
        Assert.Contains(host.Objects.All, o => o.TypeId == Content.Value.Digging!.WellType && o.Position == map.CentreOf(wet.X, wet.Z));
    }

    /// <summary>Dry walkable cells of the island.</summary>
    [Fact]
    public void ADugPitGrowsOverAndIsGoneAndAWellIsKept()
    {
        // A1460 Z1 (R49, D1248): after the biome's hours of growth the pit is one step shallower - here gone after one dig.
        var (host, who, cell) = Digger();
        var data = Content.Value.Digging!;
        Assert.Contains(data.WellType, data.Overgrow!.Keeps!);
        Dig(host, who);
        var digging = host.Digging!;
        Assert.Equal(data.StepCm, digging.DepthAt(cell.X, cell.Z));
        var centre = host.Map!.CentreOf(cell.X, cell.Z);

        var guard = host.World.Calendar.TicksPerDay * 20;
        var ticks = 0;
        while (digging.DepthAt(cell.X, cell.Z) > 0 && ticks < guard)
        {
            host.World.Step();
            ticks++;
        }

        Assert.Equal(0, digging.DepthAt(cell.X, cell.Z));
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == data.PitType && o.Position == centre);
        var biome = host.Map.Map.BiomeIds[host.Map.Map.Cell(cell.X, cell.Z).Biome];
        var hours = data.Overgrow.HoursPerStep is { } byBiome && byBiome.TryGetValue(biome, out var h) ? h : data.Overgrow.DefaultHoursPerStep;
        Assert.True(ticks >= hours * host.World.Calendar.Settings.TicksPerHour, $"gone after {ticks} ticks, before the {hours} hours of {biome}");
    }

    private static IEnumerable<(int X, int Z)> Cells(WorldMapSystem map) =>
        Enumerable.Range(0, map.Map.WidthCells * map.Map.DepthCells)
            .Select(i => (X: i % map.Map.WidthCells, Z: i / map.Map.WidthCells))
            .Where(c => map.Map.Cell(c.X, c.Z).Water == WaterKind.None && map.Map.Cell(c.X, c.Z).WalkCost != MapCell.Blocked);

    /// <summary>An island and an inhabitant standing on a dry cell of loose soil whose groundwater lies deep (a metre or more).</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    public void OreLiesAtTheSurfaceOnlyWhereTheGroundHoldsOre(ulong seed)
    {
        // A1578 (B2827, Marcel W2245): no ore entrance - ore chunks lie at the surface over ore veins, nowhere else.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", "start_island");
        var map = host.Map!;
        var underground = map.Map.Underground!;
        var rule = Content.Value.WorldGen.Spawns.Spawns.Single(r => r.ObjectType == Content.Value.Digging!.Ore!.Type);
        var ore = host.Objects.All.Where(o => o.TypeId == rule.ObjectType).ToList();
        Assert.NotEmpty(ore);
        Assert.InRange(ore.Count, 1, rule.MaxTotal);
        Assert.All(ore, o =>
        {
            var cell = map.CellOf(o.Position)!.Value;
            Assert.True(underground.Cell(cell.X, cell.Z).OrePermille >= rule.MinOrePermille, $"{o.Id} at {cell} holds no ore");
        });
    }

    private static (SimulationHost Host, Inhabitant Who, (int X, int Z) Cell) Digger()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var map = host.Map!;
        var underground = map.Map.Underground!;
        var who = host.Inhabitants.All.First();
        var start = map.CellOf(who.Position)!.Value;
        var cell = Enumerable.Range(0, map.Map.WidthCells * map.Map.DepthCells)
            .Select(i => (X: i % map.Map.WidthCells, Z: i / map.Map.WidthCells))
            .Where(c => map.Map.Cell(c.X, c.Z).Water == WaterKind.None && map.Map.Cell(c.X, c.Z).WalkCost != MapCell.Blocked
                && underground.Cell(c.X, c.Z).GroundwaterDepth >= 10 && underground.SoilAt(c.X, c.Z) is "loam" or "sand")
            .OrderBy(c => Math.Abs(c.X - start.X) + Math.Abs(c.Z - start.Z))
            .First();
        who.Position = map.CentreOf(cell.X, cell.Z);
        // It stays there while the test digs (a long rest in place).
        who.Activity = new Activity("idle", null, who.Position, 1_000_000);
        who.Planned = null;
        return (host, who, cell);
    }

    /// <summary>One dig ends where the inhabitant stands; the system applies it in the next step.</summary>
    private static void Dig(SimulationHost host, Inhabitant who)
    {
        host.Digging!.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, host.World.Tick, who.Id, Content.Value.Digging!.Action, null));
        host.World.Step();
    }

    private static List<WorldEvent> Record(SimulationHost host)
    {
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return events;
    }
}
