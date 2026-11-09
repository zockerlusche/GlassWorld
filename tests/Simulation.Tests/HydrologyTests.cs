using System.IO.Compression;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Hydrology from the heightmap and the underground (TL #19722, #19717): streams follow the drainage,
/// springs rise where the groundwater meets the ground and carry the drinking water, hollows hold ponds,
/// and both layers are saved with the island.
/// </summary>
public sealed class HydrologyTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    private static WorldPreset Preset => Content.Value.WorldGen.Presets["start_island"];

    private static GeneratedIsland Generate(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void EveryStreamRunsDownhillIntoTheSeaOrALake(ulong seed)
    {
        var map = Generate(seed).Map;
        var hydrology = Assert.IsType<HydrologyLayer>(map.Hydrology);
        var streams = 0;
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if ((hydrology.Feature(x, z) & WaterFeature.Stream) == 0 || map.Cell(x, z).Flow < 0)
                {
                    continue;
                }

                streams++;
                int cx = x, cz = z;
                for (var guard = 0; guard < map.WidthCells * map.DepthCells; guard++)
                {
                    var c = map.Cell(cx, cz);
                    if (c.Water == WaterKind.Salt || (hydrology.Feature(cx, cz) & WaterFeature.Lake) != 0 || c.Flow < 0)
                    {
                        break;
                    }

                    var (dx, dz) = FlowDirections.All[c.Flow];
                    var next = map.Cell(cx + dx, cz + dz);
                    Assert.True(next.Height <= c.Height, $"seed {seed}: ({cx},{cz}) flows uphill");
                    Assert.NotEqual(WaterKind.None, next.Water);
                    (cx, cz) = (cx + dx, cz + dz);
                }

                var end = map.Cell(cx, cz);
                Assert.True(end.Water == WaterKind.Salt || (hydrology.Feature(cx, cz) & WaterFeature.Lake) != 0 || (hydrology.Feature(cx, cz) & WaterFeature.Stream) != 0, $"seed {seed}: the stream from ({x},{z}) ends at ({cx},{cz})");
            }
        }

        Assert.True(streams > 0, $"seed {seed}: no stream");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void AccumulationGrowsDownstreamAndWidthFollowsIt(ulong seed)
    {
        var map = Generate(seed).Map;
        var hydrology = map.Hydrology!;
        var min = Preset.Water.StreamMinAccumulation;
        var widths = new List<(int Accumulation, int Width)>();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var c = map.Cell(x, z);
                var accumulation = hydrology.Accumulation(x, z);
                if ((hydrology.Feature(x, z) & WaterFeature.Stream) == 0 || c.Flow < 0 || accumulation < min)
                {
                    continue;
                }

                // A stream centre: its accumulation is reached, and the next cell carries at least as much.
                widths.Add((accumulation, hydrology.WidthCm(x, z)));
                var (dx, dz) = FlowDirections.All[c.Flow];
                if ((hydrology.Feature(x + dx, z + dz) & WaterFeature.Stream) != 0)
                {
                    Assert.True(hydrology.Accumulation(x + dx, z + dz) >= accumulation, $"seed {seed}: accumulation falls after ({x},{z})");
                }
            }
        }

        var sorted = widths.OrderBy(w => w.Accumulation).ToList();
        Assert.True(sorted.Zip(sorted.Skip(1)).All(p => p.Second.Width >= p.First.Width), $"seed {seed}: width does not grow with accumulation");
        Assert.Equal(Preset.Water.Channel.First().WidthCm, sorted[0].Width);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(99UL)]
    public void DrinkingWaterLiesExactlyAtTheSpringsPondEdgesStreamBanksAndHollows(ulong seed)
    {
        var island = Generate(seed);
        var map = island.Map;
        var hydrology = map.Hydrology!;
        var springs = hydrology.Springs().ToList();
        Assert.NotEmpty(springs);
        var catalog = Content.Value.Objects;

        // No spawn rule invents drinking water any more; the hydrology places it.
        Assert.DoesNotContain(Content.Value.WorldGen.Spawns.Spawns, r => catalog.HasProperty(r.ObjectType, "drinkable") && !catalog.HasProperty(r.ObjectType, "salty"));
        var drink = island.Objects.Where(o => catalog.HasProperty(o.TypeId, "drinkable") && !catalog.HasProperty(o.TypeId, "salty")).ToList();
        foreach (var o in drink)
        {
            var atSpring = (hydrology.Feature(o.X, o.Z) & WaterFeature.Spring) != 0;
            var atPond = map.Cell(o.X, o.Z).Water == WaterKind.None
                && new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => (hydrology.Feature(o.X + d.Item1, o.Z + d.Item2) & WaterFeature.Lake) != 0);
            var bankReach = Preset.Water.DrinkSpots.Where(s => s.At == WorldPresetDrinkSpotAt.Stream_bank).Select(s => s.MinStreamDistanceCells + 1).DefaultIfEmpty(0).Max();
            var atBank = map.Cell(o.X, o.Z).Water == WaterKind.None && Enumerable.Range(-bankReach, (2 * bankReach) + 1)
                .SelectMany(dz => Enumerable.Range(-bankReach, (2 * bankReach) + 1).Select(dx => (dx, dz)))
                .Any(d => (d.dx * d.dx) + (d.dz * d.dz) <= bankReach * bankReach && map.Contains(o.X + d.dx, o.Z + d.dz) && (hydrology.Feature(o.X + d.dx, o.Z + d.dz) & WaterFeature.Stream) != 0);
            // Pools in depressions (TL #19990): at least the data depth below the mean ground around them.
            var atHollow = Preset.Water.DrinkSpots.Where(s => s.At == WorldPresetDrinkSpotAt.Hollow).Any(s => InHollow(map, o.X, o.Z, s.HollowRadiusCells!.Value, s.MinHollowDepth!.Value));
            Assert.True(atSpring || atPond || atBank || atHollow, $"seed {seed}: drinking water at ({o.X},{o.Z}) is neither at a spring, a pond edge, a stream bank nor in a hollow");
        }

        // Every walkable spring has its object, and a stream starts there.
        foreach (var (x, z) in springs.Where(s => map.Cell(s.X, s.Z).WalkCost != MapCell.Blocked))
        {
            Assert.Contains(drink, o => o.X == x && o.Z == z);
            Assert.True((hydrology.Feature(x, z) & WaterFeature.Stream) != 0, $"seed {seed}: no stream at the spring ({x},{z})");
            Assert.True(hydrology.Accumulation(x, z) >= Math.Min(Preset.Water.Springs.Discharge, Preset.Water.StreamMinAccumulation));
        }

        // Groundwater springs keep their spacing; the springs at stream heads come on top (TL #19990).
        Assert.True(springs.Count >= Math.Min(1, Preset.Water.Springs.MaxCount));
    }

    /// <summary>
    /// No stream out of nowhere (TL #19990): every running cell that no running cell feeds is a spring or
    /// lies beside a pond or lake (its overflow); on open flat meadow no stream begins.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void EveryStreamBeginsAtASpringOrALake(ulong seed)
    {
        var map = Generate(seed).Map;
        var hydrology = map.Hydrology!;
        var fed = new HashSet<(int, int)>();
        var heads = new List<(int X, int Z)>();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if ((hydrology.Feature(x, z) & WaterFeature.Stream) != 0)
                {
                    var c = map.Cell(x, z);
                    if (c.Flow >= 0)
                    {
                        var (dx, dz) = FlowDirections.All[c.Flow];
                        fed.Add((x + dx, z + dz));
                        heads.Add((x, z));
                    }
                }
            }
        }

        var checkedHeads = 0;
        foreach (var (x, z) in heads.Where(h => !fed.Contains(h)))
        {
            var spring = (hydrology.Feature(x, z) & WaterFeature.Spring) != 0;
            var lake = Enumerable.Range(-1, 3).SelectMany(dz => Enumerable.Range(-1, 3).Select(dx => (dx, dz)))
                .Any(d => map.Contains(x + d.dx, z + d.dz) && (hydrology.Feature(x + d.dx, z + d.dz) & WaterFeature.Lake) != 0);

            // Side cells of a wide stream lie beside a cell of their centre line that carries more water.
            var side = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => map.Contains(x + d.Item1, z + d.Item2)
                && (hydrology.Feature(x + d.Item1, z + d.Item2) & WaterFeature.Stream) != 0 && hydrology.Accumulation(x + d.Item1, z + d.Item2) > hydrology.Accumulation(x, z));
            Assert.True(spring || lake || side, $"seed {seed}: the stream at ({x},{z}) begins out of nowhere");
            checkedHeads++;
        }

        Assert.True(checkedHeads > 0);
    }

    private static bool InHollow(WorldMap map, int x, int z, int radius, int depth)
    {
        long sum = 0;
        var count = 0;
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (map.Contains(x + dx, z + dz))
                {
                    sum += map.Cell(x + dx, z + dz).Height;
                    count++;
                }
            }
        }

        return (map.Cell(x, z).Height + depth) * (long)count <= sum;
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(6UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void EveryStartPairHasTwoDrinkingPlacesNotOneSpring(ulong seed)
    {
        // TL #120 safety: the drink supply (2) is checked per start; with one object per spring, no start
        // depends on a single spring.
        var island = Generate(seed);
        Assert.True(island.Passed);
        var line = Assert.Single(island.Check, c => c.Id == "start_supply/drink");
        Assert.True(line.Reachable >= 2);
        var springs = island.Map.Hydrology!.Springs().ToHashSet();
        Assert.All(island.Objects.GroupBy(o => (o.X, o.Z)), g => Assert.Single(g));
        Assert.All(springs, s => Assert.True(island.Objects.Count(o => (o.X, o.Z) == s) <= 1));
    }

    [Fact]
    public void LakesAreFedHollowsLargeEnough()
    {
        var map = Generate(7).Map;
        var hydrology = map.Hydrology!;
        var lake = new bool[map.WidthCells * map.DepthCells];
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if ((hydrology.Feature(x, z) & WaterFeature.Lake) != 0)
                {
                    lake[(z * map.WidthCells) + x] = true;
                    Assert.Equal(WaterKind.Fresh, map.Cell(x, z).Water);
                    Assert.Equal(-1, map.Cell(x, z).Flow);
                }
            }
        }

        Assert.Contains(true, lake);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void TheUndergroundComesFromTheSeedWithinTheData(ulong seed)
    {
        var island = Generate(seed);
        var underground = Assert.IsType<UndergroundLayer>(island.Map.Underground);
        var data = Preset.Underground;
        var soils = data.SoilRules.Select(r => r.Soil).Append(data.FallbackSoil).ToHashSet(StringComparer.Ordinal);
        Assert.All(underground.SoilIds, id => Assert.Contains(id, soils));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ore = 0;
        for (var z = 0; z < underground.Depth; z++)
        {
            for (var x = 0; x < underground.Width; x++)
            {
                var c = underground.Cell(x, z);
                Assert.InRange(c.StonesPermille, 0, 1000);
                Assert.InRange(c.OrePermille, 0, 1000);
                Assert.True(c.GroundwaterDepth >= 0);
                if (island.Map.Cell(x, z).Water != WaterKind.None)
                {
                    Assert.Equal(0, c.GroundwaterDepth);
                }

                seen.Add(underground.SoilAt(x, z));
                ore += c.OrePermille > 0 ? 1 : 0;
            }
        }

        Assert.True(seen.Count >= 3, $"seed {seed}: soils {string.Join(", ", seen)}");
        Assert.True(ore > 0, $"seed {seed}: no ore");
        Assert.Equal(island.Hash(), Generate(seed).Hash());
    }

    [Fact]
    public void BothLayersAreSavedWithTheIslandAndPartOfItsHash()
    {
        var island = Generate(2026);
        var (decoded, _) = IslandCodec.Decode(IslandCodec.Encode(island, 100));
        Assert.Equal(island.Hash(), decoded.Hash());
        Assert.Equal(IslandCodec.Format, IslandCodec.FormatOf(IslandCodec.Encode(island, 100)));
        var map = decoded.Map;
        Assert.Equal(island.Map.Hydrology!.Springs(), map.Hydrology!.Springs());
        Assert.Equal(island.Map.Underground!.Cell(90, 90), map.Underground!.Cell(90, 90));

        // Without the layers the hash differs: they are part of the world.
        var bare = new GeneratedIsland(island.PresetId, island.GeneratorVersion, island.Seed, island.Attempts, island.Passed, WithoutLayers(island.Map), island.Objects, island.Starts, island.Neighbours, island.Check, island.CliffCells);
        Assert.NotEqual(island.Hash(), bare.Hash());
    }

    [Fact]
    public void AnIslandSavedInFormatOneStillLoadsWithoutLayers()
    {
        var island = Generate(7);
        var bare = new GeneratedIsland(island.PresetId, island.GeneratorVersion, island.Seed, island.Attempts, island.Passed, WithoutLayers(island.Map), island.Objects, island.Starts, island.Neighbours, island.Check, island.CliffCells);
        var formatOne = EncodeFormatOne(bare, 100);
        Assert.Equal(1, IslandCodec.FormatOf(formatOne));
        var (decoded, cellSize) = IslandCodec.Decode(formatOne);
        Assert.Equal(100, cellSize);
        Assert.Null(decoded.Map.Hydrology);
        Assert.Null(decoded.Map.Underground);
        Assert.Equal(bare.Hash(), decoded.Hash());
    }

    internal static WorldMap WithoutLayers(WorldMap map)
    {
        var builder = new WorldMapBuilder(map.ChunkCells, map.ChunksX, map.ChunksZ, map.BiomeIds);
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                builder.Set(x, z, map.Cell(x, z));
            }
        }

        return builder.Build();
    }

    /// <summary>The island blob as builds before the layers wrote it (format 1).</summary>
    internal static byte[] EncodeFormatOne(GeneratedIsland island, int cellSizeCm)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var w = new BinaryWriter(deflate, Encoding.UTF8))
        {
            w.Write("GWIS"u8.ToArray());
            w.Write(1);
            w.Write(island.PresetId);
            w.Write(island.GeneratorVersion);
            w.Write(island.Seed);
            w.Write(island.Attempts);
            w.Write(island.Passed);
            w.Write(island.CliffCells);
            w.Write(cellSizeCm);
            var map = island.Map;
            w.Write(map.ChunkCells);
            w.Write(map.ChunksX);
            w.Write(map.ChunksZ);
            w.Write(map.BiomeIds.Count);
            foreach (var biome in map.BiomeIds)
            {
                w.Write(biome);
            }

            for (var z = 0; z < map.DepthCells; z++)
            {
                for (var x = 0; x < map.WidthCells; x++)
                {
                    var c = map.Cell(x, z);
                    w.Write(c.Height);
                    w.Write((byte)c.Water);
                    w.Write(c.WaterDepth);
                    w.Write(c.Flow);
                    w.Write(c.Moisture);
                    w.Write(c.Temperature);
                    w.Write(c.Biome);
                    w.Write(c.WalkCost);
                }
            }

            w.Write(island.Objects.Count);
            foreach (var o in island.Objects)
            {
                w.Write(o.TypeId);
                w.Write(o.X);
                w.Write(o.Z);
            }

            w.Write(island.Starts.Count);
            foreach (var s in island.Starts)
            {
                w.Write(s.Pair);
                w.Write(s.X);
                w.Write(s.Z);
            }

            w.Write(island.Neighbours.Count);
            foreach (var n in island.Neighbours)
            {
                w.Write(n.X);
                w.Write(n.Z);
                w.Write(n.GroupSize);
            }

            w.Write(island.Check.Count);
            foreach (var c in island.Check)
            {
                w.Write(c.Id);
                w.Write(c.Reachable);
                w.Write(c.Total);
                w.Write(c.Min);
                w.Write(c.Max);
            }
        }

        return buffer.ToArray();
    }
}
