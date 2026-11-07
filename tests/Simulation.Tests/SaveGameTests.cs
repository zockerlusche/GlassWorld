using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;
using Microsoft.Data.Sqlite;

namespace GlassWorld.Simulation.Tests;

public sealed class SaveGameTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public SaveGameTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void HashBeforeSavingEqualsHashAfterLoadingAndRunsOnIdentically()
    {
        var original = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 21, new MockDecisionPort(), "de");
        original.World.Run(2 * original.World.Calendar.TicksPerDay + 333);
        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));

        var loaded = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        // Answers that arrive after the save must be the same in both runs: the original keeps its mock,
        // the loaded run replays the original's log for the time after the save.
        original.World.Run(original.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void SavingTwiceReplacesTheFileAndLeavesNoTemporaryCopy()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        host.World.Run(100);
        SaveGameStore.Save(path, StateCodec.Capture(host));

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(100, SaveGameStore.Load(path).Tick);
        Assert.Equal(SaveGameStore.SchemaVersion, SaveGameStore.VersionOf(path));
    }

    [Fact]
    public void NewerSchemaIsRefused()
    {
        var path = Path.Combine(_directory, "future.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => SaveGameStore.Load(path));
    }

    [Fact]
    public void FilesThatAreNoSaveGameStayUntouched()
    {
        // GW1 review #50 K2: loading must not migrate an empty or foreign file.
        var empty = Path.Combine(_directory, "empty.sqlite");
        File.WriteAllBytes(empty, []);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Load(empty));
        Assert.Equal(0, new FileInfo(empty).Length);

        var foreign = Path.Combine(_directory, "foreign.sqlite");
        using (var c = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE notes(text TEXT);";
            cmd.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(foreign);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Load(foreign));
        Assert.Equal(before, File.ReadAllBytes(foreign));
    }

    [Fact]
    public void AutosaveAfterEveryGameDay()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        var path = Path.Combine(_directory, "auto.db");
        var saver = new AutoSaver(host, path);
        host.World.Run(3 * host.World.Calendar.TicksPerDay);

        Assert.Equal([1, 2, 3], saver.SavedTicks.Select(t => t / host.World.Calendar.TicksPerDay));
        Assert.Equal(3 * host.World.Calendar.TicksPerDay, SaveGameStore.Load(path).Tick);
    }

    /// <summary>
    /// Bogen 25 acceptance (F5249 = C: start world with two pairs): 30 game days headless, fixed seed,
    /// mock decider. No crash, no need neglected beyond the data threshold, will to live not fading, and
    /// the state hash survives save and load.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void Acceptance30GameDaysWithTheStartWorld()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 2026, new MockDecisionPort(), "de");
        var limit = (long)host.Content.InhabitantModel.WillToLive.NeglectHoursBeforeDecline * host.World.Calendar.Settings.TicksPerHour;
        var longestNeglect = 0L;
        for (var day = 0; day < 30; day++)
        {
            for (var t = 0; t < host.World.Calendar.TicksPerDay; t++)
            {
                host.World.Step();
                longestNeglect = Math.Max(longestNeglect, host.Inhabitants.All.Max(i => i.NeglectTicks));
            }
        }

        Assert.Equal(4, host.Inhabitants.All.Count());
        Assert.True(longestNeglect <= limit, $"a need was neglected for {longestNeglect} ticks (limit {limit})");
        Assert.All(host.Inhabitants.All, i => Assert.False(host.Inhabitants.IsFading(i), $"{i.Id} is fading ({i.WillToLive})"));
        Assert.Contains(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response);

        var path = Path.Combine(_directory, "acceptance.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), Restore(SaveGameStore.Load(path), new MockDecisionPort()).World.StateHash());
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var content = ContentSet.Load(ContentDirectory);
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(content, settings, port, "de"), state);
    }
}
