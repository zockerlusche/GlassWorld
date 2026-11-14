using System.IO.Compression;
using System.Text.Json;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The technical run trace (A1719, B2767/B2768): one JSON-lines file per game day next to the save with every core
/// event, every AI log entry and an hourly snapshot. It reads along only - the world runs to the same hash with and
/// without it - and packs the days before yesterday.
/// </summary>
public sealed class RunTraceTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glassworld-trace-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ThreeDaysLeaveEventsAiLinesAndHourlySnapshotsAndTheHashOfAWorldWithoutTheTrace()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 21, new MockDecisionPort(), "de");
        var without = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 21, new MockDecisionPort(), "de");
        // Into the first ticks of day 3: day 1 is packed then, days 2 and 3 are plain.
        var ticks = (2 * host.World.Calendar.TicksPerDay) + 2;
        using (var trace = RunTraceWriter.Start(host, _folder, ContentDirectory, force: true))
        {
            Assert.NotNull(trace);
            for (var i = 0; i < ticks; i++)
            {
                host.World.Step();
                without.World.Step();
            }
        }

        Assert.Equal(without.World.StateHash(), host.World.StateHash());
        var folder = Path.Combine(_folder, "trace");
        Assert.True(File.Exists(Path.Combine(folder, "day-0001.jsonl.gz")), "day 1 is packed once day 3 began");
        Assert.True(File.Exists(Path.Combine(folder, "day-0003.jsonl")));

        var lines = ReadDay(Path.Combine(folder, "day-0002.jsonl"));
        var types = lines.GroupBy(l => l.GetProperty("t").GetString()!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Assert.Equal(24, types["snapshot"].Count);
        Assert.Contains(types["event"], l => l.GetProperty("kind").GetString() == "action_started" && l.TryGetProperty("at", out _));
        Assert.Contains(types["ai"], l => l.GetProperty("kind").GetString() == "Request" && l.GetProperty("options").GetArrayLength() > 0);
        Assert.Contains(types["ai"], l => l.GetProperty("kind").GetString() == "Response" && l.TryGetProperty("option_id", out _));
        var snapshot = types["snapshot"][0];
        Assert.Equal(host.Inhabitants.All.Count(), snapshot.GetProperty("inhabitants").GetArrayLength());
        Assert.StartsWith("2/", snapshot.GetProperty("clock").GetString(), StringComparison.Ordinal);
        var perDay = host.World.Calendar.TicksPerDay;
        Assert.All(types["snapshot"], l => Assert.InRange(l.GetProperty("tick").GetInt64(), perDay, (2 * perDay) - 1));
        Assert.All(lines, l => Assert.True(l.GetProperty("tick").GetInt64() < 2 * perDay));

        using var packed = new GZipStream(File.OpenRead(Path.Combine(folder, "day-0001.jsonl.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(packed);
        Assert.Contains("\"t\":\"snapshot\"", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDataSwitchDecidesUnlessARunForcesIt()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var settings = RunTraceWriter.LoadSettings(ContentDirectory)!;
        using var trace = RunTraceWriter.Start(host, _folder, ContentDirectory);
        Assert.Equal(settings.Enabled, trace is not null);
        using var forced = RunTraceWriter.Start(host, Path.Combine(_folder, "forced"), ContentDirectory, force: true);
        Assert.NotNull(forced);

        // A reader (the query tool) may read the day while the game still writes it.
        forced.Note(0, "sound", new Dictionary<string, string?> { ["source"] = "inh-000001", ["sound"] = "action.drink" });
        forced.Flush();
        using var reading = new FileStream(Path.Combine(forced.Folder, "day-0001.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(reading);
        Assert.Contains("\"t\":\"sound\"", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    private static List<JsonElement> ReadDay(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
}
