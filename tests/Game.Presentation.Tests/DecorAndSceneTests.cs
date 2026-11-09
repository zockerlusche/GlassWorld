using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// TL #19722: denser vegetation as decorative plants (presentation only) and the scene finder for
/// comparison shots (creek + spring + hill near a start).
/// </summary>
public sealed class DecorAndSceneTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static GeneratedIsland Island(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    private static IReadOnlyList<Simulation.Objects.WorldObject> Objects(GeneratedIsland island) =>
        [.. island.Objects.Select((o, i) => new Simulation.Objects.WorldObject($"o{i}", o.TypeId, new Simulation.Objects.Position(o.X + 0.5, o.Z + 0.5)))];

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void DecorativePlantsGrowInGroupsOnDryGroundAwayFromObjects(ulong seed)
    {
        var data = Read<LookObjects>("look", "objects.json");
        var island = Island(seed);
        var objects = Objects(island);
        var decor = Decor.Plan(data.Decor, island.Map, seed, objects, 1.0);
        Assert.True(decor.Count > 1000, $"seed {seed}: only {decor.Count} decorative plants");
        Assert.Equal(decor.Select(d => (d.Id, d.TypeId, d.Position)), Decor.Plan(data.Decor, island.Map, seed, objects, 1.0).Select(d => (d.Id, d.TypeId, d.Position)));

        var kinds = data.Decor!.ToDictionary(d => d.Type, StringComparer.Ordinal);
        var looks = new ObjectLooks(data, Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
        foreach (var kind in kinds.Values)
        {
            // Decor types have a look and are no core object types: nobody can use them.
            Assert.True(looks.Knows(kind.Type), kind.Type);
            Assert.False(Content.Value.Objects.Contains(kind.Type), kind.Type);
        }

        foreach (var plant in decor)
        {
            var kind = kinds[plant.TypeId];
            var cell = island.Map.Cell((int)plant.Position.X, (int)plant.Position.Z);
            Assert.Equal(WaterKind.None, cell.Water);
            Assert.NotEqual(MapCell.Blocked, cell.WalkCost);
            Assert.Contains(island.Map.BiomeIds[cell.Biome], kind.Biomes);
            Assert.StartsWith(Decor.IdPrefix, plant.Id, StringComparison.Ordinal);
        }

        // Spacing to the core's objects, checked for a sample of plants.
        foreach (var plant in decor.Where((_, i) => i % 25 == 0))
        {
            var min = kinds[plant.TypeId].MinObjectDistanceM;
            Assert.DoesNotContain(objects, o => Math.Pow(o.Position.X - plant.Position.X, 2) + Math.Pow(o.Position.Z - plant.Position.Z, 2) < (min * min) - 1e-9);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void TheSceneFinderPicksACreekWithASpringAndAHillNearAStart(ulong seed)
    {
        var shots = Read<LookShots>("look", "shots.json");
        var scene = shots.Points.First(p => p.Scene is not null).Scene!;
        var island = Island(seed);
        var spot = SceneFinder.Find(scene, island, 1.0);
        Assert.True(spot is not null, $"seed {seed}: no scene");
        var found = spot!.Value;
        Assert.True(found.StreamCells >= scene.MinStreamCells);
        Assert.True(found.Relief >= scene.MinRelief);
        int x = (int)found.X, z = (int)found.Z;
        Assert.Contains(island.Starts, s => ((s.X - x) * (s.X - x)) + ((s.Z - z) * (s.Z - z)) <= scene.MaxStartDistanceCells * scene.MaxStartDistanceCells);
        Assert.Contains(island.Map.Hydrology!.Springs(), s => ((s.X - x) * (s.X - x)) + ((s.Z - z) * (s.Z - z)) <= scene.RadiusCells * scene.RadiusCells);
        Assert.NotNull(found.YawDeg);
        Assert.True(island.Map.Cell(x, z) is { Water: WaterKind.Fresh, Flow: >= 0 }, $"seed {seed}: the target is no stream cell");
        Assert.Equal(found, SceneFinder.Find(scene, island, 1.0));
    }

    /// <summary>The relief preview (TL #19990): the close-up finds a stream source on a slope on every tried island.</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheReliefShotFindsAStreamSourceOnASlope(ulong seed)
    {
        var shots = Read<LookShots>("look", "shots_relief.json");
        Assert.Equal(2, shots.Points.Count);
        var scene = shots.Points.Single(p => p.Scene is not null).Scene!;
        var island = Island(seed);
        var spot = SceneFinder.Find(scene, island, 1.0);
        Assert.True(spot is { } s && s.Relief >= scene.MinRelief, $"seed {seed}: no stream source on a slope");
        int x = (int)spot!.Value.X, z = (int)spot.Value.Z;
        Assert.Contains(island.Map.Hydrology!.Springs(), p => ((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z)) <= scene.RadiusCells * scene.RadiusCells);
    }
}
