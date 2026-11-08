using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

public sealed class WorldGeneratorTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    private static GeneratedIsland Generate(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    [Fact]
    public void SameSeedGivesTheSameWorldTwice()
    {
        Assert.Equal(Generate(42).Hash(), Generate(42).Hash());
    }

    [Fact]
    public void DifferentSeedsGiveDifferentWorlds()
    {
        var hashes = Enumerable.Range(1, 12).Select(s => Generate((ulong)s).Hash()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(12, hashes.Count);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(99UL)]
    [InlineData(2026UL)]
    public void StartIslandPassesTheMandatoryCheck(ulong seed)
    {
        var island = Generate(seed);
        Assert.True(island.Passed, $"seed {seed}: " + string.Join(", ", island.Check.Where(c => !c.Passed).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));
        Assert.Equal(4, island.Starts.Count);

        // Every biome of the table appears (B2532), pairs do not see each other but can reach each other.
        var map = island.Map;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                seen.Add(map.BiomeAt(x, z));
            }
        }

        Assert.Equal(map.BiomeIds.Count, seen.Count);
        var preset = Content.Value.WorldGen.Presets["start_island"];
        var a = island.Starts[0];
        var b = island.Starts[2];
        var d2 = ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));
        Assert.True(d2 > preset.Exploration.ViewRangeCells * preset.Exploration.ViewRangeCells);
        var reach = WorldGenerator.Reachable(map, a.X, a.Z);
        Assert.True(island.Starts.All(s => reach[(s.Z * map.WidthCells) + s.X]));
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(11UL)]
    public void StreamsNeverFlowUphillAndEndInWater(ulong seed)
    {
        var map = Generate(seed).Map;
        (int Dx, int Dz)[] directions = [(1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1)];
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var c = map.Cell(x, z);
                if (c.Flow < 0)
                {
                    continue;
                }

                var (dx, dz) = directions[c.Flow];
                var next = map.Cell(x + dx, z + dz);
                Assert.True(next.Height <= c.Height, $"seed {seed}: stream at ({x},{z}) flows uphill");
                Assert.True(next.Water != WaterKind.None, $"seed {seed}: stream at ({x},{z}) runs into dry land");
            }
        }
    }

    [Fact]
    public void ObjectsAreCatalogueTypesOnWalkableCells()
    {
        var island = Generate(5);
        Assert.NotEmpty(island.Objects);
        foreach (var o in island.Objects)
        {
            Assert.True(Content.Value.Objects.Contains(o.TypeId));
            Assert.NotEqual(MapCell.Blocked, island.Map.Cell(o.X, o.Z).WalkCost);
        }
    }

    /// <summary>
    /// Golden world hashes (B2 acceptance 1): the generator works in integers only, so these hashes must
    /// be equal on Windows and Linux (CI). A deliberate change of generator or data updates them here.
    /// </summary>
    [Theory]
    [InlineData(1UL, "cc682d754d0e6f866fbfa287b81546280827ec3d72226d398bc6d29b3f02c6f6")]
    [InlineData(7UL, "8e193540e5a232d6bf28a1a97c7dc998acdebb60fa3119b73ab0bb015a0f1573")]
    [InlineData(2026UL, "bf11b96c99a7c2b3448fe3dc9e592c16effc07ef0c2647d5b5ab85189e49e98a")]
    public void GoldenWorldHashesAreStableAcrossMachines(ulong seed, string expected)
    {
        var actual = Generate(seed).Hash();
        Assert.True(actual == expected, $"golden world hash for seed {seed} changed: {actual}");
    }

    /// <summary>Start favour (TL #19280): every start finds the listed resources within the radius.</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(6UL)]
    [InlineData(42UL)]
    public void EveryStartFindsTheFavouredResourcesWithinTheRadius(ulong seed)
    {
        var island = Generate(seed);
        var favor = Content.Value.WorldGen.Presets["start_island"].Start.Favor;
        var catalog = Content.Value.Objects;
        foreach (var start in island.Starts)
        {
            foreach (var resource in favor.Resources)
            {
                var near = island.Objects.Count(o =>
                    ((o.X - start.X) * (o.X - start.X)) + ((o.Z - start.Z) * (o.Z - start.Z)) <= favor.RadiusCells * favor.RadiusCells
                    && resource.PropertiesAll.All(p => catalog.HasProperty(o.TypeId, p))
                    && !resource.PropertiesNone.Any(p => catalog.HasProperty(o.TypeId, p)));
                Assert.True(near >= resource.Min, $"seed {seed}: start ({start.X}, {start.Z}) has {near} '{resource.Id}', wants {resource.Min}");
            }
        }
    }
}
