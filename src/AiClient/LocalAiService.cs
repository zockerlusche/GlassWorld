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
    /// <param name="dataDirectory">The host's folder for the service's lock file and logs.</param>
    /// <exception cref="TimeoutException">No ready line in time.</exception>
    /// <exception cref="Win32Exception"><c>uv</c> could not be started.</exception>
    /// <exception cref="InvalidOperationException">The service ended before its ready line.</exception>
    public static AiServiceProcess Start(string repositoryRoot, string dataDirectory)
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
                ["GW_AI_LOG_DIR"] = Path.Combine(dataDirectory, "logs"),
            });
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
