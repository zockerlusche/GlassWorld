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
    [InlineData(1UL, "333906f7b4166fcccdf0d0171bd5a4adc30c1616703a2a44eeaae5e4d94d93d7")]
    [InlineData(7UL, "bc79dd8f003174eeda83f92f7beb8e65b0f146f34d64253c60945a22f4e3d8a4")]
    [InlineData(2026UL, "c4ba70dc8e43683ad6f2e3e54fe32549074b421c23899cd47859c5c5e308d7fe")]
    public void GoldenWorldHashesAreStableAcrossMachines(ulong seed, string expected)
    {
        var actual = Generate(seed).Hash();
        Assert.True(actual == expected, $"golden world hash for seed {seed} changed: {actual}");
    }

    /// <summary>
    /// Start supply (TL #19280, F5263): starts are chosen where the supply is possible, so every start of
    /// the acceptance seeds finds the listed resources within the radius and within its walkable area.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(6UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(99UL)]
    [InlineData(123UL)]
    [InlineData(2026UL)]
    public void EveryStartFindsItsSupplyWithinTheRadius(ulong seed)
    {
        var island = Generate(seed);
        var supply = Content.Value.WorldGen.Presets["start_island"].Start.Supply;
        foreach (var start in island.Starts)
        {
            foreach (var resource in supply.Resources)
            {
                var near = Supplied(island, start, resource);
                Assert.True(near >= resource.Min, $"seed {seed}: start ({start.X}, {start.Z}) has {near} '{resource.Id}', wants {resource.Min}");
            }
        }

        Assert.All(island.Check.Where(c => c.Id.Contains("start_supply/", StringComparison.Ordinal)), c => Assert.True(c.Passed, c.Id));
    }

    /// <summary>
    /// The check reports what the poorest start finds; only a mandatory resource (drink) decides whether
    /// the island passes (a failed one regenerates it), the others only report.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(6UL)]
    [InlineData(42UL)]
    public void TheCheckReportsWhatThePoorestStartFinds(ulong seed)
    {
        var island = Generate(seed);
        Assert.True(island.Passed);
        foreach (var resource in Content.Value.WorldGen.Presets["start_island"].Start.Supply.Resources)
        {
            var mandatory = resource.Mandatory == true;
            var line = Assert.Single(island.Check, c => c.Id == $"{(mandatory ? string.Empty : MandatoryResult.ReportPrefix)}start_supply/{resource.Id}");
            Assert.Equal(!mandatory, line.ReportOnly);
            Assert.True(!mandatory || line.Passed, $"{resource.Id} is mandatory and passed");
            Assert.Equal(island.Starts.Min(start => Supplied(island, start, resource)), line.Reachable);
            Assert.Equal(resource.Min, line.Min);
        }
    }

    /// <summary>
    /// A drink supply that a start misses fails the attempt (mandatory, GW3 #120): the generator tries the
    /// next sub-seeds and reports the island as failed if none serves every start. The mandatory water
    /// check counts drinkable, not salty objects.
    /// </summary>
    [Fact]
    public void AMissingDrinkSupplyFailsTheAttemptAndRegenerates()
    {
        var island = Generate(42);
        var water = Assert.Single(island.Check, c => c.Id == "drinking_water");
        var catalog = Content.Value.Objects;
        Assert.Equal(island.Objects.Count(o => catalog.HasProperty(o.TypeId, "drinkable") && !catalog.HasProperty(o.TypeId, "salty")), water.Total);
        Assert.True(water.Passed);

        var content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
        var preset = content.WorldGen.Presets["start_island"];
        var drink = preset.Start.Supply.Resources.Single(r => r.Id == "drink");
        Assert.True(drink.Mandatory);
        drink.Min = 100;
        var starved = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 42);
        Assert.False(starved.Passed);
        Assert.Equal(preset.Start.MaxAttempts, starved.Attempts);
        var line = Assert.Single(starved.Check, c => c.Id == "start_supply/drink");
        Assert.False(line.Passed || line.ReportOnly);

        // Deterministic: the same seed gives the same failed island.
        Assert.Equal(starved.Hash(), WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 42).Hash());
    }

    /// <summary>
    /// The start supply places only what the spawn table allows: a type on the cells of one of its
    /// rules (the rule's biome, or a bank of it for water biomes) and never beyond the rules' max_total.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(6UL)]
    [InlineData(42UL)]
    public void ObjectsKeepToTheSpawnTable(ulong seed)
    {
        var island = Generate(seed);
        var spawns = Content.Value.WorldGen.Spawns.Spawns;
        var preset = Content.Value.WorldGen.Presets["start_island"];
        var cave = preset.Caves.ObjectType;
        var water = preset.Water.DrinkSpots.Select(d => d.ObjectType).ToHashSet(StringComparer.Ordinal);
        var map = island.Map;

        // Caves and drink spots come from the terrain and its water, not from the spawn table.
        foreach (var o in island.Objects.Where(o => o.TypeId != cave && !water.Contains(o.TypeId)))
        {
            var biomes = spawns.Where(r => r.ObjectType == o.TypeId).Select(r => r.Biome).ToHashSet(StringComparer.Ordinal);
            var around = new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) }
                .Where(d => map.Contains(o.X + d.Item1, o.Z + d.Item2))
                .Select(d => map.BiomeAt(o.X + d.Item1, o.Z + d.Item2));
            Assert.True(around.Any(biomes.Contains), $"seed {seed}: {o.TypeId} at ({o.X}, {o.Z}) lies in {map.BiomeAt(o.X, o.Z)}, outside its spawn biomes");
        }

        foreach (var type in spawns.GroupBy(r => r.ObjectType, StringComparer.Ordinal))
        {
            Assert.True(island.Objects.Count(o => o.TypeId == type.Key) <= type.Sum(r => r.MaxTotal), $"seed {seed}: more {type.Key} than the spawn table allows");
        }
    }

    private static int Supplied(GeneratedIsland island, StartPosition start, Contracts.StartSupplyResource resource)
    {
        var catalog = Content.Value.Objects;
        var radius = Content.Value.WorldGen.Presets["start_island"].Start.Supply.RadiusCells;
        var areas = PathFinder.WalkableAreas(island.Map);
        var w = island.Map.WidthCells;
        var area = areas[(start.Z * w) + start.X];
        return island.Objects.Count(o =>
            ((o.X - start.X) * (o.X - start.X)) + ((o.Z - start.Z) * (o.Z - start.Z)) <= radius * radius
            && area != 0 && areas[(o.Z * w) + o.X] == area
            && resource.PropertiesAll.All(p => catalog.HasProperty(o.TypeId, p))
            && !resource.PropertiesNone.Any(p => catalog.HasProperty(o.TypeId, p)));
    }
}
