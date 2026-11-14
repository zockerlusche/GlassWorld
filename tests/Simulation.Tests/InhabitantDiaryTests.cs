using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>Each inhabitant's diary from core events (TL #20472): complete at any speed, bounded, saved, outside the hash.</summary>
public sealed class InhabitantDiaryTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public InhabitantDiaryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SimulationHost Host(ulong seed = 7) =>
        SimulationHost.CreatePlaceholderWorld(ContentDirectory, seed, new MockDecisionPort(), "de");

    private static HashSet<string> LeftOut() => Content.Value.InhabitantModel.Diary!.LeftOutActions!.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryStartedActionOfAx16RunAppearsOnceInOrderAndIdleIsLeftOut()
    {
        var host = Host();
        var started = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionStarted)
            {
                started.Add(e);
            }
        };

        // x16: the game advances up to max_ticks_per_frame ticks per frame and reads once per frame.
        var perFrame = Content.Value.Calendar.Settings.MaxTicksPerFrame;
        var read = new List<DiaryEntry>();
        long version = 0;
        for (var frame = 0; frame < 3 * Content.Value.Calendar.TicksPerDay / perFrame; frame++)
        {
            host.World.Run(perFrame);
            read.AddRange(host.Diary.Since(version));
            version = host.Diary.Version;
        }

        var leftOut = LeftOut();
        Assert.Contains(started, e => leftOut.Contains(e.Action!));
        var expected = started.Where(e => !leftOut.Contains(e.Action!)).Select(e => (e.Tick, e.InhabitantId, e.Action, e.TargetId)).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, read.Select(e => (e.Tick, e.InhabitantId, (string?)e.Action, e.TargetId)));
        Assert.Equal(Enumerable.Range(1, read.Count).Select(i => (long)i), read.Select(e => e.Seq));
        Assert.DoesNotContain(read, e => leftOut.Contains(e.Action));
    }

    [Fact]
    public void TheCapacityHoldsPerInhabitant()
    {
        var host = Host();
        var small = new InhabitantDiary(new Diary { Capacity = 3, LeftOutActions = [.. LeftOut()] }, host.Inhabitants, host.Objects);
        host.Inhabitants.Reported += small.Observe;
        host.World.Run(Content.Value.Calendar.TicksPerDay);

        foreach (var inhabitant in host.Inhabitants.All)
        {
            var all = host.Diary.Of(inhabitant.Id);
            Assert.True(all.Count > 3);
            Assert.True(all.Count <= host.Diary.Capacity);
            Assert.Equal(all.TakeLast(3).Select(e => (e.Tick, e.Action, e.TargetId)), small.Of(inhabitant.Id).Select(e => (e.Tick, e.Action, e.TargetId)));
        }

        Assert.Equal(Content.Value.InhabitantModel.Diary!.Capacity, host.Diary.Capacity);
    }

    [Fact]
    public void AfterTrimmingSinceIsStillCompleteAndInOrder()
    {
        var host = Host();
        var small = new InhabitantDiary(new Diary { Capacity = 3, LeftOutActions = [.. LeftOut()] }, host.Inhabitants, host.Objects);
        host.Inhabitants.Reported += small.Observe;
        var read = new List<DiaryEntry>();
        var perFrame = Content.Value.Calendar.Settings.MaxTicksPerFrame;
        for (var frame = 0; frame < Content.Value.Calendar.TicksPerDay / perFrame; frame++)
        {
            host.World.Run(perFrame);
            var last = read.Count == 0 ? 0 : read[^1].Seq;
            var fresh = small.Since(last);
            Assert.Equal(small.All.Where(e => e.Seq > last), fresh);
            read.AddRange(fresh);
        }

        var kept = host.Inhabitants.All.SelectMany(i => small.Of(i.Id)).OrderBy(e => e.Seq).ToList();
        Assert.Equal(3 * host.Inhabitants.All.Count(), kept.Count);
        Assert.Equal(kept, small.All);
        Assert.Equal(kept, small.Since(0));
        Assert.Equal(kept.Skip(2), small.Since(kept[1].Seq));
        Assert.Equal(small.Version, kept[^1].Seq);

        // Read every frame: what is kept and new is complete each time, and the order holds.
        Assert.True(read.Count > kept.Count);
        Assert.True(read.Zip(read.Skip(1)).All(p => p.First.Seq < p.Second.Seq));
    }

    [Fact]
    public void TheVersionContinuesAfterSaveAndLoadAndStaysWithCapacityZero()
    {
        var host = Host();
        var none = new InhabitantDiary(new Diary { Capacity = 0 }, host.Inhabitants, host.Objects);
        host.Inhabitants.Reported += none.Observe;
        host.World.Run(Content.Value.Calendar.Settings.TicksPerHour * 6);
        Assert.Equal(0, none.Version);
        Assert.True(host.Diary.Version > 0);

        var path = Path.Combine(_directory, "version.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var state = SaveGameStore.Load(path);
        Assert.Equal(host.Diary.Version, state.DiaryVersion);
        var loaded = Restore(state);
        Assert.Equal(host.Diary.Version, loaded.Diary.Version);

        none.Restore(state.Diary!, state.DiaryVersion);
        Assert.Empty(none.All);
        Assert.Equal(host.Diary.Version, none.Version);
        Assert.Empty(none.Since(0));

        host.World.Run(200);
        loaded.World.Run(200);
        Assert.Equal(host.Diary.Version, loaded.Diary.Version);
        Assert.Equal(host.Diary.All, loaded.Diary.All);
    }

    [Fact]
    public void SaveAndLoadKeepTheDiaryAndTheDiaryIsOutsideTheHash()
    {
        var host = Host();
        host.World.Run(Content.Value.Calendar.Settings.TicksPerHour * 12);
        Assert.NotEmpty(host.Diary.All);

        var path = Path.Combine(_directory, "diary.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path));
        Assert.Equal(host.Diary.All, loaded.Diary.All);
        Assert.Equal(host.Diary.Version, loaded.Diary.Version);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        // An empty diary does not change the hash either: it is presentation-only history.
        var emptied = Restore(StateCodec.Capture(host) with { Diary = null });
        Assert.Empty(emptied.Diary.All);
        Assert.Equal(host.World.StateHash(), emptied.World.StateHash());

        host.World.Run(200);
        loaded.World.Run(200);
        emptied.World.Run(200);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.World.StateHash(), emptied.World.StateHash());
        Assert.Equal(host.Diary.All, loaded.Diary.All);
    }

    [Fact]
    public void ASaveFromBeforeTheDiaryLoadsWithAnEmptyDiary()
    {
        var host = Host();
        host.World.Run(Content.Value.Calendar.Settings.TicksPerHour * 6);
        var path = Path.Combine(_directory, "old.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        File.Delete(path + ".sha256"); // edited like a save from before B2712: such saves have no checksum
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE diary; DELETE FROM meta WHERE key = 'diary_version'; PRAGMA user_version = 8;";
            command.ExecuteNonQuery();
        }

        var state = SaveGameStore.Load(path);
        Assert.Empty(state.Diary ?? []);
        var loaded = Restore(state);
        Assert.Empty(loaded.Diary.All);
        Assert.Equal(0, loaded.Diary.Version);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    private static SimulationHost Restore(SaveState state)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(Content.Value, settings, new MockDecisionPort(), "de"), state);
    }
}
