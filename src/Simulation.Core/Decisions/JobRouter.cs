namespace GlassWorld.Simulation.Decisions;

/// <summary>
/// Shares the world's one job port between the systems that send generation jobs. A port hands out every
/// outcome only once, so each system gets a port of its own from here: <see cref="Claim"/> gives one that
/// receives exactly the outcomes of the jobs its system still waits for, <see cref="Rest"/> one that
/// receives everything nobody claims (the invention system, which also logs outcomes nobody waits for).
/// The inner port is asked once per tick, whoever asks first, so an outcome takes effect in the tick it
/// arrived for every system, live and in a replay.
/// </summary>
public sealed class JobRouter
{
    private readonly IJobPort _inner;
    private readonly List<Func<string, bool>> _claims = [];
    private readonly List<PortMessage> _waiting = [];
    private long _receivedTick = long.MinValue;

    /// <summary>Creates the router around the world's job port.</summary>
    public JobRouter(IJobPort inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        Rest = new Part(this, null);
    }

    /// <summary>The port for everything no other system claims.</summary>
    public IJobPort Rest { get; }

    /// <summary>
    /// A port that receives only the outcomes of jobs <paramref name="waitsFor"/> names (asked when outcomes
    /// arrive, so it also holds after loading a save game).
    /// </summary>
    public IJobPort Claim(Func<string, bool> waitsFor)
    {
        ArgumentNullException.ThrowIfNull(waitsFor);
        _claims.Add(waitsFor);
        return new Part(this, waitsFor);
    }

    private List<PortMessage> Take(long tick, Func<string, bool>? waitsFor)
    {
        if (tick != _receivedTick)
        {
            _receivedTick = tick;
            _waiting.AddRange(_inner.Receive(tick));
        }

        bool Mine(PortMessage message) => waitsFor is null ? !_claims.Any(claim => claim(message.RequestId)) : waitsFor(message.RequestId);
        var taken = _waiting.Where(Mine).ToList();
        foreach (var message in taken)
        {
            _waiting.Remove(message);
        }

        return taken;
    }

    private sealed class Part(JobRouter router, Func<string, bool>? waitsFor) : IJobPort
    {
        public void Submit(string requestId, string jobJson) => router._inner.Submit(requestId, jobJson);

        public IReadOnlyList<PortMessage> Receive(long tick) => router.Take(tick, waitsFor);

        public void SetPhase(string phase, long tick, bool force = false) => router._inner.SetPhase(phase, tick, force);
    }
}
