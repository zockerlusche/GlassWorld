using System.IO.Compression;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A world as one shareable file (F5234 C, A1354): round trip and untrusted files.</summary>
public sealed class WorldShareTests : IDisposable
{
    private static readonly WorldSharing Settings = ContractSerializer.Deserialize<WorldSharing>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "player", "sharing.json")));

    private static readonly WorldShareOptions Options = new(
        new HashSet<string>(["small", "medium"], StringComparer.Ordinal),
        new HashSet<string>(["temperate"], StringComparer.Ordinal),
        new HashSet<int>([25, 50]));

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AWorldTravelsAsOneFileAndArrivesAsANewWorld()
    {
        var (mine, world, save) = SavedWorld("Meine Insel");
        var file = WorldShare.Export(mine, world, "<html>Chronik</html>", Path.Combine(_directory, "shared"), Settings, Now);
        Assert.EndsWith(Settings.Extension, file, StringComparison.Ordinal);

        var theirs = new WorldLibrary(Path.Combine(_directory, "their-worlds"));
        var arrived = WorldShare.Import(theirs, file, Settings, Options, Now);
        Assert.Equal("Meine Insel", arrived.Name);
        Assert.Equal(world.Seed, arrived.Seed);
        Assert.Equal(world.Day, arrived.Day);
        Assert.Equal(save, File.ReadAllBytes(theirs.SavePath(arrived.Id)));
        Assert.Single(theirs.List());

        // Opening the same file again never overwrites: a second world beside the first.
        var again = WorldShare.Import(theirs, file, Settings, Options, Now);
        Assert.NotEqual(arrived.Id, again.Id);
        Assert.Equal(2, theirs.List().Count);
    }

    [Theory]
    [InlineData("traversal", "msg.share.reason.unexpected_entry")]
    [InlineData("folder", "msg.share.reason.unexpected_entry")]
    [InlineData("format", "msg.share.reason.format")]
    [InlineData("not_sqlite", "msg.share.reason.not_save")]
    [InlineData("no_save", "msg.share.reason.no_save")]
    [InlineData("no_name", "msg.share.reason.manifest")]
    [InlineData("lava", "msg.share.reason.options")]
    [InlineData("duplicate", "msg.share.reason.unexpected_entry")]
    public void AFileFromSomeoneElseIsCheckedBeforeItBecomesAWorld(string broken, string reason)
    {
        var (mine, world, _) = SavedWorld("Insel");
        var file = WorldShare.Export(mine, world, "<html></html>", Path.Combine(_directory, "shared"), Settings, Now);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            switch (broken)
            {
                case "traversal":
                    Add(zip, "../evil.txt", "x");
                    break;
                case "folder":
                    Add(zip, "data/world.sqlite", "x");
                    break;
                case "format":
                    Replace(zip, WorldShare.ManifestEntry, Manifest(format: 99, name: "Insel"));
                    break;
                case "not_sqlite":
                    Replace(zip, WorldShare.SaveEntry, "just text");
                    break;
                case "no_save":
                    zip.GetEntry(WorldShare.SaveEntry)!.Delete();
                    break;
                case "lava":
                    Replace(zip, WorldShare.ManifestEntry, Manifest(format: (int)Settings.Format, name: "Insel").Replace("temperate", "lava", StringComparison.Ordinal));
                    break;
                case "duplicate":
                    Add(zip, WorldShare.SaveEntry, "SQLite format 3\0 second");
                    break;
                default:
                    Replace(zip, WorldShare.ManifestEntry, Manifest(format: (int)Settings.Format, name: " "));
                    break;
            }
        }

        var theirs = new WorldLibrary(Path.Combine(_directory, "their-worlds"));
        var error = Assert.Throws<WorldShareRefusedException>(() => WorldShare.Import(theirs, file, Settings, Options, Now));
        Assert.Equal(reason, error.ReasonKey);
        Assert.Empty(theirs.List()); // nothing half-imported
    }

    [Fact]
    public void TooLargeOrNotAZipIsRefused()
    {
        var (mine, world, _) = SavedWorld("Insel");
        var file = WorldShare.Export(mine, world, "<html></html>", Path.Combine(_directory, "shared"), Settings, Now);
        var small = new WorldSharing { ContractVersion = Settings.ContractVersion, Format = Settings.Format, Extension = Settings.Extension, MaxSaveBytes = 8, MaxChronicleBytes = Settings.MaxChronicleBytes, MaxManifestBytes = Settings.MaxManifestBytes };
        var theirs = new WorldLibrary(Path.Combine(_directory, "their-worlds"));
        Assert.Equal("msg.share.reason.too_large", Assert.Throws<WorldShareRefusedException>(() => WorldShare.Import(theirs, file, small, Options, Now)).ReasonKey);

        var text = Path.Combine(_directory, "not-a-world" + Settings.Extension);
        File.WriteAllText(text, "hello");
        Assert.Equal("msg.share.reason.not_zip", Assert.Throws<WorldShareRefusedException>(() => WorldShare.Import(theirs, text, Settings, Options, Now)).ReasonKey);
    }

    private (WorldLibrary Library, WorldMeta World, byte[] Save) SavedWorld(string name)
    {
        var library = new WorldLibrary(Path.Combine(_directory, "my-worlds"));
        var world = library.Create(new NewWorldChoice(name, 42, "small", "temperate", 50), Now) with { Day = 7 };
        library.Write(world);
        byte[] save = [.. Encoding.ASCII.GetBytes("SQLite format 3\0"), .. Enumerable.Range(0, 200).Select(i => (byte)i)];
        File.WriteAllBytes(library.SavePath(world.Id), save);
        return (library, world, save);
    }

    private static string Manifest(int format, string name) =>
        $"{{\"format\":{format},\"name\":\"{name}\",\"seed\":42,\"size\":\"small\",\"climate\":\"temperate\",\"start_favor\":50,\"day\":7,\"exported_utc\":\"2026-11-09T08:00:00+00:00\"}}";

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static void Replace(ZipArchive zip, string name, string text)
    {
        zip.GetEntry(name)!.Delete();
        Add(zip, name, text);
    }
}
