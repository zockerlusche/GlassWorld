using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// <c>worldgen --seed N [--preset P] [--content DIR] [--json] [--png FILE] [--png-flow FILE]</c> (B2 R12):
/// generates the start island and prints the world hash and key figures (biome shares, mandatory check,
/// attempts, time, hydrology, underground, start ground against the highest tide). <c>--json</c> is machine-readable for GW2's seed series;
/// <c>--png</c> writes an overview map with the biome colours, <c>--png-flow</c> a map of the drainage
/// (hill shade, flow accumulation, streams, lakes, springs; own minimal PNG writer, no extra library).
/// </summary>
internal static class WorldGenCommand
{
    // Step-stack metric window (W1726): a one-step edge counts as stacked when another follows within it.
    private const int StackWindowCells = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static int Run(string[] args)
    {
        ulong seed = 1;
        var preset = "start_island";
        var content = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "content");
        var json = false;
        string? png = null;
        string? pngFlow = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--preset":
                    preset = args[++i];
                    break;
                case "--content":
                    content = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--png":
                    png = args[++i];
                    break;
                case "--png-flow":
                    pngFlow = args[++i];
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        var set = ContentSet.Load(Path.GetFullPath(content));
        var watch = Stopwatch.StartNew();
        var island = WorldGenerator.Generate(set.WorldGen, set.Objects, preset, seed);
        watch.Stop();
        var map = island.Map;
        var cells = map.WidthCells * map.DepthCells;
        var shares = new int[map.BiomeIds.Count];
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                shares[map.Cell(x, z).Biome]++;
            }
        }

        var report = new Report(
            seed,
            preset,
            island.GeneratorVersion,
            island.Hash(),
            island.Passed,
            island.Attempts,
            watch.ElapsedMilliseconds,
            map.BiomeIds.Select((id, i) => new KeyValuePair<string, int>(id, shares[i] * 1000 / cells)).ToDictionary(),
            map.BiomeIds.Select((id, i) => new KeyValuePair<string, int>(id, shares[i])).ToDictionary(),
            island.Check.Select(c => new CheckLine(c.Id, c.Reachable, c.Total, c.Min, c.Max, c.Passed, c.ReportOnly)).ToList(),
            island.Objects.GroupBy(o => o.TypeId).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            island.Starts.Select(s => new[] { s.Pair, s.X, s.Z }).ToList(),
            island.CliffCells,
            island.Neighbours.Count,
            Water(island, set),
            Ground(island),
            Relief(island),
            Faces(island, set.WorldGen.Presets[preset]),
            StartGround(island, set.WorldGen.Presets[preset]));
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"worldgen seed {seed} preset {preset} v{island.GeneratorVersion} hash {report.Hash}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  passed {island.Passed} attempts {island.Attempts} time {watch.ElapsedMilliseconds} ms cliffs {island.CliffCells} neighbours {island.Neighbours.Count}"));
            Console.WriteLine("  biomes ‰ " + string.Join(", ", report.BiomesPermille.Select(b => $"{b.Key} {b.Value}")));
            Console.WriteLine("  biome cells " + string.Join(", ", report.BiomeCells.Select(b => $"{b.Key} {b.Value}")));
            foreach (var c in island.Check)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  check {c.Id,-14} reachable {c.Reachable,4} total {c.Total,4} (min {c.Min}, max {c.Max}) {Verdict(c)}"));
            }

            Console.WriteLine("  objects " + string.Join(", ", report.Objects.Select(o => $"{o.Key} {o.Value}")));
            var h = report.Hydrology;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  hydrology springs {h.Springs} stream cells {h.StreamCells} mouths {h.Mouths} lakes {h.Lakes} (cells {h.LakeCells}, largest {h.LargestLake}) max accumulation {h.MaxAccumulation} blocked {h.BlockedCells}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  drink per start [{string.Join(", ", h.DrinkPerStart)}] springs within radius per start [{string.Join(", ", h.SpringsPerStart)}]"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  relief land cells {report.Relief.LandCells} height p10 {report.Relief.P10} p50 {report.Relief.P50} p90 {report.Relief.P90} max {report.Relief.Max} mean slope x100 {report.Relief.MeanSlopeX100} steep cells (slope >= 5) {report.Relief.SteepCells}"));
            var f = report.Faces;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  faces one-steps {f.OneSteps} stacked {f.StackedSteps} runs {f.StackRuns} (rock {f.RockStackedSteps} / {f.RockStackRuns}) max drop {f.MaxFaceDrop} caves {f.Caves} reachable {f.CavesReachable}"));
            var g = report.StartGround;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  start ground above the highest tide (tenths, line {g.HighTideTenths}) [{string.Join(", ", g.AboveHighTideTenths)}] dry with surroundings [{string.Join(", ", g.Dry)}] pair distance cm [{string.Join(", ", g.PairDistanceCm)}]"));
            Console.WriteLine("  underground soils " + string.Join(", ", report.Underground.Soils.Select(s => $"{s.Key} {s.Value}")) + string.Create(CultureInfo.InvariantCulture, $"; ore cells {report.Underground.OreCells}; groundwater at surface {report.Underground.SurfaceWaterCells}"));
        }

        if (png is not null)
        {
            WritePng(png, island, set.WorldGen.Biomes);
        }

        if (pngFlow is not null)
        {
            WriteFlowPng(pngFlow, island);
        }

        return island.Passed ? 0 : 2;
    }

    // Report-only lines (start supply) never fail the island, so they read ok / short instead of FAIL.
    private static string Verdict(MandatoryResult c) => c.ReportOnly ? (c.Passed ? "ok (report only)" : "short (report only)") : (c.Passed ? "ok" : "FAIL");

    private static void WritePng(string path, GeneratedIsland island, Contracts.BiomeTable biomes)
    {
        var map = island.Map;
        var colors = map.BiomeIds.Select(id => biomes.Biomes.Single(b => b.Id == id).MapColor).Select(Rgb).ToArray();
        int w = map.WidthCells, h = map.DepthCells;
        var raw = new byte[h * ((w * 3) + 1)];
        var objects = island.Objects.Select(o => (o.X, o.Z)).ToHashSet();
        var starts = island.Starts.Select(s => (s.X, s.Z)).ToHashSet();
        for (var z = 0; z < h; z++)
        {
            var row = z * ((w * 3) + 1);
            for (var x = 0; x < w; x++)
            {
                var c = map.Cell(x, z);
                var (r, g, b) = colors[c.Biome];
                var shade = 70 + (Math.Clamp(c.Height, 0, 200) * 30 / 200);
                (r, g, b) = (r * shade / 100, g * shade / 100, b * shade / 100);
                if (c.Water == WaterKind.Salt)
                {
                    (r, g, b) = c.WaterDepth > 8 ? (47, 127, 166) : (111, 182, 217);
                }

                if (objects.Contains((x, z)))
                {
                    (r, g, b) = (60, 45, 35);
                }

                if (starts.Contains((x, z)))
                {
                    (r, g, b) = (230, 40, 40);
                }

                raw[row + 1 + (x * 3)] = (byte)Math.Clamp(r, 0, 255);
                raw[row + 2 + (x * 3)] = (byte)Math.Clamp(g, 0, 255);
                raw[row + 3 + (x * 3)] = (byte)Math.Clamp(b, 0, 255);
            }
        }

        SavePng(path, raw, w, h);
    }

    private static void SavePng(string path, byte[] raw, int w, int h)
    {
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BigEndian(header, 0, (uint)w);
        BigEndian(header, 4, (uint)h);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    /// <summary>Key figures of the drainage, including the drink each start finds within the supply radius.</summary>
    private static WaterReport Water(GeneratedIsland island, ContentSet set)
    {
        var map = island.Map;
        if (map.Hydrology is not { } h)
        {
            return new WaterReport(0, 0, 0, 0, 0, 0, 0, 0, [], []);
        }

        int streams = 0, mouths = 0, lakeCells = 0, max = 0, blocked = 0;
        var lake = new bool[map.WidthCells * map.DepthCells];
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var c = map.Cell(x, z);
                blocked += c.WalkCost == MapCell.Blocked && c.Water == WaterKind.None ? 1 : 0;
                max = Math.Max(max, c.Water == WaterKind.Salt ? 0 : h.Accumulation(x, z));
                var f = h.Feature(x, z);
                if ((f & WaterFeature.Lake) != 0)
                {
                    lakeCells++;
                    lake[(z * map.WidthCells) + x] = true;
                }

                if ((f & WaterFeature.Stream) != 0)
                {
                    streams++;
                    if (c.Flow >= 0)
                    {
                        var (dx, dz) = FlowDirections.All[c.Flow];
                        mouths += map.Cell(x + dx, z + dz).Water == WaterKind.Salt ? 1 : 0;
                    }
                }
            }
        }

        var sizes = Groups(lake, map.WidthCells, map.DepthCells);
        var preset = set.WorldGen.Presets[island.PresetId];
        var radius = preset.Start.Supply.RadiusCells;
        var springs = h.Springs().ToList();
        bool Near(StartPosition s, int x, int z) => ((x - s.X) * (x - s.X)) + ((z - s.Z) * (z - s.Z)) <= radius * radius;
        var drink = island.Starts.Select(s => island.Objects.Count(o => Near(s, o.X, o.Z) && set.Objects.HasProperty(o.TypeId, "drinkable") && !set.Objects.HasProperty(o.TypeId, "salty"))).ToList();
        var springsNear = island.Starts.Select(s => springs.Count(c => Near(s, c.X, c.Z))).ToList();
        return new WaterReport(springs.Count, streams, mouths, sizes.Count, lakeCells, sizes.Count == 0 ? 0 : sizes.Max(), max, blocked, drink, springsNear);
    }

    /// <summary>How hilly the land is: height percentiles above the sea and the mean slope.</summary>
    /// <summary>
    /// Rock faces and step stacks (W1726): one-step edges, stacked ones (another within
    /// <see cref="StackWindowCells"/> cells further down the same way), on all land and on the cliff biome,
    /// the highest drop, and the caves with those the first start can walk to.
    /// </summary>
    private static FacesReport Faces(GeneratedIsland island, Contracts.WorldPreset preset)
    {
        var step = preset.Terrain.CliffMinDrop;
        var all = WorldGenerator.StepStacks(island.Map, step, StackWindowCells, null);
        var rock = WorldGenerator.StepStacks(island.Map, step, StackWindowCells, preset.Caves.CliffBiome);
        var caves = island.Objects.Where(o => o.TypeId == preset.Caves.ObjectType).ToList();
        var reach = island.Starts.Count == 0 ? null : WorldGenerator.Reachable(island.Map, island.Starts[0].X, island.Starts[0].Z);
        var reachable = reach is null ? 0 : caves.Count(c => reach[(c.Z * island.Map.WidthCells) + c.X]);
        return new FacesReport(all.OneSteps, all.StackedSteps, all.StackRuns, rock.StackedSteps, rock.StackRuns, all.MaxFaceDrop, caves.Count, reachable);
    }

    /// <summary>
    /// The start ground against the tide (TL #20750): the high-tide line (mean sea level plus the highest
    /// tide, in tenths of a height unit), per start how far its ground lies above it and whether it and
    /// its surroundings are dry by <c>start.dry_ground</c> (true without the data), and per pair the
    /// distance between its two start cells.
    /// </summary>
    private static StartGroundReport StartGround(GeneratedIsland island, Contracts.WorldPreset preset)
    {
        var high = (preset.Terrain.SeaLevel * 10) + new Tides(preset.Tides).HighestLevelTenths;
        var distances = island.Starts.GroupBy(s => s.Pair).OrderBy(p => p.Key).Select(p => p.ToList()).Where(p => p.Count == 2)
            .Select(p => (int)Math.Round(Math.Sqrt(((p[0].X - p[1].X) * (p[0].X - p[1].X)) + ((p[0].Z - p[1].Z) * (p[0].Z - p[1].Z))) * preset.Grid.CellSizeCm));
        return new StartGroundReport(
            high,
            [.. island.Starts.Select(s => (island.Map.Cell(s.X, s.Z).Height * 10) - high)],
            [.. island.Starts.Select(s => island.Map.Cell(s.X, s.Z).Water == WaterKind.None && WorldGenerator.OnDryStartGround(preset, island.Map, s.X, s.Z))],
            [.. distances]);
    }

    private static ReliefReport Relief(GeneratedIsland island)
    {
        var map = island.Map;
        var heights = new List<int>();
        long slopes = 0;
        var steep = 0;
        for (var z = 1; z < map.DepthCells - 1; z++)
        {
            for (var x = 1; x < map.WidthCells - 1; x++)
            {
                var c = map.Cell(x, z);
                if (c.Water == WaterKind.Salt)
                {
                    continue;
                }

                heights.Add(c.Height);
                var slope = new[] { map.Cell(x + 1, z), map.Cell(x - 1, z), map.Cell(x, z + 1), map.Cell(x, z - 1) }.Max(n => Math.Abs(n.Height - c.Height));
                slopes += slope;
                steep += slope >= 5 ? 1 : 0;
            }
        }

        heights.Sort();
        int At(int permille) => heights.Count == 0 ? 0 : heights[Math.Min(heights.Count - 1, heights.Count * permille / 1000)];
        return new ReliefReport(heights.Count, At(100), At(500), At(900), heights.Count == 0 ? 0 : heights[^1], heights.Count == 0 ? 0 : (int)(slopes * 100 / heights.Count), steep);
    }

    private static GroundReport Ground(GeneratedIsland island)
    {
        if (island.Map.Underground is not { } u)
        {
            return new GroundReport([], 0, 0);
        }

        var soils = new Dictionary<string, int>(StringComparer.Ordinal);
        int ore = 0, surface = 0;
        for (var z = 0; z < u.Depth; z++)
        {
            for (var x = 0; x < u.Width; x++)
            {
                if (island.Map.Cell(x, z).Water == WaterKind.Salt)
                {
                    continue;
                }

                var c = u.Cell(x, z);
                soils[u.SoilIds[c.Soil]] = soils.GetValueOrDefault(u.SoilIds[c.Soil]) + 1;
                ore += c.OrePermille > 0 ? 1 : 0;
                surface += c.GroundwaterDepth == 0 && island.Map.Cell(x, z).Water == WaterKind.None ? 1 : 0;
            }
        }

        return new GroundReport(soils, ore, surface);
    }

    private static List<int> Groups(bool[] marked, int w, int d)
    {
        var seen = new bool[marked.Length];
        var sizes = new List<int>();
        for (var i = 0; i < marked.Length; i++)
        {
            if (!marked[i] || seen[i])
            {
                continue;
            }

            var size = 0;
            var queue = new Queue<int>([i]);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                size++;
                foreach (var (dx, dz) in FlowDirections.All)
                {
                    int nx = (c % w) + dx, nz = (c / w) + dz;
                    if (nx >= 0 && nz >= 0 && nx < w && nz < d && marked[(nz * w) + nx] && !seen[(nz * w) + nx])
                    {
                        seen[(nz * w) + nx] = true;
                        queue.Enqueue((nz * w) + nx);
                    }
                }
            }

            sizes.Add(size);
        }

        return sizes;
    }

    /// <summary>Drainage map: hill shade, flow accumulation (log scale, blue), lakes, springs (red), starts (yellow).</summary>
    private static void WriteFlowPng(string path, GeneratedIsland island)
    {
        var map = island.Map;
        int w = map.WidthCells, hgt = map.DepthCells;
        var raw = new byte[hgt * ((w * 3) + 1)];
        var springs = map.Hydrology?.Springs().ToHashSet() ?? [];
        var starts = island.Starts.Select(s => (s.X, s.Z)).ToHashSet();
        var drink = island.Objects.Where(o => o.TypeId == "spring_water").Select(o => (o.X, o.Z)).ToHashSet();
        for (var z = 0; z < hgt; z++)
        {
            var row = z * ((w * 3) + 1);
            for (var x = 0; x < w; x++)
            {
                var c = map.Cell(x, z);
                int r, g, b;
                if (c.Water == WaterKind.Salt)
                {
                    (r, g, b) = (30, 60, 110);
                }
                else
                {
                    // Hill shade from the west-north-west plus height.
                    var west = map.Cell(Math.Max(0, x - 1), z).Height;
                    var north = map.Cell(x, Math.Max(0, z - 1)).Height;
                    var light = Math.Clamp(150 + ((c.Height - west) * 12) + ((c.Height - north) * 8) + (c.Height / 3), 40, 250);
                    (r, g, b) = (light, light, light * 95 / 100);
                    if (c.WalkCost == MapCell.Blocked && c.Water == WaterKind.None)
                    {
                        (r, g, b) = (light * 80 / 100, light * 60 / 100, light * 55 / 100);
                    }

                    var acc = map.Hydrology?.Accumulation(x, z) ?? 0;
                    var f = map.Hydrology?.Feature(x, z) ?? WaterFeature.None;
                    if ((f & WaterFeature.Lake) != 0)
                    {
                        (r, g, b) = (40, 110, 200);
                    }
                    else if ((f & WaterFeature.Stream) != 0)
                    {
                        (r, g, b) = (20, 90, 230);
                    }
                    else if (c.Water == WaterKind.Fresh)
                    {
                        (r, g, b) = (90, 140, 120);
                    }
                    else if (acc >= 30)
                    {
                        var t = Math.Clamp((int)((Math.Log(acc) - 3.4) * 45), 0, 200);
                        (r, g, b) = (r * (255 - t) / 255, (g * (255 - t) / 255) + (t * 120 / 255), (b * (255 - t) / 255) + t);
                    }
                }

                if (drink.Contains((x, z)))
                {
                    (r, g, b) = (0, 230, 230);
                }

                if (springs.Contains((x, z)))
                {
                    (r, g, b) = (240, 30, 30);
                }

                if (starts.Contains((x, z)))
                {
                    (r, g, b) = (255, 230, 0);
                }

                raw[row + 1 + (x * 3)] = (byte)Math.Clamp(r, 0, 255);
                raw[row + 2 + (x * 3)] = (byte)Math.Clamp(g, 0, 255);
                raw[row + 3 + (x * 3)] = (byte)Math.Clamp(b, 0, 255);
            }
        }

        SavePng(path, raw, w, hgt);
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = new byte[4];
        BigEndian(crc, 0, Crc32([.. typeBytes, .. data]));
        stream.Write(crc);
    }

    private static void BigEndian(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed record WaterReport(int Springs, int StreamCells, int Mouths, int Lakes, int LakeCells, int LargestLake, int MaxAccumulation, int BlockedCells, List<int> DrinkPerStart, List<int> SpringsPerStart);

    private sealed record ReliefReport(int LandCells, int P10, int P50, int P90, int Max, int MeanSlopeX100, int SteepCells);

    private sealed record FacesReport(int OneSteps, int StackedSteps, int StackRuns, int RockStackedSteps, int RockStackRuns, int MaxFaceDrop, int Caves, int CavesReachable);

    private sealed record GroundReport(Dictionary<string, int> Soils, int OreCells, int SurfaceWaterCells);

    private sealed record StartGroundReport(int HighTideTenths, List<int> AboveHighTideTenths, List<bool> Dry, List<int> PairDistanceCm);

    private sealed record CheckLine(string Id, int Reachable, int Total, int Min, int Max, bool Passed, bool ReportOnly);

    private sealed record Report(
        ulong Seed,
        string Preset,
        int GeneratorVersion,
        string Hash,
        bool Passed,
        int Attempts,
        long TimeMs,
        Dictionary<string, int> BiomesPermille,
        Dictionary<string, int> BiomeCells,
        List<CheckLine> Check,
        Dictionary<string, int> Objects,
        List<int[]> Starts,
        int CliffCells,
        int Neighbours,
        WaterReport Hydrology,
        GroundReport Underground,
        ReliefReport Relief,
        FacesReport Faces,
        StartGroundReport StartGround);
}
