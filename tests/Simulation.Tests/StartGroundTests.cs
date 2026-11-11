using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Generator version 5 (TL #20750, findings 17 and 18 of the visual survey D704): the start cells stand on
/// dry ground that no tide reaches (<c>start.dry_ground</c>), the two of a pair at least the personal
/// space apart (W1695), and the mandatory check rejects an island with a flooded start.
/// </summary>
public sealed class StartGroundTests
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    private static GeneratedIsland Generate(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, Preset, seed);

    [Fact]
    public void TheStartIslandAsksForDryStartGroundInVersionFive()
    {
        var preset = Content.Value.WorldGen.Presets[Preset];
        Assert.Equal(5, preset.GeneratorVersion);
        Assert.NotNull(preset.Start.DryGround);
        Assert.True(preset.Tides.Amplitude > 0, "the start island has tides, so the rule has something to keep dry");
    }

    /// <summary>The high-tide line is the highest level of the curve: no tick of a cycle lies above it, and one reaches it.</summary>
    [Fact]
    public void NoTickLiesAboveTheHighTideLine()
    {
        var tides = new Tides(Content.Value.WorldGen.Presets[Preset].Tides);
        var levels = Enumerable.Range(0, tides.PeriodTicks).Select(t => tides.LevelTenthsAt(t)).ToList();
        Assert.Equal(tides.Amplitude * 10, tides.HighestLevelTenths);
        Assert.Equal(tides.HighestLevelTenths, levels.Max());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(99UL)]
    [InlineData(123UL)]
    [InlineData(2026UL)]
    public void StartCellsAndTheirSurroundingsLieAboveTheHighestTide(ulong seed)
    {
        var preset = Content.Value.WorldGen.Presets[Preset];
        var dry = preset.Start.DryGround!;
        var tides = new Tides(preset.Tides);
        var island = Generate(seed);
        var map = island.Map;
        Assert.True(island.Passed);
        Assert.Equal(preset.Start.Pairs * 2, island.Starts.Count);

        // Counted here from the cells and the tide of every tick, not with the generator's own test.
        var highest = Enumerable.Range(0, tides.PeriodTicks).Max(t => tides.LevelTenthsAt(t));
        var line = (preset.Terrain.SeaLevel * 10) + highest + (dry.Margin * 10);
        foreach (var start in island.Starts)
        {
            for (var dz = -dry.RadiusCells; dz <= dry.RadiusCells; dz++)
            {
                for (var dx = -dry.RadiusCells; dx <= dry.RadiusCells; dx++)
                {
                    if ((dx * dx) + (dz * dz) > dry.RadiusCells * dry.RadiusCells)
                    {
                        continue;
                    }

                    Assert.True(map.Contains(start.X + dx, start.Z + dz), $"seed {seed}: start ({start.X}, {start.Z}) lies at the map edge");
                    var cell = map.Cell(start.X + dx, start.Z + dz);
                    Assert.True(cell.Water == WaterKind.None, $"seed {seed}: {cell.Water} water at ({start.X + dx}, {start.Z + dz}) beside start ({start.X}, {start.Z})");
                    Assert.True(cell.Height * 10 >= line, $"seed {seed}: ground {cell.Height} at ({start.X + dx}, {start.Z + dz}) beside start ({start.X}, {start.Z}) lies below the dry line {line / 10.0}");
                }
            }

            Assert.NotEqual(MapCell.Blocked, map.Cell(start.X, start.Z).WalkCost);
            Assert.True(NearBiome(map, start, dry.ShoreDistanceCells, preset.Start.Biome), $"seed {seed}: start ({start.X}, {start.Z}) lies further than {dry.ShoreDistanceCells} cells from '{preset.Start.Biome}'");
        }

        var check = Assert.Single(island.Check, c => c.Id == WorldGenerator.StartDryGroundCheck);
        Assert.True(check.Passed);
        Assert.False(check.ReportOnly);
        Assert.Equal(island.Starts.Count, check.Reachable);
    }

    /// <summary>W1695 from the first tick: the two of a pair stand at least the personal space of the gaits data apart.</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void ThePairStartsAtLeastThePersonalSpaceApart(ulong seed)
    {
        var separation = Content.Value.Gaits.Data.Spacing!.MinSeparationM;
        Assert.True(separation > 0);
        Assert.Equal((int)Math.Round(separation * 100), Content.Value.WorldGen.StartSeparationCm);
        var cellM = Content.Value.WorldGen.Presets[Preset].Grid.CellSizeCm / 100.0;
        foreach (var pair in Generate(seed).Starts.GroupBy(s => s.Pair))
        {
            var cells = pair.ToList();
            Assert.Equal(2, cells.Count);
            var distance = Math.Sqrt(Sq(cells[0].X - cells[1].X) + Sq(cells[0].Z - cells[1].Z)) * cellM;
            Assert.True(distance >= separation, $"seed {seed}: pair {pair.Key} starts {distance} m apart, the personal space is {separation} m");
        }
    }

    /// <summary>The inhabitants of a new world stand where the generator put them: every two at least the personal space apart.</summary>
    [Fact]
    public void TheInhabitantsOfANewWorldStartApartAndOnDryGround()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var separation = host.Content.Gaits.Data.Spacing!.MinSeparationM;
        var all = host.Inhabitants.All.ToList();
        Assert.Equal(4, all.Count);
        foreach (var a in all)
        {
            foreach (var b in all.Where(o => string.CompareOrdinal(o.Id, a.Id) > 0))
            {
                var distance = Math.Sqrt(((a.Position.X - b.Position.X) * (a.Position.X - b.Position.X)) + ((a.Position.Z - b.Position.Z) * (a.Position.Z - b.Position.Z)));
                Assert.True(distance >= separation, $"{a.Id} and {b.Id} start {distance} m apart");
            }

            // The world starts at high tide (tick 0 of the curve): the water stays below the feet.
            var cell = host.Map!.CellOf(a.Position)!.Value;
            var ground = host.Map.Map.Cell(cell.X, cell.Z);
            var water = (host.Content.WorldGen.Presets[Preset].Terrain.SeaLevel * 10) + host.Map.TideAt(host.World.Tick)!.LevelTenths;
            Assert.Equal(WaterKind.None, ground.Water);
            Assert.True(ground.Height * 10 > water, $"{a.Id} starts in the water: ground {ground.Height}, water {water / 10.0}");
        }
    }

    /// <summary>A wider personal space in the data moves the partner further away (the rule is data, not the cell size).</summary>
    [Fact]
    public void AWiderPersonalSpaceMovesThePartnerFurtherAway()
    {
        var set = Content.Value;
        var wide = WorldGenContent.Load(ContentDirectory, set.Vocabularies, set.Objects, set.Calendar, minSeparationM: 2.5);
        Assert.Equal(250, wide.StartSeparationCm);
        var island = WorldGenerator.Generate(wide, set.Objects, Preset, 2026);
        Assert.True(island.Passed);
        foreach (var pair in island.Starts.GroupBy(s => s.Pair))
        {
            var cells = pair.ToList();
            Assert.True(Sq(cells[0].X - cells[1].X) + Sq(cells[0].Z - cells[1].Z) >= 2.5 * 2.5, $"pair {pair.Key}: ({cells[0].X}, {cells[0].Z}) and ({cells[1].X}, {cells[1].Z})");
        }
    }

    /// <summary>
    /// The start check rejects a flooded start: an island placed by the rule of version 4 (starts on the
    /// beach, below the high-tide line) fails the line of the dry start ground.
    /// </summary>
    [Fact]
    public void TheStartCheckRejectsAFloodedStart()
    {
        var old = ContentSet.Load(ContentDirectory);
        var preset = old.WorldGen.Presets[Preset];
        var dry = preset.Start.DryGround;
        preset.Start.DryGround = null;
        var island = WorldGenerator.Generate(old.WorldGen, old.Objects, Preset, 7);
        Assert.True(island.Passed);
        Assert.DoesNotContain(island.Check, c => c.Id == WorldGenerator.StartDryGroundCheck);
        Assert.Null(WorldGenerator.StartGroundCheck(preset, island.Map, island.Starts));

        preset.Start.DryGround = dry;
        var tides = new Tides(preset.Tides);
        var highWater = (preset.Terrain.SeaLevel * 10) + tides.HighestLevelTenths;
        Assert.All(island.Starts, s => Assert.True(island.Map.Cell(s.X, s.Z).Height * 10 < highWater, $"start ({s.X}, {s.Z}) of the old rule is not flooded"));
        var check = WorldGenerator.StartGroundCheck(preset, island.Map, island.Starts)!;
        Assert.Equal(WorldGenerator.StartDryGroundCheck, check.Id);
        Assert.False(check.Passed);
        Assert.False(check.ReportOnly);
        Assert.Equal(0, check.Reachable);
        Assert.Equal(preset.Start.Pairs * 2, check.Min);
        Assert.All(island.Starts, s => Assert.False(WorldGenerator.OnDryStartGround(preset, island.Map, s.X, s.Z)));
    }

    /// <summary>An island that has no dry start ground at all is rejected after the last attempt, never passed silently.</summary>
    [Fact]
    public void AnIslandWithoutDryStartGroundIsRejected()
    {
        var content = ContentSet.Load(ContentDirectory);
        var preset = content.WorldGen.Presets[Preset];
        preset.Start.DryGround!.Margin = preset.Terrain.MaxHeight;
        preset.Start.MaxAttempts = 2;
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, Preset, 7);
        Assert.False(island.Passed);
        Assert.Equal(2, island.Attempts);
        Assert.Empty(island.Starts);
        var check = Assert.Single(island.Check, c => c.Id == WorldGenerator.StartDryGroundCheck);
        Assert.False(check.Passed);
    }

    /// <summary>Without the data the start placement is that of version 4: its golden hash of seed 7.</summary>
    [Fact]
    public void WithoutDryGroundTheGeneratorIsVersionFour()
    {
        var content = ContentSet.Load(ContentDirectory);
        var preset = content.WorldGen.Presets[Preset];
        preset.Start.DryGround = null;
        preset.GeneratorVersion = 4;
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, Preset, 7);
        Assert.Equal("484c966f2cd51655ae8c23b289e305fbd6424ee9206d7414ad2ef5a37d2acd2f", island.Hash());
    }

    private static bool NearBiome(WorldMap map, StartPosition start, int radius, string biome)
    {
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dz * dz) <= radius * radius && map.Contains(start.X + dx, start.Z + dz) && map.BiomeAt(start.X + dx, start.Z + dz) == biome)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Sq(int v) => v * v;
}
