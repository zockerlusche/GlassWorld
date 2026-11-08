using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// <c>worldgen --seed N [--preset P] [--content DIR] [--json] [--png FILE]</c> (B2 R12): generates the
/// start island and prints the world hash and key figures (biome shares, mandatory check, attempts,
/// time). <c>--json</c> is machine-readable for GW2's seed series; <c>--png</c> writes an overview map
/// with the biome colours (own minimal PNG writer, no extra library).
/// </summary>
internal static class WorldGenCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static int Run(string[] args)
    {
        ulong seed = 1;
        var preset = "start_island";
        var content = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "content");
        var json = false;
        string? png = null;
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
            island.Neighbours.Count);
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
        }

        if (png is not null)
        {
            WritePng(png, island, set.WorldGen.Biomes);
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
        int Neighbours);
}
