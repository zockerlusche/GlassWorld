using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Headless.Tests;

/// <summary>
/// The <c>worldgen</c> command (B2 R12) as evidence for the acceptance of B2: it reports an island that
/// fails its mandatory check, gives the same world hash in separate processes, and prints a seed range as
/// one JSON line per seed (what <c>tools/worldgen/seed_series.py</c> reads).
/// </summary>
public sealed class WorldGenCommandTests : IDisposable
{
    private const string Seed7Hash = "8ee0a29fa9f44ab10a05be02b80a8909bde8fc5f942efedac99169ee20c0701c";
    private static readonly string Content = ContentFolder.Find();
    private static readonly string[] Biomes = ["land"];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gw-worldgen-" + Guid.NewGuid().ToString("N"));

    public WorldGenCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static (int Code, string[] Lines) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = WorldGenCommand.Run(args, output);
        return (code, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Counter-check (B2 acceptance: "a preset without flint fails the mandatory check and is reported"):
    /// with a copy of the content whose spawn table places no flint - the only spark striker of the
    /// catalogue - the command ends with a non-zero exit code, says that the island did not pass after all
    /// its attempts, and its one FAIL line names the check that failed.
    /// </summary>
    [Fact]
    public void AnIslandWithoutFlintIsReportedWithAFailLineAndANonZeroExitCode()
    {
        var copy = Path.Combine(_folder, "content");
        foreach (var file in Directory.GetFiles(Content, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Content, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var path = Path.Combine(copy, "world", "spawns.json");
        var table = JsonNode.Parse(File.ReadAllText(path))!;
        var rules = table["spawns"]!.AsArray();
        var flint = rules.Where(r => r!["object_type"]!.GetValue<string>() == "flint").ToList();
        Assert.NotEmpty(flint);
        foreach (var rule in flint)
        {
            rules.Remove(rule);
        }

        File.WriteAllText(path, table.ToJsonString());
        var attempts = ContentSet.Load(copy).WorldGen.Presets["start_island"].Start.MaxAttempts;

        var (code, lines) = Run("--seed", "42", "--content", copy);
        Assert.Equal(ExitCodes.AcceptanceFailed, code);
        Assert.Contains(lines, l => l.StartsWith($"passed False attempts {attempts} ", StringComparison.Ordinal));
        var fail = Assert.Single(lines, l => l.EndsWith(" FAIL", StringComparison.Ordinal));
        Assert.StartsWith("check spark_striker ", fail, StringComparison.Ordinal);
        Assert.Matches("reachable +0 total +0 \\(min 1, ", fail);
        Assert.DoesNotContain(lines, l => l.StartsWith("objects ", StringComparison.Ordinal) && l.Contains("flint", StringComparison.Ordinal));

        // The text form carries the hydrology figure of the seed series too.
        Assert.Matches(" stream dead ends [0-9]+$", Assert.Single(lines, l => l.StartsWith("hydrology ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// B2 acceptance 1: the same seed gives the same world hash in two processes. The built headless
    /// program is started twice, one after the other; both print the hash this process computes too, which
    /// is the golden hash of the seed (WorldGeneratorTests, the same on Windows and Linux).
    /// </summary>
    [Fact]
    public void TheSameSeedGivesTheSameHashInTwoProcesses()
    {
        var first = InOwnProcess("worldgen", "--seed", "7", "--json", "--content", Content);
        var second = InOwnProcess("worldgen", "--seed", "7", "--json", "--content", Content);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.NotEqual(Environment.ProcessId, first.ProcessId);
        Assert.Equal((0, 0), (first.Code, second.Code));
        var hashes = new[] { first, second }.Select(p => JsonDocument.Parse(p.Output).RootElement.GetProperty("hash").GetString()).ToList();
        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(Seed7Hash, hashes[0]);

        var content = ContentSet.Load(Content);
        Assert.Equal(hashes[0], WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7).Hash());
    }

    [Fact]
    public void ASeedInARangeGivesTheSameJsonLineAsTheSeedAlone()
    {
        var (code, lines) = Run("--seeds", "7-8", "--json", "--content", Content);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(2, lines.Length);
        var reports = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal([7UL, 8UL], reports.Select(r => r.GetProperty("seed").GetUInt64()));
        Assert.Equal(Seed7Hash, reports[0].GetProperty("hash").GetString());
        Assert.NotEqual(Seed7Hash, reports[1].GetProperty("hash").GetString());
        Assert.All(reports, r =>
        {
            Assert.True(r.GetProperty("passed").GetBoolean());
            Assert.True(r.GetProperty("hydrology").GetProperty("stream_cells").GetInt32() > 0);
            Assert.Equal(0, r.GetProperty("hydrology").GetProperty("stream_dead_ends").GetInt32());
            Assert.Contains(r.GetProperty("check").EnumerateArray(), c => c.GetProperty("id").GetString() == "start_dry_ground");
        });

        // A range loads the content once and generates one island after the other with it. Nothing of
        // an island may leak into the next: seed 8, generated after seed 7 in the range, gives the very
        // line of seed 8 generated alone - the hash and every figure; only the measured time differs.
        var (alone, single) = Run("--seed", "8", "--json", "--content", Content);
        Assert.Equal(ExitCodes.Ok, alone);
        Assert.Equal(WithoutTime(Assert.Single(single)), WithoutTime(lines[1]));
        Assert.NotEqual(WithoutTime(lines[0]), WithoutTime(lines[1]));
    }

    /// <summary>A JSON line of the command without its one measured value, the generation time.</summary>
    private static string WithoutTime(string line)
    {
        var report = JsonNode.Parse(line)!.AsObject();
        Assert.True(report.Remove("time_ms"));
        return report.ToJsonString();
    }

    [Theory]
    [InlineData("8-7")]
    [InlineData("7")]
    [InlineData("a-b")]
    [InlineData("1-2-3")]
    [InlineData("-3")]
    public void ABadSeedRangeIsRefused(string range) =>
        Assert.Throws<ArgumentException>(() => Run("--seeds", range, "--content", Content));

    [Fact]
    public void AMapPictureNeedsASingleSeed() =>
        Assert.Throws<ArgumentException>(() => Run("--seeds", "1-2", "--png", Path.Combine(_folder, "map.png"), "--content", Content));

    /// <summary>
    /// The figure the seed series reads for "every stream ends in the sea or a lake": a stream that ends on
    /// dry land, runs into still water or in a circle counts with every cell of it; a stream into the sea
    /// or a lake, and the still side cell of a running stream, do not.
    /// </summary>
    [Fact]
    public void StreamDeadEndsCountsTheStreamCellsWhoseWaterReachesNeitherSeaNorLake()
    {
        // Row 0: a stream east into the sea. Row 2: a stream east into a lake, with a still side cell
        // (row 3) beside it. Row 5: a stream east that ends on dry land. Row 7: two cells that run into each
        // other. Row 9: a still stream cell on its own.
        const int e = 0, w = 4;
        var cells = new List<(int X, int Z, WaterKind Water, sbyte Flow, WaterFeature Feature)>
        {
            (1, 0, WaterKind.Fresh, e, WaterFeature.Stream), (2, 0, WaterKind.Fresh, e, WaterFeature.Stream), (3, 0, WaterKind.Salt, -1, WaterFeature.None),
            (1, 2, WaterKind.Fresh, e, WaterFeature.Stream | WaterFeature.Spring), (2, 2, WaterKind.Fresh, e, WaterFeature.Stream), (3, 2, WaterKind.Fresh, -1, WaterFeature.Lake),
            (2, 3, WaterKind.Fresh, -1, WaterFeature.Stream),
            (1, 5, WaterKind.Fresh, e, WaterFeature.Stream), (2, 5, WaterKind.Fresh, e, WaterFeature.Stream),
            (1, 7, WaterKind.Fresh, e, WaterFeature.Stream), (2, 7, WaterKind.Fresh, w, WaterFeature.Stream),
            (5, 9, WaterKind.Fresh, -1, WaterFeature.Stream),
        };
        Assert.Equal(0, WorldGenCommand.StreamDeadEnds(Map(cells.Where(c => c.Z < 5))));
        Assert.Equal(2, WorldGenCommand.StreamDeadEnds(Map(cells.Where(c => c.Z < 6))));
        Assert.Equal(4, WorldGenCommand.StreamDeadEnds(Map(cells.Where(c => c.Z < 8))));
        Assert.Equal(5, WorldGenCommand.StreamDeadEnds(Map(cells)));

        // Without the hydrology layer (islands saved in format 1) there is nothing to count.
        var bare = new WorldMapBuilder(8, 1, 2, Biomes);
        Assert.Equal(0, WorldGenCommand.StreamDeadEnds(bare.Build()));
    }

    private static WorldMap Map(IEnumerable<(int X, int Z, WaterKind Water, sbyte Flow, WaterFeature Feature)> cells)
    {
        const int width = 8, depth = 16;
        var builder = new WorldMapBuilder(8, 1, 2, Biomes);
        var features = new WaterFeature[width * depth];
        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                builder.Set(x, z, new MapCell(30, WaterKind.None, 0, -1, 500, 150, 0, 1000));
            }
        }

        foreach (var (x, z, water, flow, feature) in cells)
        {
            builder.Set(x, z, new MapCell(30, water, 2, flow, 1000, 150, 0, 3000));
            features[(z * width) + x] = feature;
        }

        builder.Hydrology = new HydrologyLayer(width, depth, new int[width * depth], new int[width * depth], features);
        return builder.Build();
    }

    /// <summary>
    /// Starts the built headless program (it lies beside this test assembly) as a process of its own with
    /// <c>dotnet</c> and returns its exit code, standard output and process id.
    /// </summary>
    private static (int Code, string Output, int ProcessId) InOwnProcess(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "GlassWorld.Simulation.Headless.dll");
        Assert.True(File.Exists(dll), $"{dll} not found");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var id = process.Id;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            // Our own child only: it must not outlive the test.
            process.Kill(entireProcessTree: true);
            Assert.Fail("worldgen did not end within three minutes");
        }

        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"worldgen ended with exit code {process.ExitCode}: {errors.Result}");
        return (process.ExitCode, output.Result, id);
    }
}
