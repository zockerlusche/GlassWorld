using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1380 N2b (R15, D989 v2): grown places of a spreading growth group sow into a neighbouring field cell - only where the
/// spawn table lists the type for the cell's biome, never on a place already there, never into a bare field cell, and a
/// field cell gets no more of the type than the data's limit. The new place starts empty and grows like any other.
/// </summary>
public sealed class SowingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void SpreadingPlacesSowIntoTheirBiomesOnFreeCellsUnderTheLimit()
    {
        var host = Island();
        var map = host.Map!;
        var overuse = host.Content.Nature!.Overuse!;
        Assert.NotEmpty(map.Spreads);
        var initial = map.Occurrences.ToList();
        var initialCells = initial.Select(o => (o.X, o.Z)).ToHashSet();

        host.World.Run(10L * host.World.Calendar.TicksPerDay);

        var sown = map.Occurrences.Skip(initial.Count).ToList();
        Assert.NotEmpty(sown);
        foreach (var place in sown)
        {
            Assert.True(map.Spreads.TryGetValue(place.TypeId, out var spread), $"{place.TypeId} does not spread");
            Assert.Contains((int)map.Map.Cell(place.X, place.Z).Biome, spread.Biomes);
            Assert.DoesNotContain((place.X, place.Z), initialCells);
        }

        Assert.Equal(sown.Count, sown.Select(p => (p.X, p.Z)).Distinct().Count());

        // The limit per field cell: a cell that had more already from the island stays as it was, else at most the limit.
        foreach (var group in sown.GroupBy(p => (p.TypeId, Field: FieldOf(map, p, overuse))))
        {
            var now = map.Occurrences.Count(p => p.TypeId == group.Key.TypeId && FieldOf(map, p, overuse) == group.Key.Field);
            var before = initial.Count(p => p.TypeId == group.Key.TypeId && FieldOf(map, p, overuse) == group.Key.Field);
            Assert.True(now <= Math.Max(before, map.Spreads[group.Key.TypeId].MaxPerCell), $"{group.Key}: {now} places (limit {map.Spreads[group.Key.TypeId].MaxPerCell}, before {before})");
        }

        // Sown places grow like any other: some are grown already, with their thing in the world.
        Assert.Contains(sown, p => p.GoneSinceTick is null && host.Objects.TryGet(p.ObjectId, out _));
    }

    [Fact]
    public void FruitsOnAPlantAndTypesWithoutSpreadDataNeverSow()
    {
        var host = Island();
        var map = host.Map!;
        Assert.DoesNotContain("apple", map.Spreads.Keys);
        Assert.DoesNotContain("dung", map.Spreads.Keys);
        var initial = map.Occurrences.Count;
        host.World.Run(5L * host.World.Calendar.TicksPerDay);
        Assert.All(map.Occurrences.Skip(initial), p => Assert.True(map.Spreads.ContainsKey(p.TypeId)));
    }

    private static SimulationHost Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        return host;
    }

    private static (int X, int Z) FieldOf(WorldMapSystem map, Occurrence place, Contracts.NatureOveruse overuse)
    {
        var at = map.CentreOf(place.X, place.Z);
        return ((int)Math.Floor(at.X / overuse.CellM), (int)Math.Floor(at.Z / overuse.CellM));
    }
}
