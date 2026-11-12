using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Player;

/// <summary>Connection to the AI service for conversations (B6 R13): shaped like <see cref="IDecisionPort"/>.</summary>
public interface IConversationPort
{
    /// <summary>Sends a conversation request (JSON of the contract conversation-request).</summary>
    void Send(string requestId, string requestJson);

    /// <summary>Answers or errors that arrived (taken in the tick that reads them).</summary>
    IReadOnlyList<PortMessage> Receive(long tick);
}

/// <summary>Kind of a conversation log entry.</summary>
public enum ConversationLogKind
{
    /// <summary>A request sent.</summary>
    Request,

    /// <summary>An answer taken.</summary>
    Response,

    /// <summary>An error taken (service off, timeout).</summary>
    Error,
}

/// <summary>One logged conversation message (replays take answers only from here).</summary>
/// <param name="Kind">What it is.</param>
/// <param name="RequestId">The request.</param>
/// <param name="Tick">Tick it was sent or taken.</param>
/// <param name="Json">The message.</param>
public sealed record ConversationLogEntry(ConversationLogKind Kind, string RequestId, long Tick, string Json);

/// <summary>A line of a conversation an inhabitant remembers.</summary>
/// <param name="FromPlayer">True if the player said it.</param>
/// <param name="Tick">When.</param>
/// <param name="Locale">Language of the text.</param>
/// <param name="Text">What was said.</param>
public sealed record ConversationLineState(bool FromPlayer, long Tick, string Locale, string Text);

/// <summary>What an inhabitant keeps from talking with the player.</summary>
/// <param name="Understanding">How well it understands the player (0..1, grows from gestures to sentences).</param>
/// <param name="Fear">Fear of the player (trust and respect live in the relationships, other id "player").</param>
/// <param name="Lines">Recent lines, oldest first, at most the data's count.</param>
public sealed record ConversationMemory(double Understanding, double Fear, IReadOnlyList<ConversationLineState> Lines);

/// <summary>A sentence of the player waiting for an answer.</summary>
/// <param name="RequestId">Request sent for it.</param>
/// <param name="InhabitantId">Who was spoken to.</param>
/// <param name="Tick">When it was said.</param>
public sealed record OpenConversation(string RequestId, string InhabitantId, long Tick);

/// <summary>Saved conversation state.</summary>
/// <param name="Memories">Per inhabitant.</param>
/// <param name="Open">Sentences waiting for an answer.</param>
/// <param name="Counter">Request counter (ids).</param>
/// <param name="Log">Requests and answers (replay).</param>
public sealed record ConversationState(IReadOnlyDictionary<string, ConversationMemory> Memories, IReadOnlyList<OpenConversation> Open, long Counter, IReadOnlyList<ConversationLogEntry> Log)
{
    /// <summary>The conversation history per inhabitant for its memory page (B8 R16); null in save games from before.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ConversationLineState>>? History { get; init; }
}

