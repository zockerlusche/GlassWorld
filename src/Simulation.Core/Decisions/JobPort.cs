namespace GlassWorld.Simulation.Decisions;

/// <summary>
/// The core's connection to generation jobs of the AI service (B7 job envelope: invention, build plan,
/// process …). Jobs go out as validated <c>GenerationJob</c> JSON; their final <c>JobStatus</c> (done or
/// failed) or a <c>ServiceError</c> comes back whenever it is ready (B2487) and takes effect at a tick,
/// recorded in the job log for replays. The port never produces a result of its own.
/// </summary>
public interface IJobPort
{
    /// <summary>Submits a job (JSON already validated against <c>generation-job</c>).</summary>
    void Submit(string requestId, string jobJson);

    /// <summary>Final job states (or service errors) that are ready at <paramref name="tick"/>, in arrival order.</summary>
    IReadOnlyList<PortMessage> Receive(long tick);
}

/// <summary>Replays job outcomes from a recorded log: each takes effect at the tick it took effect originally.</summary>
public sealed class ReplayJobPort : IJobPort
{
    private readonly ILookup<long, PortMessage> _byTick;

    /// <summary>Creates the port from the job log of an earlier run.</summary>
    public ReplayJobPort(DecisionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _byTick = log.Entries
            .Where(e => e.Kind is DecisionLogKind.JobResult or DecisionLogKind.JobError or DecisionLogKind.JobDiscarded)
            .ToLookup(e => e.Tick, e => new PortMessage(e.RequestId, e.Json, e.Kind == DecisionLogKind.JobError && e.Note == ReplayNotes.ServiceError));
    }

    /// <inheritdoc/>
    public void Submit(string requestId, string jobJson)
    {
        // Outcomes come from the log only.
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick) => [.. _byTick[tick]];
}
