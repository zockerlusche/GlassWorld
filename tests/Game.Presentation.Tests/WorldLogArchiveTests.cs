using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1787 part 2 (Marcel W2246): the logs of a world outlive it and go to the archive, never deleted.</summary>
public sealed class WorldLogArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gw-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheLogsOfAWorldThatIsGoneMoveToTheArchiveTheOthersStay()
    {
        var worlds = Path.Combine(_root, "daten", "worlds");
        var logs = Path.Combine(_root, "log");
        Directory.CreateDirectory(Path.Combine(worlds, "kept"));
        foreach (var id in new[] { "kept", "gone" })
        {
            var folder = WorldLogArchive.Of(logs, id);
            Directory.CreateDirectory(Path.Combine(folder, "trace"));
            File.WriteAllText(Path.Combine(folder, "trace", "day-0001.jsonl"), "{}");
        }

        var now = new DateTimeOffset(2026, 11, 16, 1, 2, 3, TimeSpan.Zero);
        var moved = WorldLogArchive.ArchiveOrphans(worlds, logs, now);

        var archived = Path.Combine(logs, WorldLogArchive.Archive, "gone-20261116-010203");
        Assert.Equal([archived], moved);
        Assert.True(File.Exists(Path.Combine(archived, "trace", "day-0001.jsonl")));
        Assert.False(Directory.Exists(WorldLogArchive.Of(logs, "gone")));
        Assert.True(File.Exists(Path.Combine(WorldLogArchive.Of(logs, "kept"), "trace", "day-0001.jsonl")));

        // A second world of the same name gone at the same time gets a name of its own; nothing is overwritten.
        Directory.CreateDirectory(WorldLogArchive.Of(logs, "gone"));
        Assert.Equal([archived + "-2"], WorldLogArchive.ArchiveOrphans(worlds, logs, now));
        Assert.True(File.Exists(Path.Combine(archived, "trace", "day-0001.jsonl")));
    }

    [Fact]
    public void OnlyAWorldOfTheListLogsInTheLogFolderOtherSavesKeepThemBesideThem()
    {
        // Review GW5 #525: a check run's world or the load check's copy is no world of the list.
        var worlds = Path.Combine(_root, "daten", "worlds");
        var logs = Path.Combine(_root, "log");
        Assert.Equal(WorldLogArchive.Of(logs, "insel"), WorldLogArchive.For(Path.Combine(worlds, "insel", "world.sqlite"), worlds, logs));
        Assert.Equal(Path.Combine(_root, "daten"), WorldLogArchive.For(Path.Combine(_root, "daten", "check-load.db"), worlds, logs));
        Assert.Equal(Path.Combine(_root, "smoke", "worlds", "a"), WorldLogArchive.For(Path.Combine(_root, "smoke", "worlds", "a", "world.sqlite"), worlds, logs));
    }

    [Fact]
    public void WithoutWorldLogsNothingHappens() =>
        Assert.Empty(WorldLogArchive.ArchiveOrphans(Path.Combine(_root, "worlds"), Path.Combine(_root, "log"), DateTimeOffset.UnixEpoch));
}
