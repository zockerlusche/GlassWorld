using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

public sealed class WorldGeneratorTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    private static GeneratedIsland Generate(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    [Fact]
    public void TheResourceFactorScalesDensityAndLimitsButKeepsAtLeastOne()
    {
        // A1770 (B2805 'Einfach'): poor/normal/rich multiply every spawn rule; a rule never drops to nothing.
        var content = Content.Value.WorldGen;
        var poor = WorldGenerator.Rich(content.Spawns, 0.6);
        var rich = WorldGenerator.Rich(content.Spawns, 1.5);
        Assert.Same(content.Spawns, WorldGenerator.Rich(content.Spawns, 1.0));
        for (var i = 0; i < content.Spawns.Spawns.Count; i++)
        {
            var rule = content.Spawns.Spawns.ElementAt(i);
            Assert.True(poor.Spawns.ElementAt(i).MaxTotal <= rule.MaxTotal && rich.Spawns.ElementAt(i).MaxTotal >= rule.MaxTotal);
            Assert.True(rule.MaxTotal == 0 || poor.Spawns.ElementAt(i).MaxTotal >= 1);
        }
    }

    [Theory]
    [InlineData(0.6, 1, 11UL)]
    [InlineData(0.6, 4, 12UL)]
    [InlineData(1.5, 1, 13UL)]
    [InlineData(1.5, 4, 14UL)]
    [InlineData(1.0, 3, 15UL)]
    public void EverySimpleChoiceBuildsAnIslandThatPasses(double factor, int pairs, ulong seed)
    {
        // A1770 (B2805, B2808): every combination of the 'Einfach' controls builds a valid island with its starts.
        var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed, null, factor, pairs);
        Assert.True(island.Passed, string.Join(", ", island.Check.Where(c => !c.Passed).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));
        Assert.Equal(pairs * 2, island.Starts.Count);
    }

    [Fact]
    public void ANewWorldTakesOverTheIslandThePreviewGenerated()
    {
        // A1769 (B2805): the start uses exactly the island the preview showed - not generated again - but only when it is
        // this world's island (review GW2 #584): another seed, size, resource factor or pair count generates anew.
        var contentDir = Path.Combine(TestData.Root, "content");
        var shown = new PreviewIsland(WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", 77, null, 1.5, 3), null, 1.5, 3);
        var options = new SimulationHostOptions { Island = shown, ResourceFactor = 1.5, StartPairs = 3 };
        var host = SimulationHost.CreateGeneratedWorld(contentDir, 77, new MockDecisionPort(), "de", "start_island", options: options);
        Assert.Same(shown.Island, host.Map!.Island);
        Assert.Equal(6, host.Inhabitants.All.Count());
        foreach (var (seed, other) in new[]
        {
            (78UL, options),
            (77UL, options with { GridCells = 256 }),
            (77UL, options with { ResourceFactor = 0.6 }),
            (77UL, options with { StartPairs = 2 }),
        })
        {
            var fresh = SimulationHost.CreateGeneratedWorld(contentDir, seed, new MockDecisionPort(), "de", "start_island", options: other);
            Assert.NotSame(shown.Island, fresh.Map!.Island);
            Assert.Equal((other.GridCells ?? 192, other.GridCells ?? 192), (fresh.Map.Island.Map.WidthCells, fresh.Map.Island.Map.DepthCells));
        }
    }

    [Fact]
    public void DesignedStartInhabitantsKeepTheirNameSexAndValuesTheOthersStayDrawn()
    {
        // A1774 (B2805): a designed inhabitant has its name, sex and character values (as a share of the data's range);
        // what is left out is drawn from the seed exactly as before.
        var contentDir = Path.Combine(TestData.Root, "content");
        var drawn = SimulationHost.CreateGeneratedWorld(contentDir, 31, new MockDecisionPort(), "de", "start_island");
        var range = Content.Value.InhabitantModel.Start.Traits.First();
        var design = new[] { new StartInhabitantSpec("Mira", "male", new Dictionary<string, double> { [range.Id] = 1.0 }), new StartInhabitantSpec() };
        var spec = new WorldSpec(Inhabitants: design);
        var made = SimulationHost.CreateGeneratedWorld(contentDir, 31, new MockDecisionPort(), "de", "start_island", options: new SimulationHostOptions { Spec = spec });
        var first = made.Inhabitants.All.First();
        Assert.Equal("Mira", first.Name);
        Assert.Equal("male", first.Sex);
        Assert.Equal(range.Max, first.Traits[range.Id], 6);
        var otherTrait = Content.Value.InhabitantModel.Start.Traits.Skip(1).First().Id;
        Assert.Equal(drawn.Inhabitants.All.First().Traits[otherTrait], first.Traits[otherTrait], 9);
        Assert.Equal(drawn.Inhabitants.All.Skip(1).First().Traits, made.Inhabitants.All.Skip(1).First().Traits);
        Assert.Equal(drawn.Inhabitants.All.Skip(1).First().Sex, made.Inhabitants.All.Skip(1).First().Sex);

        // A name outside the shape of the name data is not taken (review GW3 #598) - one is drawn instead.
        var odd = SimulationHost.CreateGeneratedWorld(contentDir, 31, new MockDecisionPort(), "de", "start_island",
            options: new SimulationHostOptions { Spec = new WorldSpec(Inhabitants: [new StartInhabitantSpec(new string('x', 200))]) });
        Assert.True(ContentSet.IsProperName(odd.Inhabitants.All.First().Name!));
    }

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

    [Fact]
    public void EverySizeOfTheNewWorldFormGivesAnIslandOfItsGridThatPassesTheCheck()
    {
        // A1768 (B2805): the size the player chooses reaches the generator - each option of the data, on the start preset.
        var options = Contracts.Validation.ContractSerializer.Deserialize<Contracts.WorldOptions>(
            File.ReadAllText(Path.Combine(TestData.Root, "content", "player", "world_options.json")));
        Assert.True(options.Sizes.Count >= 2);
        foreach (var size in options.Sizes)
        {
            var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", 42, size.GridCells);
            Assert.Equal((size.GridCells, size.GridCells), (island.Map.WidthCells, island.Map.DepthCells));
            Assert.True(island.Passed, $"{size.Id}: " + string.Join(", ", island.Check.Where(c => !c.Passed).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));
        }

        // The preset of the content stays as it is; a size that does not fit its chunks is refused.
        Assert.Equal(192, Content.Value.WorldGen.Presets["start_island"].Grid.WidthCells);
        Assert.Throws<ArgumentException>(() => WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", 42, 200));
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
    [InlineData(1UL, "e1220b405da610547829e6f9a366e7cf96fde924aea5321031a470b90e2ddb19")]
    [InlineData(7UL, "e8989b45d62df75a8e67051a5498a875b3eafa9f20f3832abb0923752b5b7c41")]
    [InlineData(2026UL, "dae46dbcc438647cc28439e158f5bb3a1ada8396483eddfb153ee67f83a3ce1c")]
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
    public void AStartShortOfDrinkGetsSpringsAndOnlyAnImpossibleNeedFails()
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
        // A1833 (B2808 step 4, the generator always builds): a start short of drink gets springs in its reach - the drink
        // spots' fixed limits no longer fail an island.
        drink.Min = 30;
        var supplied = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 42);
        Assert.True(supplied.Passed, string.Join(", ", supplied.Check.Where(c => !c.Passed).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));

        // Only a need no reach can hold (more springs than cells) still fails the attempt and regenerates, as before.
        drink.Min = 100_000;
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
