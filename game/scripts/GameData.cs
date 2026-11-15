using Godot;

namespace GlassWorld.Game;

/// <summary>
/// A1787 (B2824, Marcel W2236): where the game keeps its data - worlds (save, run trace, learning log), settings, shared
/// worlds, diagnostics, memories, chronicle exports and the AI service's logs. An exported game keeps them next to itself, in <c>&lt;game folder&gt;/daten</c>,
/// so a test package is self-contained and one look into its folder shows everything; when that folder cannot be
/// written (a protected program folder), in <c>Documents/GlassWorld</c>. The environment variable
/// <see cref="Variable"/> names another folder; the editor and development runs keep Godot's user folder as before.
/// Resolved once per start. Godot's own log is the one exception: the start scripts pass <c>--log-file daten\logs\godot.log</c>
/// next to the game before any code runs, so when the data falls back to Documents that log still goes beside the game
/// (review GW5 #503) - and it is not written at all when that folder is protected.
/// </summary>
public static class GameData
{
    /// <summary>Environment variable that names the data folder (tests, tools).</summary>
    public const string Variable = "GLASSWORLD_DATA";

    /// <summary>Name of the data folder next to an exported game.</summary>
    public const string BesideGame = "daten";

    private static string? _root;

    /// <summary>The data folder (created if needed).</summary>
    public static string Root => _root ??= Resolve();

    /// <summary>Name of the log folder next to an exported game (Marcel W2246: <c>&lt;game folder&gt;\log</c>).</summary>
    public const string LogsBesideGame = "log";

    private static string? _logs;

    /// <summary>
    /// A1787 part 2 (Marcel W2246): the game's log folder - Godot's log (start scripts), the AI service's logs (<c>ki</c>)
    /// and one folder per world (<c>welten/&lt;world&gt;</c>: run trace, learning log; <c>archiv</c> when the world is gone,
    /// <see cref="GlassWorld.Presentation.World.WorldLogArchive"/>). Next to the data folder when that is <c>daten</c> beside
    /// the game (an exported game, the quick run of a package), else <c>log</c> inside the data folder. Created if needed.
    /// </summary>
    public static string Logs => _logs ??= ResolveLogs();

    /// <summary>The log folder of one world (run trace, learning log) - outside the world folder, so it outlives it.</summary>
    public static string WorldLogs(string worldId) => GlassWorld.Presentation.World.WorldLogArchive.Of(Logs, worldId);

    /// <summary>A path inside the data folder.</summary>
    public static string At(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string ResolveLogs()
    {
        var data = System.IO.Path.TrimEndingDirectorySeparator(Root);
        var parent = System.IO.Path.GetDirectoryName(data);
        if (string.Equals(System.IO.Path.GetFileName(data), BesideGame, StringComparison.OrdinalIgnoreCase) && parent is { Length: > 0 }
            && Writable(System.IO.Path.Combine(parent, LogsBesideGame)))
        {
            return System.IO.Path.Combine(parent, LogsBesideGame);
        }

        var inside = System.IO.Path.Combine(data, LogsBesideGame);
        System.IO.Directory.CreateDirectory(inside);
        return inside;
    }

    private static string Resolve()
    {
        if (System.Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } chosen && Writable(chosen))
        {
            return System.IO.Path.GetFullPath(chosen);
        }

        // Editor and development runs: Godot's user folder, as before (check runs and tools rely on it).
        if (!OS.HasFeature("template"))
        {
            return OS.GetUserDataDir();
        }

        var beside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", BesideGame);
        if (Writable(beside))
        {
            return beside;
        }

        var documents = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "GlassWorld");
        return Writable(documents) ? documents : OS.GetUserDataDir();
    }

    /// <summary>Whether a folder exists or can be made, and a file can be written in it (a probe of its own, removed again).</summary>
    private static bool Writable(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var probe = System.IO.Path.Combine(folder, ".glassworld-write-probe");
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
