using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

public sealed class WorldMapTests
{
    private static readonly string[] Biomes = ["beach_sea", "meadow"];

    private static WorldMapBuilder Filled()
    {
        var builder = new WorldMapBuilder(4, 2, 3, Biomes);
        for (var z = 0; z < 12; z++)
        {
            for (var x = 0; x < 8; x++)
            {
                builder.Set(x, z, new MapCell(x + (z * 10), x < 2 ? WaterKind.Salt : WaterKind.None, x < 2 ? 3 : 0, -1, 400 + x, 160 - z, (byte)(x < 2 ? 0 : 1), 1000));
            }
        }

        return builder;
    }

    [Fact]
    public void CellsAreAddressedAcrossChunks()
    {
        var map = Filled().Build();
        Assert.Equal((8, 12), (map.WidthCells, map.DepthCells));
        Assert.Equal(7 + 110, map.Cell(7, 11).Height);
        Assert.Equal(5 + 40, map.Cell(5, 4).Height);
        Assert.Equal(("beach_sea", "meadow"), (map.BiomeAt(1, 9), map.BiomeAt(2, 9)));
        Assert.False(map.Contains(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(-1, 0));
    }

    [Fact]
    public void BuiltMapIsImmutable()
    {
        var builder = Filled();
        var map = builder.Build();
        var hash = map.Hash();
        builder.Set(3, 3, new MapCell(999, WaterKind.Fresh, 1, 2, 900, 100, 1, 2000));
        Assert.Equal(hash, map.Hash());
        Assert.NotEqual(hash, builder.Build().Hash());
    }

    [Fact]
    public void HashIsStableAndSensitiveToEveryField()
    {
        var a = Filled().Build().Hash();
        Assert.Equal(a, Filled().Build().Hash());
        var changes = new Func<MapCell, MapCell>[]
        {
            c => c with { Height = c.Height + 1 },
            c => c with { Water = WaterKind.Fresh },
            c => c with { WaterDepth = c.WaterDepth + 1 },
            c => c with { Flow = 3 },
            c => c with { Moisture = c.Moisture + 1 },
            c => c with { Temperature = c.Temperature - 1 },
            c => c with { Biome = 0 },
            c => c with { WalkCost = MapCell.Blocked },
        };
        foreach (var change in changes)
        {
            var builder = Filled();
            var cell = builder.Build().Cell(5, 7);
            builder.Set(5, 7, change(cell));
            Assert.NotEqual(a, builder.Build().Hash());
        }
    }

    [Fact]
    public void BuilderRejectsUnknownBiomeAndOutOfRange()
    {
        var builder = new WorldMapBuilder(4, 1, 1, Biomes);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Set(0, 0, new MapCell(0, WaterKind.None, 0, -1, 0, 0, 2, 1000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Set(4, 0, default));
        Assert.Throws<ArgumentException>(() => new WorldMapBuilder(4, 1, 1, []));
    }

    [Fact]
    public void RealWorldDataLoadsWithAllCrossReferences()
    {
        var content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
        var world = content.WorldGen;
        Assert.Contains("start_island", world.Presets.Keys);
        Assert.Contains("temperate", world.Climates.Keys);
        Assert.Equal(["beach_sea", "meadow", "field", "forest", "freshwater", "rocks_hills", "swamp"], world.BiomeIds);
    }

    public static TheoryData<string, string, string, string> BrokenReferences => new()
    {
        { "world/biomes.json", "\"fallback_biome\": \"meadow\"", "\"fallback_biome\": \"desert\"", "fallback biome 'desert'" },
        { "world/spawns.json", "\"object_type\": \"pyrite\"", "\"object_type\": \"gold\"", "unknown object type 'gold'" },
        { "world/spawns.json", "\"properties_all\": [\n        \"sparks\"\n      ]", "\"properties_all\": [\n        \"magic\"\n      ]", "unknown property 'magic'" },
        { "climate/temperate.json", "\"season\": \"winter\"", "\"season\": \"monsoon\"", "unknown season 'monsoon'" },
        { "climate/temperate.json", "\"weather\": \"heat\",\n        \"wind_permille\"", "\"weather\": \"heatwave\",\n        \"wind_permille\"", "'heatwave' not in vocabulary 'weather'" },
        { "world/presets/start_island.json", "\"min_pair_distance_cells\": 40", "\"min_pair_distance_cells\": 20", "must exceed view_range_cells" },
        { "world/presets/start_island.json", "\"phase_permille\": 1000,\n        \"level_permille\": 1000", "\"phase_permille\": 1000,\n        \"level_permille\": 400", "tide curve must end at the level it starts with" },
        { "world/presets/start_island.json", "\"climate_id\": \"temperate\"", "\"climate_id\": \"tropical\"", "unknown climate 'tropical'" },
        { "climate/temperate.json", "\"snow_weather\": \"snow\"", "\"snow_weather\": \"hail\"", "snow weather 'hail' has no state values" },
        { "climate/temperate.json", "\"drought_weather\": \"drought\"", "\"drought_weather\": \"dust\"", "drought weather 'dust' has no state values" },
        { "climate/temperate.json", "\"start_event\": \"storm_started\"", "\"start_event\": \"storm_brewing\"", "signal event 'storm_brewing' not in vocabulary 'event'" },
        { "climate/temperate.json", "\"condition\": \"storm\"", "\"condition\": \"hail\"", "signal condition 'hail' has no state values" },
        { "climate/temperate.json", "\"time_of_day\": \"night\"", "\"time_of_day\": \"midnight\"", "no temperature offset for time of day 'night'" },
        { "world/presets/start_island.json", "\"object_type\": \"rock_niche\"", "\"object_type\": \"dragon_lair\"", "cave object type 'dragon_lair'" },
        { "world/presets/start_island.json", "\"cliff_biome\": \"rocks_hills\"", "\"cliff_biome\": \"glacier\"", "cave cliff biome 'glacier'" },
        { "world/presets/start_island.json", "\"biome\": \"beach_sea\"", "\"biome\": \"lagoon\"", "start biome 'lagoon'" },
        { "world/presets/start_island.json", "\"properties_all\": [\n            \"edible\"\n          ]", "\"properties_all\": [\n            \"magic\"\n          ]", "start supply 'food' uses unknown properties magic" },
        { "world/presets/start_island.json", "\"properties_all\": [\n            \"edible\"\n          ]", "\"properties_all\": [\n            \"sheltering\"\n          ]", "start supply 'food' matches no spawn type of the island" },
        { "world/spawns.json", "\"max_total\": 24,\n      \"regrow_hours\": 96", "\"max_total\": 24,\n      \"regrow_hours\": 97", "disagree on regrow_hours" },
    };

    [Theory]
    [MemberData(nameof(BrokenReferences))]
    public void BrokenReferenceFailsAtLoadWithItsReason(string file, string find, string replace, string reason)
    {
        var copy = CopyContent();
        try
        {
            var path = Path.Combine(copy, file);
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            Assert.Contains(find, text, StringComparison.Ordinal);
            File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
            var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(copy));
            Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    [Fact]
    public void ChangedBiomeTableIsReadWithoutCodeChange()
    {
        // R33 / acceptance 4: a changed table changes the world data, no code involved.
        var copy = CopyContent();
        try
        {
            var path = Path.Combine(copy, "world", "biomes.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"walk_cost_permille\": 2200", "\"walk_cost_permille\": 3300", StringComparison.Ordinal));
            var swamp = ContentSet.Load(copy).WorldGen.Biomes.Biomes.Single(b => b.Id == "swamp");
            Assert.Equal(3300, swamp.WalkCostPermille);
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    /// <summary>
    /// B2 acceptance 4 (R33): a changed table changes the world without code. The same seed, the same
    /// program, a copy of the content in which only one value of the biome table differs (the forest
    /// begins at a lower moisture): the generated island has more forest, less of other land and another
    /// world hash. Nothing but the data file was touched.
    /// </summary>
    [Fact]
    public void AChangedBiomeTableChangesTheGeneratedWorldWithoutCodeChange()
    {
        const ulong seed = 7;
        var original = ContentSet.Load(Path.Combine(TestData.Root, "content"));
        var before = WorldGenerator.Generate(original.WorldGen, original.Objects, "start_island", seed);
        var copy = CopyContent();
        try
        {
            var path = Path.Combine(copy, "world", "biomes.json");
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            const string find = "\"max_height\": 104,\n      \"min_moisture_permille\": 470,";
            Assert.Contains(find, text, StringComparison.Ordinal);
            File.WriteAllText(path, text.Replace(find, "\"max_height\": 104,\n      \"min_moisture_permille\": 400,", StringComparison.Ordinal));
            var changed = ContentSet.Load(copy);
            var forest = changed.WorldGen.Biomes.Rules.Single(r => r.Biome == "forest");
            Assert.Equal(400, forest.MinMoisturePermille);
            Assert.Equal(470, original.WorldGen.Biomes.Rules.Single(r => r.Biome == "forest").MinMoisturePermille);

            var after = WorldGenerator.Generate(changed.WorldGen, changed.Objects, "start_island", seed);
            Assert.NotEqual(before.Hash(), after.Hash());

            // Both islands are the same attempt of the seed, so they stand on the same ground.
            Assert.Equal(before.Attempts, after.Attempts);
            var (cellsBefore, cellsAfter) = (BiomeCells(before.Map), BiomeCells(after.Map));
            Assert.True(cellsAfter["forest"] > cellsBefore["forest"], $"forest cells {cellsBefore["forest"]} -> {cellsAfter["forest"]}");

            // The ground itself is the same (the table sorts cells into biomes, it does not shape them): what
            // the forest gained, the biomes below it in the table lost.
            Assert.Equal(cellsBefore.Values.Sum(), cellsAfter.Values.Sum());
            Assert.Equal(cellsBefore["beach_sea"], cellsAfter["beach_sea"]);
            Assert.True(cellsAfter["meadow"] + cellsAfter["field"] < cellsBefore["meadow"] + cellsBefore["field"]);

            // And the unchanged copy gives the unchanged world: the difference comes from the value alone.
            File.WriteAllText(path, text);
            var same = ContentSet.Load(copy);
            Assert.Equal(before.Hash(), WorldGenerator.Generate(same.WorldGen, same.Objects, "start_island", seed).Hash());
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    private static Dictionary<string, int> BiomeCells(WorldMap map)
    {
        var cells = map.BiomeIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                cells[map.BiomeAt(x, z)]++;
            }
        }

        return cells;
    }

    private static string CopyContent()
    {
        var target = Path.Combine(Path.GetTempPath(), "gw-content-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(Path.Combine(TestData.Root, "content"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(TestData.Root, "content"), file);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }

        return target;
    }
}
