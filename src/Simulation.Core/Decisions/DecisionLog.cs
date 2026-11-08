namespace GlassWorld.Simulation.Decisions;

/// <summary>Kind of a decision log entry.</summary>
public enum DecisionLogKind
{
    /// <summary>A request was sent.</summary>
    Request,

    /// <summary>A valid answer took effect.</summary>
    Response,

    /// <summary>The decider reported an error, or its answer violated the contract.</summary>
    Error,

    /// <summary>An answer arrived but no longer fitted (question closed, option unknown).</summary>
    Discarded,

    /// <summary>A player command took effect (part of the replay, it changes the world).</summary>
    Command,

    /// <summary>A generation job the core submitted (job log).</summary>
    JobRequest,

    /// <summary>A final job state that took effect (job log).</summary>
    JobResult,

    /// <summary>A service error or failed job that took effect (job log).</summary>
    JobError,

    /// <summary>A job outcome the core did not use (unknown job, contract violation).</summary>
    JobDiscarded,
}

/// <summary>One entry of the authoritative replay log (ADR-0006: the core keeps it).</summary>
/// <param name="Kind">What happened.</param>
/// <param name="RequestId">Request id.</param>
/// <param name="Tick">Tick at which the entry was written (request: sent; others: took effect).</param>
/// <param name="Json">Raw JSON of the request or of the received message.</param>
/// <param name="Note">Short reason for errors and discards.</param>
public sealed record DecisionLogEntry(DecisionLogKind Kind, string RequestId, long Tick, string Json, string? Note);

/// <summary>
/// Everything that went to and came from the decider, with the tick it took effect. With the world seed
/// this log reproduces a run exactly (<see cref="ReplayDecisionPort"/>), whatever the hardware.
/// </summary>
public sealed class DecisionLog
{
    private readonly List<DecisionLogEntry> _entries = [];

    /// <summary>All entries in order.</summary>
    public IReadOnlyList<DecisionLogEntry> Entries => _entries;

    /// <summary>Appends an entry.</summary>
    public void Add(DecisionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }
}

/// <summary>Plays the received messages of a log back at the ticks they took effect.</summary>
public sealed class ReplayDecisionPort : IDecisionPort
{
    private readonly ILookup<long, PortMessage> _byTick;

    /// <summary>Creates the replay from a recorded log.</summary>
    public ReplayDecisionPort(DecisionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _byTick = log.Entries
            .Where(e => e.Kind is DecisionLogKind.Response or DecisionLogKind.Error or DecisionLogKind.Discarded)
            .ToLookup(e => e.Tick, e => new PortMessage(e.RequestId, e.Json, e.Kind == DecisionLogKind.Error && e.Note == ReplayNotes.ServiceError));
    }

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        // Answers come from the log only.
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick) => [.. _byTick[tick]];
}

/// <summary>Notes used in the log.</summary>
public static class ReplayNotes
{
    /// <summary>The decider answered with a ServiceError.</summary>
    public const string ServiceError = "service_error";

    /// <summary>The answer violated the contract.</summary>
    public const string ContractViolation = "contract_violation";

    /// <summary>The question was no longer open.</summary>
    public const string QuestionClosed = "question_closed";

    /// <summary>The chosen option was not offered.</summary>
    public const string UnknownOption = "unknown_option";

    /// <summary>A passing option (warn, show, teach) no longer had anything to pass on when the answer came.</summary>
    public const string NothingToPass = "nothing_to_pass";
}
