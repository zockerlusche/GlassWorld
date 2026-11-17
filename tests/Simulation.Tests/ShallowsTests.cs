using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1842 (R43, plan D1250): the shallow sea of the beach has its own things - seaweed (edible, fibrous) and shells - placed
/// by spawn rules with in_water: shallow on the biome's walkable water cells; every other rule stays on dry cells.
/// </summary>
public sealed class ShallowsTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void SeaweedGrowsOnlyInTheShallowWaterAndDryRulesStayOnLand(ulong seed)
    {
        var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);
        var map = island.Map;
        var inWater = Content.Value.WorldGen.Spawns.Spawns.Where(r => r.InWater is not null).Select(r => r.ObjectType).ToHashSet(StringComparer.Ordinal);
        var onLand = Content.Value.WorldGen.Spawns.Spawns.Where(r => r.InWater is null).Select(r => r.ObjectType).ToHashSet(StringComparer.Ordinal);

        var seaweed = island.Objects.Where(o => o.TypeId == "seaweed").ToList();
        Assert.NotEmpty(seaweed);
        Assert.All(seaweed, o =>
        {
            var cell = map.Cell(o.X, o.Z);
            Assert.NotEqual(WaterKind.None, cell.Water);
            Assert.NotEqual(MapCell.Blocked, cell.WalkCost);
        });

        // Shells lie on the beach and in its shallow water; a type only dry rules place is never in the water.
        Assert.Contains(island.Objects, o => o.TypeId == "shell" && map.Cell(o.X, o.Z).Water != WaterKind.None);
        Assert.All(island.Objects.Where(o => onLand.Contains(o.TypeId) && !inWater.Contains(o.TypeId)), o =>
            Assert.True(map.Cell(o.X, o.Z).Water == WaterKind.None || Content.Value.WorldGen.Spawns.Spawns.All(r => r.ObjectType != o.TypeId),
                $"{o.TypeId} at {o.X},{o.Z} lies in the water"));

        // Edible and fibrous by the data (TL #28525).
        var properties = Content.Value.Objects.TrueProperties("seaweed");
        Assert.Contains("edible", (IReadOnlySet<string>)properties);
        Assert.Contains("fibrous", (IReadOnlySet<string>)properties);
    }
}
