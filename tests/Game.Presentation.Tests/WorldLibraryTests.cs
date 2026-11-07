using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>Several worlds side by side (B6 R3): folders, meta data, list order, safe ids.</summary>
public sealed class WorldLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gw-worlds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Meine Insel", "meine-insel")]
    [InlineData("Größe & Übermut!", "groesse-uebermut")]
    [InlineData("   ", "world")]
    [InlineData("../../etc", "etc")]
    [InlineData("Nul", "nul-world")]
    [InlineData("COM1", "com1-world")]
    [InlineData("con.txt", "con-txt")]
    [InlineData("A very long world name that keeps going and going", "a-very-long-world-name-that-keep")]
    public void SlugIsASafeFolderName(string name, string expected) => Assert.Equal(expected, WorldLibrary.Slug(name));

    [Fact]
    public void SameNameGetsAUniqueFolder()
    {
        var library = new WorldLibrary(_root);
        var a = library.Create(new NewWorldChoice("Insel", 1, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        var b = library.Create(new NewWorldChoice("Insel", 2, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        Assert.Equal("insel", a.Id);
        Assert.Equal("insel-2", b.Id);
    }

    [Fact]
    public void ListShowsWorldsWithSaveMostRecentFirst()
    {
        var library = new WorldLibrary(_root);
        var old = library.Create(new NewWorldChoice("Alt", 1, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        var recent = library.Create(new NewWorldChoice("Neu", 2, "medium", "warm", 50), DateTimeOffset.UnixEpoch.AddDays(3));
        var unsaved = library.Create(new NewWorldChoice("Ohne Spielstand", 3, "small", "temperate", 50), DateTimeOffset.UnixEpoch.AddDays(9));
        File.WriteAllText(library.SavePath(old.Id), "x");
        File.WriteAllText(library.SavePath(recent.Id), "x");

        Assert.Equal(["neu", "alt"], library.List().Select(w => w.Id));
        Assert.DoesNotContain(library.List(), w => w.Id == unsaved.Id);
    }

    [Fact]
    public void BrokenMetaIsSkippedNotDeleted()
    {
        var library = new WorldLibrary(_root);
        var world = library.Create(new NewWorldChoice("Kaputt", 1, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        File.WriteAllText(library.SavePath(world.Id), "x");
        File.WriteAllText(Path.Combine(_root, world.Id, "meta.json"), "{ broken");
        Assert.Empty(library.List());
        Assert.True(File.Exists(library.SavePath(world.Id)));
    }

    [Fact]
    public void DiscardRemovesOnlyFoldersWithoutSaveGame()
    {
        var library = new WorldLibrary(_root);
        var failed = library.Create(new NewWorldChoice("Fehlstart", 1, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        var saved = library.Create(new NewWorldChoice("Gespeichert", 2, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        File.WriteAllText(library.SavePath(saved.Id), "x");

        Assert.True(library.Discard(failed.Id));
        Assert.False(Directory.Exists(Path.Combine(_root, failed.Id)));
        Assert.False(library.Discard(saved.Id));
        Assert.True(File.Exists(library.SavePath(saved.Id)));
    }

    [Fact]
    public void WriteUpdatesDayAndTime()
    {
        var library = new WorldLibrary(_root);
        var world = library.Create(new NewWorldChoice("Insel", 1, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        File.WriteAllText(library.SavePath(world.Id), "x");
        library.Write(world with { Day = 12, LastPlayedUtc = DateTimeOffset.UnixEpoch.AddDays(1) });
        Assert.Equal(12, library.List()[0].Day);
    }
}
