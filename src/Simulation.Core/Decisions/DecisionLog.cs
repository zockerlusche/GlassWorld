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

    /// <summary>How many entries carry a note (e.g. <see cref="ReplayNotes.RequestRejected"/>).</summary>
    public int CountOf(string note)
    {
        var count = 0;
        foreach (var entry in _entries)
        {
            count += string.Equals(entry.Note, note, StringComparison.Ordinal) ? 1 : 0;
        }

        return count;
    }

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
            .ToLookup(e => e.Tick, e => new PortMessage(e.RequestId, e.Json, e.Kind == DecisionLogKind.Error && ReplayNotes.IsServiceError(e.Note)));
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

    /// <summary>
    /// The service refused the request as invalid (a ServiceError with code <c>bad_request</c>): its own
    /// name among the errors, because the running game does not validate what it sends (ADR-0009) and a
    /// refusal is the sign that a request form and its contract have come apart. Counted per world
    /// (<see cref="SimulationHost.RejectedRequests"/>).
    /// </summary>
    public const string RequestRejected = "request_rejected";

    /// <summary>The note for an error message of the service: <see cref="RequestRejected"/> or <see cref="ServiceError"/>.</summary>
    public static string OfError(string errorJson) => IsRejection(errorJson) ? RequestRejected : ServiceError;

    /// <summary>Whether a log note marks an error message of the service (either name).</summary>
    public static bool IsServiceError(string? note) =>
        string.Equals(note, ServiceError, StringComparison.Ordinal) || string.Equals(note, RequestRejected, StringComparison.Ordinal);

    /// <summary>
    /// Whether an error message of the service says that it refused the request as invalid: its
    /// <c>code</c> is <c>bad_request</c>. Read leniently (the conversation port also hands on error bodies
    /// that are no complete ServiceError); anything unreadable is no refusal.
    /// </summary>
    public static bool IsRejection(string errorJson)
    {
        ArgumentNullException.ThrowIfNull(errorJson);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(errorJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(code.GetString(), "bad_request", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The answer violated the contract.</summary>
    public const string ContractViolation = "contract_violation";

    /// <summary>The question was no longer open.</summary>
    public const string QuestionClosed = "question_closed";

    /// <summary>The chosen option was not offered.</summary>
    public const string UnknownOption = "unknown_option";

    /// <summary>A passing option (warn, show, teach) no longer had anything to pass on when the answer came.</summary>
    public const string NothingToPass = "nothing_to_pass";

    /// <summary>An action at the frontier (B2-6 R14) had no unseen land left to walk to when the answer came.</summary>
    public const string NothingToExplore = "nothing_to_explore";
}
