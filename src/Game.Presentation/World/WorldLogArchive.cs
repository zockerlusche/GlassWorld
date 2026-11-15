using System.Globalization;

namespace GlassWorld.Presentation.World;

/// <summary>
/// A1787 part 2 (Marcel W2246, D1192 #39): the logs of each world - run trace, learning log - live in a folder of their own
/// below the game's log folder (<c>log/welten/&lt;world&gt;</c>), not inside the world folder, so a world removed by hand
/// does not take its logs with it. When the world list is read, the logs of a world that is gone move to
/// <c>log/archiv/&lt;world&gt;-&lt;date&gt;</c>. Nothing is ever deleted.
/// </summary>
public static class WorldLogArchive
{
    /// <summary>Folder below the log folder that holds one folder per world.</summary>
    public const string Worlds = "welten";

    /// <summary>Folder below the log folder that holds the logs of worlds that are gone.</summary>
    public const string Archive = "archiv";

    /// <summary>The log folder of one world.</summary>
    /// <param name="logsRoot">The game's log folder.</param>
    /// <param name="worldId">The world's folder name (WorldLibrary id).</param>
    public static string Of(string logsRoot, string worldId)
    {
        ArgumentException.ThrowIfNullOrEmpty(logsRoot);
        ArgumentException.ThrowIfNullOrEmpty(worldId);
        return Path.Combine(logsRoot, Worlds, worldId);
    }

    /// <summary>
    /// Moves the log folder of every world whose world folder is gone into the archive, named with the world id and the
    /// time it was moved (a number is added if that name is taken). Returns the folders it moved to. A folder that cannot
    /// be moved now (held open) stays and is tried again next time.
    /// </summary>
    /// <param name="worldsRoot">The world list's folder (one folder per world).</param>
    /// <param name="logsRoot">The game's log folder.</param>
    /// <param name="now">Wall clock for the archive name (display only, never in the simulation).</param>
    public static IReadOnlyList<string> ArchiveOrphans(string worldsRoot, string logsRoot, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(worldsRoot);
        ArgumentException.ThrowIfNullOrEmpty(logsRoot);
        var worlds = Path.Combine(logsRoot, Worlds);
        if (!Directory.Exists(worlds))
        {
            return [];
        }

        var moved = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(worlds).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(folder);
            if (Directory.Exists(Path.Combine(worldsRoot, id)))
            {
                continue;
            }

            var archive = Path.Combine(logsRoot, Archive);
            Directory.CreateDirectory(archive);
            var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(archive, $"{id}-{stamp}");
            for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
            {
                target = Path.Combine(archive, string.Create(CultureInfo.InvariantCulture, $"{id}-{stamp}-{n}"));
            }

            try
            {
                Directory.Move(folder, target);
                moved.Add(target);
            }
            catch (IOException)
            {
                // Held open (Windows) - it stays where it is and is moved next time.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: no right to move it now.
            }
        }

        return moved;
    }
}
