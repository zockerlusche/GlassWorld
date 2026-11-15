using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Night;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A dialogue job that is out: who began, with whom, and the limits sent.</summary>
/// <param name="RequestId">Job id (also the talk's id in its said events).</param>
/// <param name="SpeakerId">The one who began the talk (speaks first).</param>
/// <param name="ListenerId">The one it talks to.</param>
/// <param name="Action">The action that began it (talk_to).</param>
/// <param name="Tick">Tick the job went out.</param>
/// <param name="MaxWords">Words per turn sent.</param>
/// <param name="Locale">Language asked for (de/en).</param>
public sealed record DialogueJob(string RequestId, string SpeakerId, string ListenerId, string Action, long Tick, int MaxWords, string Locale);

/// <summary>Complete state (save games): request counter, open jobs, last talk with words per pair.</summary>
/// <param name="Counter">Request counter.</param>
/// <param name="Jobs">Open jobs.</param>
/// <param name="LastTalk">Tick of the last dialogue job per pair (key: the two ids in order, joined by '|').</param>
public sealed record DialogueState(long Counter, IReadOnlyList<DialogueJob> Jobs, IReadOnlyDictionary<string, long> LastTalk);

/// <summary>
/// A1735 (B2782, D1119 step 3): a talk between two inhabitants. When one starts a talking action (data) with
/// another inhabitant - awake and in reach, the inhabitants system checked that - the core sends one job of
/// kind <c>dialogue</c> with both speakers; the answer is 2-4 turns. The core keeps the turns that pass its
/// checks, in order, and stops at the first that does not: the speaker is one of the two and they take turns
/// (the one who began first), the text is in the language asked for, has at most the words sent and passes
/// the content guard; an expression not in the vocabulary is left out. Each kept turn becomes a
/// <c>said</c> event of its speaker with the other as target, the talk's id, turn and turns.
/// A talk has no effect on the world (PR2): needs, relationship and knowledge go through the action as
/// before. At most one job per pair within the data's hours; a job without an answer in time is dropped -
/// the two talked without words, as without AI. Stays out of the hash while it holds nothing.
/// </summary>
public sealed class DialogueSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>A turn of a speaker who is not one of the two, or not in turn.</summary>
    public const string OutOfTurn = "turn_out_of_turn";

    /// <summary>A turn with more words than were sent.</summary>
    public const string TooLong = "turn_too_long";

    /// <summary>A turn not in the language asked for.</summary>
    public const string WrongLanguage = "turn_wrong_language";

    /// <summary>A turn the content guard refused.</summary>
    public const string Refused = "turn_refused";

    /// <summary>The answer came after the data's hours.</summary>
    public const string TooLate = "dialogue_too_late";

    /// <summary>The kind of the events the turns become.</summary>
    public const string SaidKind = "said";

    private readonly DialogueSettings? _settings;
    private readonly KnowledgeSettings? _knowledge;
    private readonly InhabitantsSystem _inhabitants;
    private readonly TextGuard _guard;
    private readonly IReadOnlySet<string> _expressions;
    private readonly IJobPort? _port;
    private readonly ulong _seed;
    private readonly long _ticksPerHour;
    private readonly SortedDictionary<string, DialogueJob> _jobs = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _lastTalk = new(StringComparer.Ordinal);
    private long _counter;

    /// <summary>Creates the system. <paramref name="jobs"/> null or no dialogue settings = dormant.</summary>
    /// <param name="settings">The dialogue settings (content/player/conversation.json, dialogue).</param>
    /// <param name="knowledge">The knowledge settings (what of an inhabitant goes into a request).</param>
    /// <param name="inhabitants">The inhabitants: their action starts begin talks, their reports carry the turns.</param>
    /// <param name="guard">The content guard.</param>
    /// <param name="expressions">The expression vocabulary.</param>
    /// <param name="jobs">The world's job port, shared with the other job systems.</param>
    /// <param name="log">The world's job log.</param>
    /// <param name="seed">The world's seed (request ids).</param>
    /// <param name="ticksPerHour">Ticks per game hour.</param>
    /// <param name="displayLocale">Language the turns are written in.</param>
    public DialogueSystem(DialogueSettings? settings, KnowledgeSettings? knowledge, InhabitantsSystem inhabitants, TextGuard guard, IReadOnlySet<string> expressions, JobRouter? jobs, DecisionLog log, ulong seed, long ticksPerHour, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerHour);
        _settings = settings;
        _knowledge = knowledge;
        _inhabitants = inhabitants;
        _guard = guard;
        _expressions = expressions;
        _seed = seed;
        _ticksPerHour = ticksPerHour;
        _port = settings is null ? null : jobs?.Claim(_jobs.ContainsKey);
        Log = log;
        DisplayLocale = displayLocale;
        if (_port is not null)
        {
            inhabitants.Reported += OnReported;
        }
    }

    /// <inheritdoc/>
    public string Name => "dialogue";

    /// <inheritdoc/>
    public bool HasState => _counter > 0 || _jobs.Count > 0 || _lastTalk.Count > 0;

    /// <summary>The world's job log.</summary>
    public DecisionLog Log { get; }

    /// <summary>Whether jobs are validated against the contract before they go out (tests may turn it off).</summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>The day requests per game hour the data allow, shared with the decider (A1587); null = no limit.</summary>
    public AiBudget? Budget { get; init; }

    /// <summary>Language the next talk is written in (de/en).</summary>
    public string DisplayLocale { get; set; }

    /// <summary>Open jobs, in id order.</summary>
    public IEnumerable<DialogueJob> Jobs => _jobs.Values;

    /// <summary>
    /// The types an inhabitant perceives, as the decider sees them (review GW2 #483): its knowledge in the job is
    /// taken about these, so the two can talk about what they know; null = none (tests).
    /// </summary>
    public Func<Inhabitant, IReadOnlyList<string>>? PerceivedTypes { get; set; }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_port is null || _settings is null)
        {
            return;
        }

        // A job without an answer in time is dropped first: the two went on without words. The log line is
        // replayed as a message at this tick too, and then finds no open job, like a late answer live.
        var within = (long)Math.Ceiling(_settings.AnswerWithinHours * _ticksPerHour);
        foreach (var job in _jobs.Values.Where(j => world.Tick - j.Tick > within).ToList())
        {
            _jobs.Remove(job.RequestId);
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, job.RequestId, world.Tick, "{}", TooLate));
        }

        foreach (var message in _port.Receive(world.Tick))
        {
            Apply(world, message);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_jobs.Count);
        foreach (var job in _jobs.Values)
        {
            hasher.Write(job.RequestId).Write(job.SpeakerId).Write(job.ListenerId).Write(job.Action).Write(job.Tick).Write(job.MaxWords).Write(job.Locale);
        }

        hasher.Write(_lastTalk.Count);
        foreach (var (pair, tick) in _lastTalk)
        {
            hasher.Write(pair).Write(tick);
        }
    }

    /// <summary>Takes the complete state.</summary>
    public DialogueState Capture() => new(_counter, [.. _jobs.Values], new SortedDictionary<string, long>(_lastTalk, StringComparer.Ordinal));

    /// <summary>Restores a captured state (null = a save game from before: no talk had words yet).</summary>
    public void Restore(DialogueState? state)
    {
        if (state is null)
        {
            return;
        }

        _counter = state.Counter;
        _jobs.Clear();
        foreach (var job in state.Jobs)
        {
            _jobs[job.RequestId] = job;
        }

        _lastTalk.Clear();
        foreach (var (pair, tick) in state.LastTalk)
        {
            _lastTalk[pair] = tick;
        }
    }

    /// <summary>The key of a pair, the same whoever begins.</summary>
    public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    private void OnReported(WorldEvent worldEvent)
    {
        if (_settings is null
            || worldEvent.Kind != InhabitantsSystem.ActionStarted
            || worldEvent.Action is not { } action
            || !_settings.Actions.Contains(action)
            || worldEvent.TargetId is not { } listenerId
            || !_inhabitants.TryGet(worldEvent.InhabitantId, out var speaker)
            || !_inhabitants.TryGet(listenerId, out var listener))
        {
            return;
        }

        var pair = PairKey(speaker.Id, listener.Id);
        var gap = (long)Math.Ceiling(_settings.PerPairHours * _ticksPerHour);
        if (gap > 0 && _lastTalk.TryGetValue(pair, out var last) && worldEvent.Tick - last < gap)
        {
            return;
        }

        // A1587 (B7 R24): over the game hour's budget for day requests the two talk without words this time.
        if (Budget is { } budget && !budget.HasRoom(worldEvent.Tick))
        {
            return;
        }

        _lastTalk[pair] = worldEvent.Tick;
        _counter++;
        var requestId = DecisionIds.RequestId(_seed, worldEvent.Tick, "dialogue/" + speaker.Id, _counter);
        var payload = new DialoguePayload { MaxTurns = _settings.MaxTurns, MaxWords = _settings.MaxWords };
        payload.Speakers.Add(Speaker(speaker, listener));
        payload.Speakers.Add(Speaker(listener, speaker));
        foreach (var expression in _expressions.Order(StringComparer.Ordinal))
        {
            payload.Expressions.Add(expression);
        }

        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = worldEvent.Tick,
            Seed = DecisionIds.SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(DisplayLocale, ignoreCase: true),
            Kind = JobKind.Dialogue,
            Priority = _settings.JobPriority,
            Inhabitant = null,
            Object = null,
            Payload = new JobPayload { Dialogue = payload },
        };
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, worldEvent.Tick, json, null));
        _port!.Submit(requestId, json);
        Budget?.Note(worldEvent.Tick);
        _jobs[requestId] = new DialogueJob(requestId, speaker.Id, listener.Id, action, worldEvent.Tick, _settings.MaxWords, DisplayLocale.ToLowerInvariant());
    }

    /// <summary>
    /// One speaker as the service sees it: its state as the decider builds it - knowledge about what it perceives, its
    /// own names for things and needs, its doubts - and the name it learned for the other (else none - then "du").
    /// </summary>
    private DialogueSpeaker Speaker(Inhabitant who, Inhabitant other) => new()
    {
        Inhabitant = InhabitantStates.Of(who, _knowledge, PerceivedTypes?.Invoke(who) ?? [], [other.Id], _inhabitants.LabelOf, _inhabitants.TypeLabelOf, _inhabitants.NeedLabelOf, _inhabitants.UnsureOf),
        CallsOther = who.KnownNames.Contains(other.Id) && !string.IsNullOrWhiteSpace(other.Name) ? new LocalizedName { De = other.Name, En = other.Name } : null,
        LastThought = null,
    };

    private void Apply(World world, PortMessage message)
    {
        if (!_jobs.Remove(message.RequestId, out var job))
        {
            return;
        }

        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.OfError(message.Json)));
            return;
        }

        JobStatus status;
        try
        {
            status = ContractSerializer.Deserialize<JobStatus>(message.Json);
        }
        catch (ContractViolationException)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.ContractViolation));
            return;
        }

        if (status.State == JobState.Failed && status.JobId == message.RequestId && status.Kind == JobKind.Dialogue)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString()));
            return;
        }

        if (!(status.State == JobState.Done && status.JobId == message.RequestId && status.Kind == JobKind.Dialogue && status.Result?.Dialogue is not null))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, NightJobsSystem.NotFinal));
            return;
        }

        var (kept, reason) = Check(job, status.Result.Dialogue.Turns);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, message.RequestId, world.Tick, message.Json, reason is null ? null : NightJobsSystem.Rejected + reason));
        for (var i = 0; i < kept.Count; i++)
        {
            var turn = kept[i];
            var target = turn.Speaker == job.SpeakerId ? job.ListenerId : job.SpeakerId;
            var expression = turn.Expression is { } e && _expressions.Contains(e) ? e : null;
            _inhabitants.Report(new WorldEvent(SaidKind, world.Tick, turn.Speaker, job.Action, target)
            {
                Text = turn.Text.Text.Trim(),
                Detail = expression,
                Conversation = job.RequestId,
                Turn = i + 1,
                Turns = kept.Count,
            });
        }
    }

    /// <summary>The turns that pass, in order up to the first that does not, and why that one failed (null = all passed).</summary>
    private (List<DialogueTurn> Kept, string? Reason) Check(DialogueJob job, IEnumerable<DialogueTurn> turns)
    {
        var kept = new List<DialogueTurn>();
        foreach (var turn in turns)
        {
            var expected = kept.Count % 2 == 0 ? job.SpeakerId : job.ListenerId;
            string? reason = turn.Speaker != expected ? OutOfTurn
                : !string.Equals(turn.Text.Locale.ToString(), job.Locale, StringComparison.OrdinalIgnoreCase) ? WrongLanguage
                : Words(turn.Text.Text) > job.MaxWords ? TooLong
                : !_guard.Check(turn.Text.Text).Allowed ? Refused
                : null;
            if (reason is not null)
            {
                return (kept, reason);
            }

            if (string.IsNullOrWhiteSpace(turn.Text.Text))
            {
                // An empty turn ends the talk there (no words this time), without fault.
                return (kept, null);
            }

            kept.Add(turn);
        }

        return (kept, null);
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
