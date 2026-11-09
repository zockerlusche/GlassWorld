using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>A shared world file was refused; <see cref="ReasonKey"/> is a DE/EN text key (no file content in it).</summary>
public sealed class WorldShareRefusedException(string reasonKey, Exception? inner = null) : Exception(reasonKey, inner)
{
    /// <summary>Text key of the reason (<c>msg.share.reason.*</c>).</summary>
    public string ReasonKey { get; } = reasonKey;
}

/// <summary>The choices a shared world must use (the same as for a new world).</summary>
/// <param name="Sizes">Size option ids.</param>
/// <param name="Climates">Climate option ids.</param>
/// <param name="StartFavors">Start favour options.</param>
public sealed record WorldShareOptions(IReadOnlySet<string> Sizes, IReadOnlySet<string> Climates, IReadOnlySet<int> StartFavors);

/// <summary>What a shared world file says about itself (manifest.json).</summary>
/// <param name="Format">Format version (content/player/sharing.json).</param>
/// <param name="Name">The world's name as the player gave it.</param>
/// <param name="Seed">World seed.</param>
/// <param name="Size">Size option id.</param>
/// <param name="Climate">Climate option id.</param>
/// <param name="StartFavor">Start favour.</param>
/// <param name="Day">Game day of the save.</param>
/// <param name="ExportedUtc">When it was shared (wall clock, display only).</param>
public sealed record WorldShareManifest(int Format, string Name, ulong Seed, string Size, string Climate, int StartFavor, long Day, DateTimeOffset ExportedUtc);

/// <summary>
/// A world as one shareable local file (F5234 C, A1354): a zip with the save game, the chronicle as a
/// web page and a manifest. Nothing goes online - the player passes the file on. A file from someone
/// else is untrusted: only the three known entries (no paths), sizes within the data's limits, a known
/// format, an SQLite save; it becomes a new world beside the player's own, never overwrites one.
/// The save game itself is checked by the core when the world is opened (contracts, migrations).
/// </summary>
public static class WorldShare
{
    /// <summary>Entry name of the manifest.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>Entry name of the save game.</summary>
    public const string SaveEntry = "world.sqlite";

    /// <summary>Entry name of the chronicle page.</summary>
    public const string ChronicleEntry = "chronicle.html";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>Writes the shared file of a saved world into <paramref name="folder"/> and returns its path.</summary>
    public static string Export(WorldLibrary library, WorldMeta world, string chronicleHtml, string folder, WorldSharing settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(settings);
        var save = library.SavePath(world.Id);
        if (!File.Exists(save))
        {
            throw new InvalidOperationException("The world has no save game yet.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, WorldLibrary.Slug(world.Name) + "_" + now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + settings.Extension);
        var manifest = new WorldShareManifest((int)settings.Format, world.Name, world.Seed, world.Size, world.Climate, world.StartFavor, world.Day, now);
        var temp = path + ".tmp";
        try
        {
            WriteZip(temp, save, manifest, chronicleHtml);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp); // nothing left behind when writing failed (already moved otherwise)
        }

        return path;
    }

    private static void WriteZip(string temp, string save, WorldShareManifest manifest, string chronicleHtml)
    {
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Write(zip, ManifestEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json)));

            // The save may be open in the game: read it shared.
            using (var source = new FileStream(save, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var target = zip.CreateEntry(SaveEntry, CompressionLevel.Optimal).Open())
            {
                source.CopyTo(target);
            }

            Write(zip, ChronicleEntry, Encoding.UTF8.GetBytes(chronicleHtml ?? string.Empty));
        }
    }

    /// <summary>
    /// Reads a shared file and adds it as a new world to <paramref name="library"/>. Throws
    /// <see cref="InvalidDataException"/> with a short reason when the file is not a shared world.
    /// </summary>
    public static WorldMeta Import(WorldLibrary library, string file, WorldSharing settings, WorldShareOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new WorldShareRefusedException("msg.share.reason.not_zip", e);
        }

        using (zip)
        {
            var allowed = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [ManifestEntry] = settings.MaxManifestBytes,
                [SaveEntry] = settings.MaxSaveBytes,
                [ChronicleEntry] = settings.MaxChronicleBytes,
            };
            if (zip.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(g => g.Count() > 1))
            {
                throw new WorldShareRefusedException("msg.share.reason.unexpected_entry");
            }

            foreach (var entry in zip.Entries)
            {
                // Only the known names at the top: no folders, no paths, nothing else.
                if (!allowed.TryGetValue(entry.FullName, out var max))
                {
                    throw new WorldShareRefusedException("msg.share.reason.unexpected_entry");
                }

                if (entry.Length > max)
                {
                    throw new WorldShareRefusedException("msg.share.reason.too_large");
                }
            }

            var manifest = ReadManifest(zip, settings);
            if (!options.Sizes.Contains(manifest.Size) || !options.Climates.Contains(manifest.Climate) || !options.StartFavors.Contains(manifest.StartFavor))
            {
                throw new WorldShareRefusedException("msg.share.reason.options");
            }

            var saveEntry = zip.GetEntry(SaveEntry) ?? throw new WorldShareRefusedException("msg.share.reason.no_save");
            var save = Read(saveEntry, settings.MaxSaveBytes);
            if (save.Length < SqliteHeader.Length || !save.AsSpan(0, SqliteHeader.Length).SequenceEqual(SqliteHeader))
            {
                throw new WorldShareRefusedException("msg.share.reason.not_save");
            }

            var meta = library.Create(new NewWorldChoice(manifest.Name, manifest.Seed, manifest.Size, manifest.Climate, manifest.StartFavor), now);
            try
            {
                File.WriteAllBytes(library.SavePath(meta.Id), save);
                meta = meta with { Day = manifest.Day };
                library.Write(meta);
                return meta;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No half-made world: without its save the folder goes again.
                File.Delete(library.SavePath(meta.Id));
                library.Discard(meta.Id);
                throw;
            }
        }
    }

    private static WorldShareManifest ReadManifest(ZipArchive zip, WorldSharing settings)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new WorldShareRefusedException("msg.share.reason.manifest");
        WorldShareManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorldShareManifest>(Read(entry, settings.MaxManifestBytes), Json);
        }
        catch (JsonException e)
        {
            throw new WorldShareRefusedException("msg.share.reason.manifest", e);
        }

        if (manifest is null || manifest.Format != settings.Format)
        {
            throw new WorldShareRefusedException("msg.share.reason.format");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 64 || manifest.Day < 1)
        {
            throw new WorldShareRefusedException("msg.share.reason.manifest");
        }

        return manifest;
    }

    private static byte[] Read(ZipArchiveEntry entry, long max)
    {
        // Count while reading: the size in the zip header could lie.
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new WorldShareRefusedException("msg.share.reason.too_large");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        target.Write(data);
    }
}
