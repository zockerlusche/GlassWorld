using GlassWorld.AiClient;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Headless;

/// <summary>The decider and job ports of a run, and whether AI calls are still running (pacing).</summary>
internal interface IAiConnection : IDisposable
{
    /// <summary>Decider port.</summary>
    IDecisionPort Decisions { get; }

    /// <summary>Job port, or null (generation jobs dormant, like the game with the mock).</summary>
    IJobPort? Jobs { get; }

    /// <summary>True while at least one question or job is out with the AI and not answered yet.</summary>
    bool Busy { get; }
}

/// <summary>The AI service could not be started (exit code 4).</summary>
internal sealed class AiUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>The mock decider, no job port: the deterministic run CI uses.</summary>
internal sealed class MockConnection(string contentDirectory) : IAiConnection
{
    /// <inheritdoc/>
    public IDecisionPort Decisions { get; } = MockDecisionPort.FromContent(contentDirectory);

    /// <inheritdoc/>
    public IJobPort? Jobs => null;

    /// <inheritdoc/>
    public bool Busy => false;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>
/// The local AI service, started exactly like the game starts it with <c>GLASSWORLD_AI=live</c>
/// (<see cref="LocalAiService"/>: session token, ready line, Job Object, parent pid), with the same
/// decider and job ports. Dispose stops it (<c>POST /shutdown</c>, then kill).
/// </summary>
internal sealed class LiveConnection : IAiConnection
{
    private readonly AiServiceProcess _service;
    private readonly HttpDecisionPort _decisions;
    private readonly HttpJobPort _jobs;
    private bool _disposed;

    private LiveConnection(AiServiceProcess service, HttpDecisionPort decisions, HttpJobPort jobs)
    {
        _service = service;
        _decisions = decisions;
        _jobs = jobs;
    }

    /// <inheritdoc/>
    public IDecisionPort Decisions => _decisions;

    /// <inheritdoc/>
    public IJobPort? Jobs => _jobs;

    /// <inheritdoc/>
    public bool Busy => _decisions.InFlight > 0 || _jobs.InFlight > 0;

    /// <summary>Port of the running service.</summary>
    public int Port => _service.Port;

    /// <summary>Message when the game's service is running (exit code 4).</summary>
    public const string GameServiceRunning = "a game's AI service is running; close the game first";

    /// <summary>
    /// Starts the service from the repository (<c>GLASSWORLD_AI_ROOT</c>, else the content folder's
    /// parent). Lock and logs go to <c>GLASSWORLD_HEADLESS_DATA</c> or the local application data folder,
    /// apart from the game's, so a headless run never ends the game's service as a leftover. If the game's
    /// lock file names a running service, nothing is started: two services would share one GPU without
    /// knowing about each other (GW2 review #132).
    /// </summary>
    /// <param name="contentDirectory">Content folder.</param>
    /// <param name="deadlineMs">Decision deadline from the decider settings.</param>
    /// <param name="log">Where start notes go.</param>
    /// <param name="gameLockFile">The game's lock file (null = <see cref="LocalAiService.GameLockFile"/>; tests pass a fake).</param>
    public static LiveConnection Start(string contentDirectory, int deadlineMs, TextWriter log, string? gameLockFile = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        var gameLock = gameLockFile ?? LocalAiService.GameLockFile();
        if (LocalAiService.RunningService(gameLock) is { } running)
        {
            throw new AiUnavailableException($"{GameServiceRunning} (pid {running}, lock {gameLock})", new InvalidOperationException(gameLock));
        }

        var repository = LocalAiService.RepositoryRoot(contentDirectory, Environment.GetEnvironmentVariable(LocalAiService.RootVariable));
        var data = Environment.GetEnvironmentVariable("GLASSWORLD_HEADLESS_DATA") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassWorld", "headless", "ai-service");
        AiServiceProcess service;
        try
        {
            service = LocalAiService.Start(repository, data);
        }
        catch (Exception ex) when (LocalAiService.IsStartFailure(ex))
        {
            throw new AiUnavailableException($"AI service not started from {repository}: {ex.Message}", ex);
        }

        if (service.StaleKilled is { } stale)
        {
            log.WriteLine($"AI_SERVICE ended leftover service from an earlier session (pid {stale})");
        }

        return new LiveConnection(service, HttpDecisionPort.ForService(service, deadlineMs), HttpJobPort.ForService(service));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decisions.Dispose();
        _jobs.Dispose();
        try
        {
            // ADR-0006: the shutdown endpoint unloads the model (frees VRAM), then the process is killed.
            _service.StopAsync(LocalAiService.StopTimeout).GetAwaiter().GetResult();
        }
        finally
        {
            _service.Dispose();
        }
    }
}