/// <summary>
/// Talking with an inhabitant (B6 R13, A1342; D333). The player's sentence becomes the command send_text:
/// it goes through the content guard first (refused text costs no favour), costs a little favour and
/// asks the AI service through <see cref="IConversationPort"/>. The answer is only a proposal (B2462):
/// the core checks it (contract, request, content guard, expression vocabulary) and computes every change
/// to understanding, fear and the relationship to the player from the data's rules, never from numbers
/// of the answer. Not understood becomes a question to the AI with a question mark (B2511); no answer
/// gives a calm hint and the favour back (R16). The state is optional: worlds without conversations
/// keep their hash.
/// </summary>
public sealed class ConversationSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Player command: say something to an inhabitant.</summary>
    public const string SendText = "send_text";

    /// <summary>Reason: the content guard refused the text.</summary>
    public const string ReasonRefused = "msg.command.text_refused";

    /// <summary>Reason: nothing to say.</summary>
    public const string ReasonEmpty = "msg.command.text_empty";

    /// <summary>Reason: the inhabitant has not answered the last sentence yet.</summary>
    public const string ReasonOpen = "msg.command.conversation_open";

    /// <summary>Text key of the calm line when no answer came (timeout, error, service stopping); DE/EN in content/i18n.</summary>
    public const string NoAnswerKey = "msg.converse.no_answer";

    /// <summary>Other id of the player in an inhabitant's relationships.</summary>
    public const string PlayerId = "player";

    /// <summary>Intervention id of a conversation in the favour costs.</summary>
    public const string Intervention = "conversation";

    private readonly ConversationSettings _settings;
    private readonly KnowledgeSettings _knowledge;
    private readonly InhabitantsSystem _inhabitants;
    private readonly PlayerSystem _player;
    private readonly DecisionsSystem _decisions;
    private readonly TextGuard _guard;
    private readonly IReadOnlySet<string> _expressions;
    private string _displayLocale;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, ConversationMemory> _memories = new(StringComparer.Ordinal);

    // The conversation history per inhabitant for its memory page (B8 R16): every remembered line, at most the data's
    // history_lines_max, oldest dropped first. State like the memories (hashed, saved).
    private readonly SortedDictionary<string, List<ConversationLineState>> _history = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, OpenConversation> _open = new(StringComparer.Ordinal);
    private readonly List<ConversationLogEntry> _log = [];
    private long _counter;

    /// <summary>Creates the system; the AI connection is set later through <see cref="Port"/>.</summary>
    public ConversationSystem(ConversationSettings settings, KnowledgeSettings knowledge, InhabitantsSystem inhabitants, PlayerSystem player, DecisionsSystem decisions, TextGuard guard, IReadOnlySet<string> expressions, string displayLocale, int ticksPerHour)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _expressions = expressions ?? throw new ArgumentNullException(nameof(expressions));
        _displayLocale = displayLocale;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerHour);
        _ticksPerHour = ticksPerHour;
        var outcomes = settings.Outcomes.Select(o => o.Outcome).ToHashSet();
        if (outcomes.Count != Enum.GetValues<OutcomeChangeOutcome>().Length)
        {
            throw new InvalidDataException("Conversation: every outcome needs exactly one entry of changes.");
        }
    }

    /// <summary>
    /// The connection to the AI service (the game sets it like the decision port; tests the mock). Null =
    /// no service: every sentence ends in no answer after the data's time (R16). Not part of the state.
    /// </summary>
    public IConversationPort? Port { get; set; }

    /// <summary>
    /// The diary of started actions (TL #20472), set by the host: it tells the request since when the speaker
    /// has been doing what it does (W1737). Without it the action counts as begun now.
    /// </summary>
    public InhabitantDiary? Diary { get; set; }

    /// <inheritdoc/>
    public string Name => "conversation";

    /// <summary>
    /// Whether a request is validated against its schema before it leaves (ADR-0009; set by the host from
    /// <see cref="SimulationHostOptions.ValidateRequests"/>): on in tests and check runs, off in the running game.
    /// </summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>Language of the player's sentences and the answers (display_locale; set with the player's language).</summary>
    public string DisplayLocale
    {
        get => _displayLocale;
        internal set => _displayLocale = value;
    }

    /// <inheritdoc/>
    public bool HasState => _memories.Count > 0 || _open.Count > 0 || _history.Count > 0;

    /// <summary>Logged requests and answers.</summary>
    public IReadOnlyList<ConversationLogEntry> Log => _log;

    /// <summary>
    /// The inhabitant's conversations with the player, oldest first, at most the data's history_lines_max (B8 R16): the
    /// player's sentences and the inhabitant's spoken answers as they were said, nothing added.
    /// </summary>
    public IReadOnlyList<ConversationLineState> HistoryOf(string inhabitantId) =>
        _history.TryGetValue(inhabitantId, out var lines) ? [.. lines] : [];

    /// <summary>What an inhabitant keeps from talking with the player (start values if never spoken to).</summary>
    public ConversationMemory MemoryOf(string inhabitantId) =>
        _memories.GetValueOrDefault(inhabitantId) ?? new ConversationMemory(_settings.UnderstandingStart, _settings.FearStart, []);

    /// <summary>Whether a sentence to the inhabitant still waits for an answer.</summary>
    public bool IsWaiting(string inhabitantId) => _open.Values.Any(o => o.InhabitantId == inhabitantId);

    /// <summary>Checks send_text at once (the UI shows the reason).</summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // "player" is the reserved other id of the player in the relationships: never an inhabitant.
        if (command.InhabitantId is null || command.InhabitantId == PlayerId || Find(command.InhabitantId) is null)
        {
            return new CommandResult(false, "msg.command.no_inhabitant");
        }

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return new CommandResult(false, ReasonEmpty);
        }

        if (!_guard.Check(command.Text).Allowed)
        {
            return new CommandResult(false, ReasonRefused);
        }

        if (IsWaiting(command.InhabitantId))
        {
            return new CommandResult(false, ReasonOpen);
        }

        return _player.CanAfford(Intervention) ? new CommandResult(true, null) : new CommandResult(false, PlayerSystem.ReasonNoFavor);
    }

    /// <summary>Applies send_text at its tick (checked again: things may have changed).</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!Check(command).Accepted)
        {
            return;
        }

        var inhabitant = _inhabitants.Get(command.InhabitantId!);
        var text = command.Text!.Trim();
        _player.Spend(Intervention);
        var memory = MemoryOf(inhabitant.Id);
        var request = Request(world, inhabitant, memory, text);
        _memories[inhabitant.Id] = Remember(inhabitant.Id, memory, new ConversationLineState(true, world.Tick, _displayLocale, text));
        _open[request.RequestId] = new OpenConversation(request.RequestId, inhabitant.Id, world.Tick);
        var json = ContractSerializer.Serialize(request, ValidateRequests);
        Append(new ConversationLogEntry(ConversationLogKind.Request, request.RequestId, world.Tick, json));
        _inhabitants.Report(new WorldEvent("conversation_started", world.Tick, inhabitant.Id, null, null) { Text = text });
        Port?.Send(request.RequestId, json);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var message in Port?.Receive(world.Tick) ?? [])
        {
            Take(world, message);
        }

        // No answer in time (service off or too slow): calm hint and the favour back (R16).
        var limit = (long)Math.Ceiling(_settings.NoAnswerAfterHours * _ticksPerHour);
        foreach (var open in _open.Values.Where(o => world.Tick - o.Tick >= limit).ToList())
        {
            NoAnswer(world, open);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_memories.Count);
        foreach (var (id, memory) in _memories)
        {
            hasher.Write(id).Write(memory.Understanding).Write(memory.Fear).Write(memory.Lines.Count);
            foreach (var line in memory.Lines)
            {
                hasher.Write(line.FromPlayer ? 1 : 0).Write(line.Tick).Write(line.Locale).Write(line.Text);
            }
        }

        // The memory pages' history only when there is one: worlds without it hash as before.
        if (_history.Count > 0)
        {
            hasher.Write(_history.Count);
            foreach (var (id, lines) in _history)
            {
                hasher.Write(id).Write(lines.Count);
                foreach (var line in lines)
                {
                    hasher.Write(line.FromPlayer ? 1 : 0).Write(line.Tick).Write(line.Locale).Write(line.Text);
                }
            }
        }

        hasher.Write(_open.Count);
        foreach (var open in _open.Values)
        {
            hasher.Write(open.RequestId).Write(open.InhabitantId).Write(open.Tick);
        }
    }

    /// <summary>The state to save.</summary>
    public ConversationState Capture() => new(new SortedDictionary<string, ConversationMemory>(_memories, StringComparer.Ordinal), [.. _open.Values], _counter, [.. _log])
    {
        History = _history.Count == 0 ? null : _history.ToDictionary(h => h.Key, h => (IReadOnlyList<ConversationLineState>)[.. h.Value], StringComparer.Ordinal),
    };

    /// <summary>Restores saved state (null = a save game from before conversations).</summary>
    public void Restore(ConversationState? state)
    {
        _memories.Clear();
        _history.Clear();
        _open.Clear();
        _log.Clear();
        _counter = state?.Counter ?? 0;
        foreach (var (id, memory) in state?.Memories ?? new Dictionary<string, ConversationMemory>())
        {
            _memories[id] = memory;
        }

        foreach (var open in state?.Open ?? [])
        {
            _open[open.RequestId] = open;
        }

        _log.AddRange(state?.Log ?? []);
        foreach (var (id, lines) in state?.History ?? new Dictionary<string, IReadOnlyList<ConversationLineState>>())
        {
            _history[id] = [.. lines];
        }
    }

    private ConversationRequest Request(World world, Inhabitant inhabitant, ConversationMemory memory, string text)
    {
        _counter++;
        var start = new Relation(_knowledge.RelationshipStart.Affection, _knowledge.RelationshipStart.Trust, _knowledge.RelationshipStart.Respect);
        var relation = inhabitant.Relationships.Get(PlayerId, start);
        var seen = _decisions.PerceivedTypes(inhabitant);
        return new ConversationRequest
        {
            ContractVersion = ContractInfo.Version,
            RequestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id, _counter),
            Tick = world.Tick,
            // Created on the first sentence only (a new stream changes the hash), so worlds without
            // conversations keep theirs.
            Seed = world.Random.Get("conversation/seed").NextInt(0, int.MaxValue),
            DeadlineMs = _settings.DeadlineMs,
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Speaker = new Speaker
            {
                Id = inhabitant.Id,

                // B4a R5: its memory, as in the decision request (the summary and the newest events).
                Memory = InhabitantStates.MemoryOf(inhabitant, _inhabitants.LabelOf),
                Needs = [.. inhabitant.Needs.Select(n => new Level { Id = n.Key, Value = n.Value })],
                Mood = [.. inhabitant.Moods.Where(m => m.Value > 0).Select(m => new Level { Id = m.Key, Value = m.Value })],
                Traits = [.. inhabitant.Traits.Select(t => new Level { Id = t.Key, Value = t.Value })],
                WillToLive = inhabitant.WillToLive,
                Understanding = memory.Understanding,
                RelationToPlayer = new RelationToPlayer { Trust = relation.Trust, Awe = relation.Respect, Fear = memory.Fear },

                // W1737: what it does, knows and sees, so the answer stays with what is there. Beliefs only,
                // never the truth.
                Doing = Doing(world, inhabitant),
                Knowledge = Known(inhabitant, seen),
                Sees = [.. seen.Take(SeesMax).Order(StringComparer.Ordinal)],
                SeesLabels = SeenLabels(inhabitant, seen),
            },
            PlayerText = text,
            RecentLines = [.. memory.Lines.Select(l => new ConversationLine
            {
                Author = l.FromPlayer ? ConversationLineAuthor.Player : ConversationLineAuthor.Inhabitant,
                Tick = l.Tick,
                Text = new GeneratedText { Locale = Enum.Parse<Locale>(l.Locale, ignoreCase: true), Text = l.Text },
            })],
        };
    }

    // Limits of the conversation request contract (maxItems of speaker.knowledge and speaker.sees).
    private const int KnowledgeMax = 24;
    private const int SeesMax = 16;

    /// <summary>
    /// What the speaker is doing (W1737): its action, the thing or inhabitant it is about, since when, and the
    /// step it has planned. Null while it does nothing. A thing is named by its type, as the speaker sees it.
    /// </summary>
    private ConversationDoing? Doing(World world, Inhabitant inhabitant)
    {
        if (inhabitant.Activity is not { } activity)
        {
            return null;
        }

        // The diary notes an action when it starts (after the walk to its target): while the action itself runs,
        // the speaker's last entry is this one. On the way there it has only just set out.
        var started = activity.RemainingTicks is not null && Diary?.Of(inhabitant.Id) is { Count: > 0 } entries ? entries[^1] : null;
        if (started is not null && !(string.Equals(started.Action, activity.Action, StringComparison.Ordinal)
            && string.Equals(started.TargetId, activity.TargetId, StringComparison.Ordinal)))
        {
            started = null;
        }

        var target = About(activity.TargetId, started?.TargetType);
        var planned = inhabitant.Planned is { } next ? About(next.TargetId, null) : null;
        return new ConversationDoing
        {
            Action = activity.Action,
            Target = target,
            Label = LabelOf(inhabitant, target),
            SinceTick = started?.Tick ?? world.Tick,
            Planned = inhabitant.Planned is { } step ? new ConversationStep { Action = step.Action, Target = planned, Label = LabelOf(inhabitant, planned) } : null,
        };
    }

    /// <summary>What the speaker calls the kind of thing a subject names (an invention it knows something about); null otherwise.</summary>
    private LocalizedName? LabelOf(Inhabitant inhabitant, Subject? subject) =>
        subject is { Kind: SubjectKind.Type } ? _inhabitants.TypeLabelOf(inhabitant, subject.Id) : null;

    /// <summary>
    /// The speaker's own names for the kinds of things it sees that the data do not name (the entries of
    /// <c>sees</c> it has a name for), sorted by type; null when it has none.
    /// </summary>
    private List<SeenLabel>? SeenLabels(Inhabitant inhabitant, IReadOnlyList<string> seen)
    {
        var labels = seen.Take(SeesMax).Order(StringComparer.Ordinal)
            .Select(type => (Type: type, Label: _inhabitants.TypeLabelOf(inhabitant, type)))
            .Where(x => x.Label is not null)
            .Select(x => new SeenLabel { Type = x.Type, Label = x.Label! })
            .ToList();
        return labels.Count > 0 ? labels : null;
    }

    private Subject? About(string? targetId, string? knownType)
    {
        if (targetId is null)
        {
            return null;
        }

        if (_inhabitants.All.Any(i => string.Equals(i.Id, targetId, StringComparison.Ordinal)))
        {
            return new Subject { Kind = SubjectKind.Inhabitant, Id = targetId };
        }

        return (knownType ?? _decisions.TypeOf(targetId)) is { } type ? new Subject { Kind = SubjectKind.Type, Id = type } : null;
    }

    /// <summary>
    /// What the speaker believes (W1737): first about what it sees, chosen as in the decision request, then its
    /// newest other beliefs, up to the contract's limit.
    /// </summary>
    private List<KnowledgeEntry> Known(Inhabitant inhabitant, IReadOnlyList<string> seen)
    {
        var known = (InhabitantStates.Of(inhabitant, _knowledge, seen, [], null, _inhabitants.TypeLabelOf).Knowledge ?? []).Take(KnowledgeMax).ToList();
        var seenTypes = seen.ToHashSet(StringComparer.Ordinal);
        var visible = _knowledge.Looking?.VisibleProperties.ToHashSet(StringComparer.Ordinal) ?? [];
        known.AddRange(inhabitant.Beliefs.All

            // As in the first part: what a thing is not by its look says little and would crowd out the rest.
            .Where(b => !seenTypes.Contains(b.TypeId) && (b.Belief.Value || !visible.Contains(b.PropertyId)))
            .OrderByDescending(b => b.Belief.Tick)
            .ThenBy(b => b.TypeId, StringComparer.Ordinal)
            .ThenBy(b => b.PropertyId, StringComparer.Ordinal)
            .Take(KnowledgeMax - known.Count)
            .Select(b => InhabitantStates.Entry(inhabitant, b.TypeId, b.PropertyId, b.Belief, _inhabitants.TypeLabelOf)));
        return known;
    }

    private void Take(World world, PortMessage message)
    {
        if (!_open.TryGetValue(message.RequestId, out var open))
        {
            return; // late or unknown: nothing waits for it any more
        }

        Append(new ConversationLogEntry(message.IsError ? ConversationLogKind.Error : ConversationLogKind.Response, message.RequestId, world.Tick, message.Json));
        if (message.IsError)
        {
            NoAnswer(world, open);
            return;
        }

        ConversationResponse response;
        try
        {
            response = ContractSerializer.Deserialize<ConversationResponse>(message.Json);
        }
        catch (ContractViolationException)
        {
            NoAnswer(world, open);
            return;
        }

        if (response.RequestId != open.RequestId)
        {
            NoAnswer(world, open);
            return;
        }

        _open.Remove(open.RequestId);
        if (Find(open.InhabitantId) is not { } inhabitant)
        {
            return; // the inhabitant is gone (B9): nobody answers any more
        }

        // The answer is a proposal: text only if the guard allows it, expressions only from the vocabulary.
        var reply = response.Reply is { } r && !string.IsNullOrWhiteSpace(r.Text) && _guard.Check(r.Text).Allowed ? r : null;
        var expression = response.Expression is { } e && _expressions.Contains(e) ? e : null;
        var outcome = response.Outcome switch
        {
            ConversationResponseOutcome.Reply when reply is null => OutcomeChangeOutcome.Gesture,
            ConversationResponseOutcome.Reply => OutcomeChangeOutcome.Reply,
            ConversationResponseOutcome.Gesture => OutcomeChangeOutcome.Gesture,
            ConversationResponseOutcome.Decline => OutcomeChangeOutcome.Decline,
            _ => OutcomeChangeOutcome.Not_understood,
        };
        Change(inhabitant, outcome, reply is null ? null : new ConversationLineState(false, world.Tick, reply.Locale.ToString().ToLowerInvariant(), reply.Text));
        switch (outcome)
        {
            case OutcomeChangeOutcome.Reply:
                _inhabitants.Report(new WorldEvent("said", world.Tick, inhabitant.Id, null, null) { Text = reply!.Text, Detail = expression });
                break;
            case OutcomeChangeOutcome.Gesture:
                _inhabitants.Report(new WorldEvent("gestured", world.Tick, inhabitant.Id, null, null) { Detail = expression });
                break;
            case OutcomeChangeOutcome.Decline:
                _inhabitants.Report(new WorldEvent("declined", world.Tick, inhabitant.Id, null, null) { Text = reply?.Text, Detail = expression });
                break;
            default:
                // The asking back is said like a decline (W1701): the guarded sentence goes with the event.
                _inhabitants.Report(new WorldEvent("not_understood", world.Tick, inhabitant.Id, null, null) { Text = reply?.Text, Detail = expression });
                _decisions.NoticePlayer(world, inhabitant.Id); // B2511: a question with a question mark
                break;
        }
    }

    private void NoAnswer(World world, OpenConversation open)
    {
        _open.Remove(open.RequestId);
        if (_settings.RefundOnNoAnswer)
        {
            _player.Refund(Intervention);
        }

        if (Find(open.InhabitantId) is { } inhabitant)
        {
            Change(inhabitant, OutcomeChangeOutcome.No_answer, null);
        }

        // A calm line instead of nothing (W1701): the text is a key, the game shows it in the player's language.
        _inhabitants.Report(new WorldEvent("no_answer", world.Tick, open.InhabitantId, null, null) { Text = NoAnswerKey });
    }

    /// <summary>Applies the data's changes for an outcome (never numbers from the answer, B2462).</summary>
    private void Change(Inhabitant inhabitant, OutcomeChangeOutcome outcome, ConversationLineState? line)
    {
        var rule = _settings.Outcomes.Single(o => o.Outcome == outcome);
        var memory = MemoryOf(inhabitant.Id);
        memory = memory with
        {
            Understanding = Math.Clamp(memory.Understanding + rule.Understanding, 0, _settings.UnderstandingMax),
            Fear = Math.Clamp(memory.Fear + rule.Fear, 0, 1),
        };
        _memories[inhabitant.Id] = line is null ? memory : Remember(inhabitant.Id, memory, line);
        if (rule.Affection != 0 || rule.Trust != 0 || rule.Respect != 0)
        {
            var start = new Relation(_knowledge.RelationshipStart.Affection, _knowledge.RelationshipStart.Trust, _knowledge.RelationshipStart.Respect);
            var r = inhabitant.Relationships.Get(PlayerId, start);
            inhabitant.Relationships.Set(PlayerId, new Relation(
                Math.Clamp(r.Affection + rule.Affection, 0, 1),
                Math.Clamp(r.Trust + rule.Trust, 0, 1),
                Math.Clamp(r.Respect + rule.Respect, 0, 1)));
        }
    }

    private Inhabitant? Find(string inhabitantId) => _inhabitants.All.FirstOrDefault(i => i.Id == inhabitantId);

    /// <summary>Adds a log entry and keeps the log within the data's bound (entries of waiting sentences stay).</summary>
    private void Append(ConversationLogEntry entry)
    {
        _log.Add(entry);
        var excess = _log.Count - _settings.LogMaxEntries;
        for (var i = 0; excess > 0 && i < _log.Count;)
        {
            if (_open.ContainsKey(_log[i].RequestId))
            {
                i++;
                continue;
            }

            _log.RemoveAt(i);
            excess--;
        }
    }

    private ConversationMemory Remember(string inhabitantId, ConversationMemory memory, ConversationLineState line)
    {
        if (_settings.HistoryLinesMax is > 0 and var most)
        {
            if (!_history.TryGetValue(inhabitantId, out var history))
            {
                _history[inhabitantId] = history = [];
            }

            history.Add(line);
            if (history.Count > most)
            {
                history.RemoveRange(0, history.Count - most);
            }
        }

        var lines = memory.Lines.Append(line).TakeLast(_settings.RecentLinesMax).ToList();
        return memory with { Lines = lines };
    }
}

