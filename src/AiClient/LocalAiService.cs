using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace GlassWorld.AiClient;

/// <summary>
/// How a host (the game, the headless runner) starts the local AI service from the repository
/// (ADR-0006): <c>uv run python -m glassworld_ai</c> in the repository root, with lock and log folders
/// in the host's data folder. One place, so the game and the headless runner start it the same way.
/// </summary>
public static class LocalAiService
{
    /// <summary>Environment variable that may name the folder holding the AI service (test package, until B10 packages it).</summary>
    public const string RootVariable = "GLASSWORLD_AI_ROOT";

    /// <summary>How long a start waits for the ready line (the model loads in the background later).</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a stop waits for the service to unload the model before it is killed (GW2 #30).</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The repository root the service runs from: <paramref name="configuredRoot"/> (usually the value of
    /// <see cref="RootVariable"/>) if set, otherwise the parent of the content folder.
    /// </summary>
    public static string RepositoryRoot(string contentDirectory, string? configuredRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentDirectory);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot) ? Path.Combine(contentDirectory, "..") : configuredRoot);
    }

    /// <summary>Starts the service and waits for its ready line.</summary>
    /// <param name="repositoryRoot">Repository root (see <see cref="RepositoryRoot"/>).</param>
    /// <param name="dataDirectory">The host's folder for the service's lock file (and its logs when no log folder is given).</param>
    /// <param name="logDirectory">Where the service writes its logs (A1787 part 2: the game's log folder); null = <c>logs</c> in the data folder.</param>
    /// <exception cref="TimeoutException">No ready line in time.</exception>
    /// <exception cref="Win32Exception"><c>uv</c> could not be started.</exception>
    /// <exception cref="InvalidOperationException">The service ended before its ready line.</exception>
    public static AiServiceProcess Start(string repositoryRoot, string dataDirectory, string? logDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryRoot);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return AiServiceProcess.Start(
            "uv",
            ["run", "python", "-m", "glassworld_ai"],
            repositoryRoot,
            ReadyTimeout,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GW_AI_LOCK_DIR"] = Path.Combine(dataDirectory, "lock"),
                ["GW_AI_LOG_DIR"] = logDirectory ?? Path.Combine(dataDirectory, "logs"),
            });
    }

    /// <summary>Folder of the packaged service next to a game (B10-S2): <c>ai-service</c> with the embedded Python.</summary>
    public const string PackageFolder = "ai-service";

    /// <summary>
    /// A1421 (B10-S2): the packaged service next to the game - <c>&lt;gameDirectory&gt;/ai-service</c> with
    /// <c>python/python.exe</c> and <c>config.toml</c> (tools/package_ai_service.py) - or null when there is none.
    /// </summary>
    public static string? PackagedService(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        var folder = Path.Combine(gameDirectory, PackageFolder);
        return File.Exists(Path.Combine(folder, "python", "python.exe")) && File.Exists(Path.Combine(folder, "config.toml"))
            ? Path.GetFullPath(folder)
            : null;
    }

    /// <summary>
    /// A1421: the folders the packaged service writes to (its config names folders inside the program folder, D B10-S2):
    /// everything in the host's data folder, as <c>GW_AI_*</c> overrides. The game sets them in its own process too, so
    /// it finds the sounds and pictures where the service puts them.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PackagedFolders(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GW_AI_IMAGE_OUTPUT_DIR"] = Path.Combine(dataDirectory, "images"),
            ["GW_AI_SOUND_OUTPUT_DIR"] = Path.Combine(dataDirectory, "sounds"),
            ["GW_AI_MODEL3D_IMAGE_DIR"] = Path.Combine(dataDirectory, "images"),
            ["GW_AI_MODEL3D_OUTPUT_DIR"] = Path.Combine(dataDirectory, "models"),
            ["GW_AI_OLLAMA_INSTALL_DIR"] = Path.Combine(dataDirectory, "ollama"),
            ["GW_AI_MODEL_STORE_DIR"] = Path.Combine(dataDirectory, "model-store"),
        };
    }

    /// <summary>Starts the packaged service (embedded Python, no uv) and waits for its ready line.</summary>
    /// <param name="packageDirectory">The package folder (see <see cref="PackagedService"/>).</param>
    /// <param name="dataDirectory">The host's data folder for lock, outputs and models.</param>
    /// <param name="logDirectory">Where the service writes its logs; null = <c>logs</c> in the data folder.</param>
    /// <exception cref="TimeoutException">No ready line in time.</exception>
    /// <exception cref="Win32Exception">The embedded Python could not be started.</exception>
    /// <exception cref="InvalidOperationException">The service ended before its ready line.</exception>
    public static AiServiceProcess StartPackaged(string packageDirectory, string dataDirectory, string? logDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageDirectory);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var environment = new Dictionary<string, string>(PackagedFolders(dataDirectory), StringComparer.Ordinal)
        {
            ["GW_AI_LOCK_DIR"] = Path.Combine(dataDirectory, "lock"),
            ["GW_AI_LOG_DIR"] = logDirectory ?? Path.Combine(dataDirectory, "logs"),
        };
        return AiServiceProcess.Start(
            Path.Combine(packageDirectory, "python", "python.exe"),
            ["-m", "glassworld_ai", "--config", Path.Combine(packageDirectory, "config.toml")],
            packageDirectory,
            ReadyTimeout,
            environment);
    }

    /// <summary>
    /// The lock file the game's service writes (<c>GW_AI_LOCK_DIR</c> = Godot's user data folder of the
    /// project + <c>ai-service/lock</c>, file <c>ai-service.lock</c>): Windows
    /// <c>%APPDATA%/Godot/app_userdata/GlassWorld</c>, Linux <c>~/.local/share/godot/app_userdata/GlassWorld</c>,
    /// macOS <c>~/Library/Application Support/Godot/app_userdata/GlassWorld</c>.
    /// </summary>
    public static string GameLockFile()
    {
        var userData = OperatingSystem.IsLinux()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "godot", "app_userdata", "GlassWorld")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "GlassWorld");
        return Path.Combine(userData, "ai-service", "lock", "ai-service.lock");
    }

    /// <summary>
    /// The pid of a service that is running according to a lock file (JSON <c>pid</c>, <c>port</c>,
    /// <c>started_utc</c>, <c>start_marker</c>, written by the service once it is ready), or null if there
    /// is no lock, it cannot be read, or its process is gone. On Windows the start marker (process creation
    /// time) must match too, so a reused pid does not count; where it cannot be checked, a living pid counts.
    /// </summary>
    public static int? RunningService(string lockFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFile);
        int pid;
        long? marker;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
            var root = document.RootElement;
            pid = root.GetProperty("pid").GetInt32();
            marker = root.TryGetProperty("start_marker", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }

        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            if (marker is { } expected && OperatingSystem.IsWindows())
            {
                try
                {
                    return process.StartTime.ToFileTimeUtc() == expected ? pid : null;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Not allowed to read its start time: a living pid counts (better refuse than overload the GPU).
                    return pid;
                }
            }

            return pid;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True for the exceptions <see cref="Start"/> throws when the service is not available.</summary>
    public static bool IsStartFailure(Exception exception) =>
        exception is TimeoutException or Win32Exception or InvalidOperationException;
}
