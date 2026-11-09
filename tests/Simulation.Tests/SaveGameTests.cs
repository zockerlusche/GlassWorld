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
    public void ASearchUnderWayIsHashedAndSurvivesSaveAndLoad()
    {
        // Review GW3 #118: the give-up clock of looking further away is state like any other.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var before = host.World.StateHash();
        host.Inhabitants.Get("inh-000001").SearchSince["thirst"] = 7;
        Assert.NotEqual(before, host.World.StateHash());

        var path = Path.Combine(_directory, "search.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path), new MockDecisionPort());
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(7, loaded.Inhabitants.Get("inh-000001").SearchSince["thirst"]);
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

    [Theory]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("text")]
    public void ForeignOrEmptyFileIsRefusedAndLeftUnchanged(string kind)
    {
        var path = Path.Combine(_directory, kind + ".db");
        switch (kind)
        {
            case "empty":
                File.WriteAllBytes(path, []);
                break;
            case "text":
                File.WriteAllText(path, "not a database");
                break;
            default:
                using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "CREATE TABLE notes(text TEXT);";
                    cmd.ExecuteNonQuery();
                }

                break;
        }

        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Load(path));
        Assert.Equal(before, File.ReadAllBytes(path));
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

    /// <summary>
    /// Review GW3 #108: a save of schema 4 (main before F4) with the first look's hair styles and outfit
    /// (curly, braided, tied, wrap) is migrated, and every inhabitant gets a figure the tables know:
    /// hair entry, outfit and its clothing parts (what FigureSpec.From needs; no stand-in capsules).
    /// </summary>
    [Fact]
    public void ASaveFromBeforeTheHairKitKeepsRealFigures()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        var path = Path.Combine(_directory, "v4.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        string[] oldHair = ["curly", "braided", "tied", "short"];
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            var ids = host.Inhabitants.All.Select(i => i.Id).Order(StringComparer.Ordinal).ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                using var set = c.CreateCommand();
                set.CommandText = "UPDATE meta SET value = json_set(value, $hair, $h, $outfit, 'wrap') WHERE key = 'appearances';";
                set.Parameters.AddWithValue("$hair", $"$.\"{ids[i]}\".HairStyle");
                set.Parameters.AddWithValue("$outfit", $"$.\"{ids[i]}\".ClothingStyle");
                set.Parameters.AddWithValue("$h", oldHair[i % oldHair.Length]);
                Assert.Equal(1, set.ExecuteNonQuery());
            }

            using var version = c.CreateCommand();
            version.CommandText = "PRAGMA user_version = 4;";
            version.ExecuteNonQuery();
        }

        var state = SaveGameStore.Load(path);
        var loaded = Restore(state, new MockDecisionPort());
        var look = loaded.Content.Appearance;
        var figures = Contracts.Validation.ContractSerializer.Deserialize<Contracts.LookFigures>(
            File.ReadAllText(Path.Combine(ContentDirectory, "look", "figures.json")));
        Assert.All(state.Inhabitants, i => Assert.Equal("tunic", i.Appearance!.ClothingStyle));
        Assert.Equal(["braid", "bun", "curls", "short"], state.Inhabitants.Select(i => i.Appearance!.HairStyle).Order(StringComparer.Ordinal));
        Assert.All(loaded.Inhabitants.All, i =>
        {
            var a = i.Appearance!;
            Assert.Contains(figures.Hair, h => h.Style == a.HairStyle);
            var outfit = Assert.Single(look.Outfits, o => o.Id == a.ClothingStyle);
            Assert.All(outfit.Parts, part => Assert.Contains(figures.ClothingParts, p => p.Id == part));
        });
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var content = ContentSet.Load(ContentDirectory);
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(content, settings, port, "de"), state);
    }
}
