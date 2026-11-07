using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GlassWorld.Presentation.World;

/// <summary>Display data of a saved world (meta.json). The save file stays the truth (ADR-0003).</summary>
/// <param name="Id">Folder name, language-neutral.</param>
/// <param name="Name">Name the player gave (shown as is, never sent to the AI).</param>
/// <param name="Seed">World seed.</param>
/// <param name="Size">Size option id (content/player/world_options.json).</param>
/// <param name="Climate">Climate option id.</param>
/// <param name="StartFavor">Start favour chosen for the world (content/player/favor.json).</param>
/// <param name="Day">Game day at the last save.</param>
/// <param name="LastPlayedUtc">When it was last saved (wall clock; display only, never in the simulation).</param>
public sealed record WorldMeta(string Id, string Name, ulong Seed, string Size, string Climate, int StartFavor, long Day, DateTimeOffset LastPlayedUtc);

/// <summary>
/// The player's worlds (B6 R3): one folder per world below a root (the game passes
/// <c>user://worlds</c>), with <c>world.sqlite</c> (the save game) and <c>meta.json</c> (what the
/// world list shows). Several worlds side by side; ids are safe folder names derived from the name.
/// </summary>
public sealed class WorldLibrary(string root)
{
    /// <summary>File name of the save game inside a world folder.</summary>
    public const string SaveFile = "world.sqlite";

    private const string MetaFile = "meta.json";

    private static readonly HashSet<string> ReservedNames = new(
        ["con", "prn", "aux", "nul", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"com{n}", $"lpt{n}" })],
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// <summary>Folder holding all worlds.</summary>
    public string Root { get; } = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>Creates a new world folder with a unique id and writes its meta data.</summary>
    public WorldMeta Create(NewWorldChoice choice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentException.ThrowIfNullOrWhiteSpace(choice.Name);
        var name = choice.Name;
        Directory.CreateDirectory(Root);
        var baseId = Slug(name);
        var id = baseId;
        for (var n = 2; Directory.Exists(Path.Combine(Root, id)); n++)
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{n}");
        }

        Directory.CreateDirectory(Path.Combine(Root, id));
        var meta = new WorldMeta(id, name.Trim(), choice.Seed, choice.Size, choice.Climate, choice.StartFavor, 1, now);
        Write(meta);
        return meta;
    }

    /// <summary>Path of the save game of a world.</summary>
    public string SavePath(string id) => Path.Combine(Root, id, SaveFile);

    /// <summary>Updates the meta data after a save (day and time).</summary>
    public void Write(WorldMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var path = Path.Combine(Root, meta.Id, MetaFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, Json), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>All worlds with a save game, most recently played first; broken meta files are skipped.</summary>
    public IReadOnlyList<WorldMeta> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var worlds = new List<WorldMeta>();
        foreach (var folder in Directory.GetDirectories(Root))
        {
            var metaPath = Path.Combine(folder, MetaFile);
            if (!File.Exists(metaPath) || !File.Exists(Path.Combine(folder, SaveFile)))
            {
                continue;
            }

            try
            {
                var meta = JsonSerializer.Deserialize<WorldMeta>(File.ReadAllText(metaPath), Json);
                if (meta is not null && meta.Id == Path.GetFileName(folder))
                {
                    worlds.Add(meta);
                }
            }
            catch (JsonException)
            {
                // A world with a broken meta file is not listed; its save game stays untouched.
            }
        }

        return [.. worlds.OrderByDescending(w => w.LastPlayedUtc).ThenBy(w => w.Id, StringComparer.Ordinal)];
    }

    /// <summary>Safe, language-neutral folder name: a-z, 0-9 and '-', at most 32 characters.</summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder();
        foreach (var c in name.Trim().ToUpperInvariant())
        {
            var mapped = c switch
            {
                'Ä' => "AE",
                'Ö' => "OE",
                'Ü' => "UE",
                'ß' => "SS",
                _ when char.IsAsciiLetterOrDigit(c) => c.ToString(),
                _ => "-",
            };
            builder.Append(mapped);
        }

        var slug = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
#pragma warning disable CA1308 // Folder names are lower-case by convention; ASCII only.
        slug = slug.ToLowerInvariant();
#pragma warning restore CA1308
        slug = slug.Length > 32 ? slug[..32].TrimEnd('-') : slug;
        slug = slug.Length == 0 ? "world" : slug;

        // Windows refuses device names as folder names ("nul", "con", "com1", ...).
        return ReservedNames.Contains(slug) ? slug + "-world" : slug;
    }
}