/// <summary>
/// Stand-in for the AI service in tests and offline runs: answers at the next tick, deterministically from
/// the request (seed and understanding): little understanding gives gestures or not understood, more gives
/// a short reply in the player's language.
/// </summary>
public sealed class MockConversationPort : IConversationPort
{
    private readonly Queue<PortMessage> _ready = new();

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        var request = ContractSerializer.Deserialize<ConversationRequest>(requestJson);
        var roll = (request.Seed % 100) / 100.0;
        var understanding = request.Speaker.Understanding;
        var outcome = roll > understanding + 0.6 ? ConversationResponseOutcome.Not_understood
            : understanding < 0.3 ? ConversationResponseOutcome.Gesture
            : ConversationResponseOutcome.Reply;
        var reply = outcome == ConversationResponseOutcome.Reply
            ? new GeneratedText { Locale = request.DisplayLocale, Text = request.DisplayLocale == Locale.De ? "Hm, ja." : "Hm, yes." }
            : null;
        var response = new ConversationResponse
        {
            ContractVersion = request.ContractVersion,
            RequestId = request.RequestId,
            Tick = request.Tick,
            Outcome = outcome,
            Reply = reply,
            Expression = outcome == ConversationResponseOutcome.Gesture ? "wave" : null,
            Rationale = "mock",
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
        };
        _ready.Enqueue(new PortMessage(requestId, ContractSerializer.Serialize(response), IsError: false));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick)
    {
        var messages = _ready.ToList();
        _ready.Clear();
        return messages;
    }
}

/// <summary>Replays answers and errors from a conversation log at their logged ticks (deterministic replay).</summary>
public sealed class ReplayConversationPort(IEnumerable<ConversationLogEntry> log) : IConversationPort
{
    private readonly List<ConversationLogEntry> _answers = [.. log.Where(e => e.Kind != ConversationLogKind.Request)];

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick) =>
        [.. _answers.Where(e => e.Tick == tick).Select(e => new PortMessage(e.RequestId, e.Json, e.Kind == ConversationLogKind.Error))];
}
