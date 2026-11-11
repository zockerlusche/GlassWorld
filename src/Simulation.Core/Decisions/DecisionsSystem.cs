using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Decisions;

/// <summary>An open question of an inhabitant (B2487: shown as a question mark until answered).</summary>
/// <param name="Id">Question id, e.g. <c>q-000001</c> (no wording; the UI shows only that there is one).</param>
/// <param name="InhabitantId">Who asks.</param>
/// <param name="Kind">What was not understood (trigger kind).</param>
/// <param name="Subject">Object id or subject key the question is about.</param>
/// <param name="RequestId">Request sent to the decider.</param>
/// <param name="AskedTick">Tick the question was asked.</param>
/// <param name="Options">Options offered (option id → action, target), to check the answer against.</param>
/// <param name="RetryAtTick">After a retryable error: tick at which the question is sent again (B2487).</param>
public sealed record OpenQuestion(string Id, string InhabitantId, string Kind, string Subject, string RequestId, long AskedTick, IReadOnlyList<(string OptionId, string Action, string? TargetId)> Options, long? RetryAtTick = null);

/// <summary>A command from the player, checked by the core at the next tick (ADR-0005).</summary>
/// <param name="Kind">Command kind (<c>inhabitant_clicked</c>, <c>place_gift</c>, ...).</param>
/// <param name="InhabitantId">Inhabitant concerned, or null for commands without one (weather, gifts in B6).</param>
public sealed record PlayerCommand(string Kind, string? InhabitantId)
{
    /// <summary>
    /// The id a command is about: object type of a gift, weather of <c>set_weather</c>, epoch of
    /// <c>set_look_epoch</c> (vocabulary ids).
    /// </summary>
    public string? TypeId { get; init; }

    /// <summary>World position X of a gift (metres).</summary>
    public double? X { get; init; }

    /// <summary>World position Z of a gift (metres).</summary>
    public double? Z { get; init; }

    /// <summary>A whole number for commands that need one (e.g. the start favour).</summary>
    public int? Amount { get; init; }

    /// <summary>Mailbox message a command is about (acknowledge).</summary>
    public string? MessageId { get; init; }

    /// <summary>What the player says to an inhabitant (send_text, B6 R13).</summary>
    public string? Text { get; init; }
}

/// <summary>Result of a player command.</summary>
/// <param name="Accepted">True if the core accepted it.</param>
/// <param name="ReasonKey">Text key with the reason when rejected (DE/EN in content/i18n).</param>
public sealed record CommandResult(bool Accepted, string? ReasonKey);

/// <summary>
/// Questions to the decider (a world system that runs before the inhabitants). Whatever an inhabitant
/// does not understand becomes a question (W1595): an object type it has no belief about, or noticing the
/// player. Answers are collected at the tick boundary, checked against the contract and against the
/// options that were offered, and turn into the inhabitant's next activity; until then the routine layer
/// keeps the inhabitant living (B2487). Every request and answer is logged for replays.
/// </summary>
public sealed class DecisionsSystem : IWorldSystem
{
    /// <summary>The world's weather for the decider's environment (B2 R13); null = fallback values from the settings.</summary>
    public Weather.WeatherSystem? WeatherSource { get; init; }

    /// <summary>Knowledge data (B4a): passing actions as options, knowledge and relationships in the request.</summary>
    public KnowledgeSettings? Knowledge { get; init; }

    /// <summary>
    /// The inhabitants' own name of an invented type (null for content types, which the data names); set by
    /// the host once the invention system exists. Used for the topic of passing options (D625).
    /// </summary>
    public Func<string, LocalizedName?>? InventedName { get; set; }

    /// <summary>Whether the inhabitant has an open question about a type (W1595; the routine waits for the answer).</summary>
    public bool HasOpenQuestion(string inhabitantId, string typeId) =>
        _open.Values.Any(q => q.InhabitantId == inhabitantId && q.Subject == "type:" + typeId);

    /// <summary>Trigger: an object type without any belief.</summary>
    public const string UnknownObject = "unknown_object";

    /// <summary>Trigger: another inhabitant in sight whose own name the inhabitant does not know (B4a R8).</summary>
    public const string UnknownInhabitant = "unknown_inhabitant";

    /// <summary>Trigger: the inhabitant noticed the player (e.g. was clicked).</summary>
    public const string NoticedPlayer = "noticed_player";

    /// <summary>Player command: an inhabitant was clicked.</summary>
    public const string InhabitantClicked = "inhabitant_clicked";

    /// <summary>
    /// Player command: the player switched the language (#20043). Requests from the tick it takes effect
    /// use it; logged like every command, so a replay switches at the same tick.
    /// </summary>
    public const string SetDisplayLocale = "set_display_locale";

    /// <summary>
    /// The language requests use until a <see cref="SetDisplayLocale"/> is logged: every save from before
    /// #20043 asked in it, so replays and restores start with it.
    /// </summary>
    public const string DefaultDisplayLocale = "de";

    /// <summary>Text key: unknown command.</summary>
    public const string ReasonUnknownCommand = "msg.command.unknown";

    /// <summary>Text key: the command names no existing inhabitant.</summary>
    public const string ReasonNoInhabitant = "msg.command.no_inhabitant";

    private readonly SortedDictionary<string, OpenQuestion> _open = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _lastAsked = new(StringComparer.Ordinal);
    private readonly Queue<PlayerCommand> _commands = new();
    private readonly Dictionary<string, (Func<PlayerCommand, CommandResult> Check, Action<World, PlayerCommand> Apply)> _handlers = new(StringComparer.Ordinal);
    private readonly DeciderSettings _settings;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly IDecisionPort _port;
    private string _displayLocale;
    private readonly IReadOnlyDictionary<string, VocabularyEntryTarget> _actionTargets;
    private ILookup<long, PlayerCommand>? _replayCommands;
    private long _counter;

    /// <summary>Creates the system.</summary>
    public DecisionsSystem(DeciderSettings settings, InhabitantsSystem inhabitants, WorldObjects objects, ActionRules actions, UtilityBrain brain, Vocabulary actionVocabulary, IDecisionPort port, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(actionVocabulary);
        _actionTargets = actionVocabulary.Entries.ToDictionary(e => e.Id, e => e.Target ?? VocabularyEntryTarget.None, StringComparer.Ordinal);
        _settings = settings;
        _inhabitants = inhabitants;
        _objects = objects;
        _actions = actions;
        _brain = brain;
        _port = port;
        _displayLocale = displayLocale;
    }

    /// <inheritdoc/>
    public string Name => "decisions";

    /// <summary>Language of the texts the decider writes (display_locale of the requests).</summary>
    public string DisplayLocale
    {
        get => _displayLocale;
        internal set => _displayLocale = value;
    }

    /// <summary>The replay log.</summary>
    public DecisionLog Log { get; } = new();

    /// <summary>Open questions in id order.</summary>
    public IEnumerable<OpenQuestion> Open => _open.Values;

    /// <summary>Open questions of one inhabitant (ids only for the UI window, no wording).</summary>
    public IReadOnlyList<string> OpenQuestionIds(string inhabitantId) =>
        [.. _open.Values.Where(q => q.InhabitantId == inhabitantId).Select(q => q.Id)];

    /// <summary>
    /// Open questions that still puzzle the inhabitant (TL #19921, for the question mark): a question about
    /// a type it has looked at since leaves only the effect open, so it no longer counts.
    /// </summary>
    public IReadOnlyList<string> PuzzlingQuestionIds(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return [.. _open.Values
            .Where(q => q.InhabitantId == inhabitant.Id
                && !(q.Subject.StartsWith(TypeSubject, StringComparison.Ordinal) && InhabitantsSystem.HasLookedAt(Knowledge, inhabitant, q.Subject[TypeSubject.Length..])))
            .Select(q => q.Id)];
    }

    private const string TypeSubject = "type:";

    /// <summary>
    /// Replay mode: player commands come from a recorded log at the ticks they took effect (they change the
    /// world, so they belong to the replay like the answers of the decider).
    /// </summary>
    public void LoadReplay(DecisionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _replayCommands = log.Entries
            .Where(e => e.Kind == DecisionLogKind.Command)
            .ToLookup(e => e.Tick, e => System.Text.Json.JsonSerializer.Deserialize<PlayerCommand>(e.Json)!);
    }

    /// <summary>Complete state for save games.</summary>
    public (long Counter, IReadOnlyList<OpenQuestion> Questions, IReadOnlyList<KeyValuePair<string, long>> LastAsked) CaptureState() =>
        (_counter, [.. _open.Values], [.. _lastAsked]);

    /// <summary>Restores the state of a save game (the log continues where it ended).</summary>
    public void RestoreState(long counter, IEnumerable<OpenQuestion> questions, IEnumerable<KeyValuePair<string, long>> lastAsked, IEnumerable<DecisionLogEntry> log)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(lastAsked);
        ArgumentNullException.ThrowIfNull(log);
        _counter = counter;
        foreach (var question in questions)
        {
            _open.Add(question.Id, question);
        }

        foreach (var (key, tick) in lastAsked)
        {
            _lastAsked.Add(key, tick);
        }

        foreach (var entry in log)
        {
            Log.Add(entry);
        }
    }

    /// <summary>
    /// After loading with a live decider: questions whose request went to the previous process will never
    /// be answered, so they are sent again at the next tick (B2487). Not used for hash comparisons.
    /// </summary>
    public void ResendPendingAfterLoad(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var question in _open.Values.Where(q => q.RetryAtTick is null).ToList())
        {
            _open[question.Id] = question with { RetryAtTick = world.Tick };
        }
    }

    /// <summary>
    /// Registers a command kind handled by another world system (e.g. gifts by the player system): the
    /// check runs at submit, the apply at the tick it takes effect. Every command goes through the same
    /// log, so replays apply it at the same tick.
    /// </summary>
    public void RegisterCommand(string kind, Func<PlayerCommand, CommandResult> check, Action<World, PlayerCommand> apply)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(apply);
        _handlers[kind] = (check, apply);
    }

    /// <summary>
    /// World setup only (before the first tick): applies a registered command at once and logs it at the
    /// current tick, so a save game made right after creation already holds it in the log (GW1 #73 F1).
    /// A replay applies it again at the first tick.
    /// </summary>
    public CommandResult ApplySetup(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        if (!_handlers.TryGetValue(command.Kind, out var handler))
        {
            return new CommandResult(false, ReasonUnknownCommand);
        }

        var result = handler.Check(command);
        if (result.Accepted)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Command, string.Empty, world.Tick, System.Text.Json.JsonSerializer.Serialize(command), null));
            handler.Apply(world, command);
        }

        return result;
    }

    /// <summary>Queues a player command; it takes effect at the next tick.</summary>
    public CommandResult Submit(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_handlers.TryGetValue(command.Kind, out var handler))
        {
            var result = handler.Check(command);
            if (result.Accepted)
            {
                _commands.Enqueue(command);
            }

            return result;
        }

        if (command.Kind != InhabitantClicked)
        {
            return new CommandResult(false, ReasonUnknownCommand);
        }

        if (command.InhabitantId is null || !_inhabitants.All.Any(i => i.Id == command.InhabitantId))
        {
            return new CommandResult(false, ReasonNoInhabitant);
        }

        _commands.Enqueue(command);
        return new CommandResult(true, null);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var message in _port.Receive(world.Tick))
        {
            Apply(world, message);
        }

        if (_replayCommands is not null)
        {
            _commands.Clear();
            foreach (var replayed in _replayCommands[world.Tick])
            {
                _commands.Enqueue(replayed);
            }
        }

        while (_commands.TryDequeue(out var command))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Command, string.Empty, world.Tick, System.Text.Json.JsonSerializer.Serialize(command), null));
            if (_handlers.TryGetValue(command.Kind, out var handler))
            {
                handler.Apply(world, command);
                continue;
            }

            if (command.InhabitantId is not null && Enabled(NoticedPlayer))
            {
                Ask(world, _inhabitants.Get(command.InhabitantId), NoticedPlayer, "player", null);
            }
        }

        foreach (var due in _open.Values.Where(q => q.RetryAtTick is { } at && at <= world.Tick).ToList())
        {
            Resend(world, due);
        }

        if (Enabled(UnknownObject))
        {
            foreach (var inhabitant in _inhabitants.All)
            {
                var unknown = NearestUnknown(inhabitant);
                if (unknown is not null)
                {
                    Ask(world, inhabitant, UnknownObject, "type:" + unknown.TypeId, unknown);
                }
            }
        }

        if (Enabled(UnknownInhabitant))
        {
            // B4a R8: the other is at first an unknown human; who it is becomes a question (W1595), whose
            // options (approach, talk to, introduce ...) are the core's, the choice the decider's.
            // The nearest stranger (ties by id) it may ask about now: one cooling down or asked about
            // unknown_inhabitant_max_asks times already does not hide a second one (review GW3 #207).
            foreach (var inhabitant in _inhabitants.All)
            {
                if (PerceivedInhabitants(inhabitant).FirstOrDefault(o => o.Name is not null && !inhabitant.KnownNames.Contains(o.Id)
                    && MayAsk(world, inhabitant, InhabitantSubject + o.Id) && AsksAbout(inhabitant, o) < (_settings.UnknownInhabitantMaxAsks ?? int.MaxValue)) is { } stranger)
                {
                    var subject = InhabitantSubject + stranger.Id;
                    if (Ask(world, inhabitant, UnknownInhabitant, subject, null))
                    {
                        _lastAsked[AsksKey(inhabitant, stranger)] = AsksAbout(inhabitant, stranger) + 1;
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_open.Count);
        foreach (var q in _open.Values)
        {
            hasher.Write(q.Id).Write(q.InhabitantId).Write(q.Kind).Write(q.Subject).Write(q.RequestId).Write(q.AskedTick).Write(q.RetryAtTick ?? -1).Write(q.Options.Count);
            foreach (var (optionId, action, target) in q.Options)
            {
                hasher.Write(optionId).Write(action).Write(target ?? string.Empty);
            }
        }

        hasher.Write(_lastAsked.Count);
        foreach (var (key, tick) in _lastAsked)
        {
            hasher.Write(key).Write(tick);
        }
    }

    /// <summary>
    /// The inhabitant noticed the player (e.g. did not understand what the player said, B2511): opens a
    /// question to the AI with a question mark, like a click, within the usual limits and cooldown.
    /// </summary>
    public void NoticePlayer(World world, string inhabitantId)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (Enabled(NoticedPlayer))
        {
            Ask(world, _inhabitants.Get(inhabitantId), NoticedPlayer, "player", null);
        }
    }

    private bool Enabled(string kind) =>
        _settings.Triggers.Any(t => t.Enabled && string.Equals(t.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The nearest perceived object whose type the inhabitant has no belief about (ties by id). A single
    /// pass without sorting: this runs every tick for every inhabitant.
    /// </summary>
    private WorldObject? NearestUnknown(Inhabitant inhabitant)
    {
        var radiusSquared = _inhabitants.SightRadiusM * _inhabitants.SightRadiusM;
        WorldObject? best = null;
        var bestDistance = double.MaxValue;
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var obj in _objects.All)
        {
            var dx = obj.Position.X - inhabitant.Position.X;
            var dz = obj.Position.Z - inhabitant.Position.Z;
            var d = (dx * dx) + (dz * dz);
            if (d > radiusSquared || d > bestDistance)
            {
                continue;
            }

            if (!known.TryGetValue(obj.TypeId, out var isKnown))
            {
                isKnown = inhabitant.Beliefs.About(obj.TypeId).Any();
                known[obj.TypeId] = isKnown;
            }

            if (!isKnown && (d < bestDistance || string.CompareOrdinal(obj.Id, best!.Id) < 0))
            {
                best = obj;
                bestDistance = d;
            }
        }

        return best;
    }

    private List<WorldObject> Perceived(Inhabitant inhabitant)
    {
        var radius = _inhabitants.SightRadiusM;
        return [.. _objects.All
            .Select(o => (o, d: Distance(inhabitant.Position, o.Position)))
            .Where(x => x.d <= radius)
            .OrderBy(x => x.d)
            .ThenBy(x => x.o.Id, StringComparer.Ordinal)
            .Select(x => x.o)];
    }

    private const string InhabitantSubject = "inhabitant:";

    /// <summary>
    /// How often the inhabitant has asked who another is (unknown_inhabitant). Kept with the cooldowns under
    /// an own key prefix (value = count, not a tick), so it is saved and hashed with them.
    /// </summary>
    private long AsksAbout(Inhabitant inhabitant, Inhabitant other) => _lastAsked.GetValueOrDefault(AsksKey(inhabitant, other));

    private static string AsksKey(Inhabitant inhabitant, Inhabitant other) => "asks|" + inhabitant.Id + "|" + InhabitantSubject + other.Id;

    /// <summary>Whether a question about the subject may be asked now: room for one more, none open about it, not cooling down.</summary>
    private bool MayAsk(World world, Inhabitant inhabitant, string subject)
    {
        var cooldown = (long)_settings.AskCooldownHours * world.Calendar.Settings.TicksPerHour;
        return _open.Values.Count(q => q.InhabitantId == inhabitant.Id) < _settings.MaxOpenQuestions
            && !_open.Values.Any(q => q.InhabitantId == inhabitant.Id && q.Subject == subject)
            && !(_lastAsked.TryGetValue(inhabitant.Id + "|" + subject, out var last) && world.Tick - last < cooldown);
    }

    private bool Ask(World world, Inhabitant inhabitant, string kind, string subject, WorldObject? target)
    {
        var cooldownKey = inhabitant.Id + "|" + subject;
        if (!MayAsk(world, inhabitant, subject))
        {
            return false;
        }

        _counter++;
        var questionId = string.Create(CultureInfo.InvariantCulture, $"q-{_counter:000000}");
        var question = Send(world, inhabitant, questionId, kind, subject, target);
        _open.Add(question.Id, question);
        _lastAsked[cooldownKey] = world.Tick;
        _inhabitants.Report(new WorldEvent("question_opened", world.Tick, inhabitant.Id, null, question.Id));
        return true;
    }

    private OpenQuestion Send(World world, Inhabitant inhabitant, string questionId, string kind, string subject, WorldObject? target)
    {
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id, _counter);
        var options = BuildOptions(inhabitant, target, world.Tick);
        var json = ContractSerializer.Serialize(BuildRequest(world, inhabitant, requestId, options));
        Log.Add(new DecisionLogEntry(DecisionLogKind.Request, requestId, world.Tick, json, null));
        _port.Send(requestId, json);
        return new OpenQuestion(questionId, inhabitant.Id, kind, subject, requestId, world.Tick, options);
    }

    /// <summary>Sends a question again after a retryable error, with the current state and a new request id.</summary>
    private void Resend(World world, OpenQuestion question)
    {
        _counter++;
        WorldObject? target = null;
        var targetId = question.Options.Select(o => o.TargetId).FirstOrDefault(id => id is not null);
        if (targetId is not null && _objects.TryGet(targetId, out var found))
        {
            target = found;
        }

        var resent = Send(world, _inhabitants.Get(question.InhabitantId), question.Id, question.Kind, question.Subject, target);
        _open[question.Id] = resent with { AskedTick = question.AskedTick };
    }

    /// <summary>Most options a decision request may hold (contract decision-request: options maxItems).</summary>
    public const int MaxOptions = 32;

    /// <summary>
    /// Options the core allows: object actions on the subject and every action without a target (target
    /// kind from the action vocabulary), each only while the asker's needs suggest it (<see cref="Behaviour.UtilityBrain.NeedSuggests"/>)
    /// and it does not rest after repeats without effect (W1696).
    /// </summary>
    private List<(string OptionId, string Action, string? TargetId)> BuildOptions(Inhabitant asker, WorldObject? target, long tick)
    {
        var raw = new List<(string Action, string? TargetId)>();
        var passOn = new List<(string Action, string? TargetId)>();
        var meeting = new List<(string Action, string? TargetId)>();
        foreach (var action in _actions.OfferedActions)
        {
            // Only what the asker's needs suggest (F5275 A): no sleep offered to someone wide awake, so a
            // queued answer cannot chain sleeps while thirsty.
            if (!_brain.NeedSuggests(asker, action, target?.TypeId))
            {
                continue;
            }

            switch (_actionTargets.GetValueOrDefault(action, VocabularyEntryTarget.None))
            {
                case VocabularyEntryTarget.Object when target is not null && !Rests(asker, action, target.Id, tick):
                    raw.Add((action, target.Id));
                    break;
                case VocabularyEntryTarget.None when !Rests(asker, action, null, tick)
                    && (!_inhabitants.AtFrontier(action) || _inhabitants.FrontierOf?.Invoke(asker) is not null):
                    // An action at the frontier (B2-6 R14) only while unseen land can be reached on foot.
                    raw.Add((action, null));
                    break;
                case VocabularyEntryTarget.Inhabitant when Knowledge is { } knowledge:
                    // Passing on knowledge (B4a R4): offered towards someone in sight when the asker knows
                    // something this action carries and the other has not experienced it. The naming action
                    // (W1698) only while the asker does not know the other's name; meeting actions (B4a R8)
                    // while the conditions of their data hold.
                    foreach (var other in PerceivedInhabitants(asker).Take(knowledge.RequestMaxInhabitants))
                    {
                        if (InhabitantsSystem.HasPurpose(knowledge, asker, other, action) && !Rests(asker, action, other.Id, tick))
                        {
                            (IsMeeting(knowledge, action) ? meeting : passOn).Add((action, other.Id));
                        }
                    }

                    break;
                default:
                    // Other inhabitant actions follow with B3/B9; object actions need a subject.
                    break;
            }
        }

        // Passing knowledge on comes after the usual options and never pushes the request over the
        // contract's limit (review GW2 #103): at most the data's number, in a fixed order.
        var room = Math.Max(0, MaxOptions - raw.Count);
        raw.AddRange(passOn.Take(Math.Min(room, Knowledge?.MaxPassOnOptions ?? 0)));

        // Meeting actions (B4a R8) are no knowledge passed on: they follow with the room that is left.
        raw.AddRange(meeting);
        return [.. raw.Take(MaxOptions).Select((o, i) => (string.Create(CultureInfo.InvariantCulture, $"o{i + 1}"), o.Action, o.TargetId))];
    }

    private static bool IsMeeting(KnowledgeSettings knowledge, string action) =>
        knowledge.Meeting?.Options.Any(o => string.Equals(o.Action, action, StringComparison.Ordinal)) == true;

    /// <summary>W1696: an action that rests after repeats without effect (for this target, or here) is not offered.</summary>
    private bool Rests(Inhabitant asker, string action, string? targetId, long tick) =>
        asker.Resting.Count > 0 && asker.Rests(action, targetId, asker.Position, tick, _brain.Weights.Repetition?.SamePlaceM ?? 0);

    private DecisionRequest BuildRequest(World world, Inhabitant inhabitant, string requestId, List<(string OptionId, string Action, string? TargetId)> options)
    {
        var seed = world.Random.Get("decisions/seed").NextInt(0, int.MaxValue);
        return new DecisionRequest
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = seed,
            DeadlineMs = _settings.DeadlineMs,
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Inhabitant = InhabitantStates.Of(inhabitant, Knowledge, Perceived(inhabitant).Select(o => o.TypeId), PerceivedInhabitants(inhabitant).Select(i => i.Id)),
            Environment = new EnvironmentState
            {
                TimeOfDay = world.Now.Phase.ToString().ToLowerInvariant(),
                TemperatureC = WeatherSource is { } w ? Math.Round(w.Read(world).TemperatureTenthC / 10.0, 1) : _settings.FallbackEnvironment.TemperatureC,
                Weather = WeatherSource is { } source ? [.. source.Conditions] : [.. _settings.FallbackEnvironment.Weather],

                // B2594: how far it sees right now; what lies beyond is not in the perception lists.
                VisibilityM = Math.Round(_inhabitants.SightRadiusM, 1),
            },
            Perception = new Perception
            {
                Objects = [.. Perceived(inhabitant).Take(64).Select(o => new PerceivedObject
                {
                    Id = o.Id,
                    Type = o.TypeId,
                    DistanceM = Distance(inhabitant.Position, o.Position),
                    KnownProperties = [.. inhabitant.Beliefs.About(o.TypeId).Select(b => new KnownProperty { Property = b.PropertyId, Value = b.Belief.Value, Confidence = b.Belief.Confidence })],
                })],

                // Contract 0.2 (B4a-S5): other inhabitants in sight, nearest first.
                // W1698: how the asker calls the other - its own name once the asker knows it.
                Inhabitants = [.. PerceivedInhabitants(inhabitant).Take(Knowledge?.RequestMaxInhabitants ?? 0)
                    .Select(i => new PerceivedInhabitant
                    {
                        Id = i.Id,
                        DistanceM = Distance(inhabitant.Position, i.Position),
                        Called = i.Name is { } name && inhabitant.KnownNames.Contains(i.Id) ? new CalledAs { Name = name, Label = null } : null,
                    })],
            },
            Options = [.. options.Select(o => new Option { OptionId = o.OptionId, Action = o.Action, Target = o.TargetId, Topic = TopicOf(inhabitant, o.Action, o.TargetId) })],
        };
    }

    /// <summary>
    /// What a passing option (show, teach, warn) is about (D625): the asker's own belief the core would pass
    /// to the target (the same one <see cref="InhabitantsSystem.TopicOf"/> shows), so the decider can name the
    /// thing. Null for every other option. Only in the request, never stored, so hashes stay as they were.
    /// </summary>
    private OptionTopic? TopicOf(Inhabitant asker, string action, string? targetId)
    {
        if (Knowledge is not { } knowledge
            || targetId is null
            || _actionTargets.GetValueOrDefault(action, VocabularyEntryTarget.None) != VocabularyEntryTarget.Inhabitant
            || _inhabitants.All.FirstOrDefault(i => i.Id == targetId) is not { } listener
            || InhabitantsSystem.PassableBelief(knowledge, asker, listener, action) is not { } belief)
        {
            return null;
        }

        return new OptionTopic
        {
            ObjectType = belief.TypeId,
            Name = InventedName?.Invoke(belief.TypeId),
            Property = belief.PropertyId,
            Value = belief.Belief.Value,
        };
    }

    private void Apply(World world, PortMessage message)
    {
        var question = _open.Values.FirstOrDefault(q => q.RequestId == message.RequestId && q.RetryAtTick is null);
        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Error, message.RequestId, world.Tick, message.Json, ReplayNotes.ServiceError));
            if (question is not null && IsRetryable(message.Json))
            {
                // B2487: no fixed time limit; the question stays open and is asked again later.
                _open[question.Id] = question with { RetryAtTick = world.Tick + ((long)_settings.RetryAfterMinutes * world.Calendar.Settings.TicksPerHour / 60) };
                return;
            }

            Close(world, question, "question_failed");
            return;
        }

        DecisionResponse response;
        try
        {
            response = ContractSerializer.Deserialize<DecisionResponse>(message.Json);
        }
        catch (ContractViolationException)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Error, message.RequestId, world.Tick, message.Json, ReplayNotes.ContractViolation));
            Close(world, question, "question_failed");
            return;
        }

        if (question is null || response.RequestId != question.RequestId)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Discarded, message.RequestId, world.Tick, message.Json, ReplayNotes.QuestionClosed));
            return;
        }

        var chosen = question.Options.Where(o => o.OptionId == response.OptionId).ToList();
        var resolved = chosen.Count == 1 && chosen[0].TargetId is { } id ? _inhabitants.ResolveTarget(id) : null;
        if (chosen.Count != 1 || (chosen[0].TargetId is not null && resolved is null))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Discarded, message.RequestId, world.Tick, message.Json, ReplayNotes.UnknownOption));
            Close(world, question, "question_failed", ReplayNotes.UnknownOption);
            return;
        }

        var inhabitant = _inhabitants.Get(question.InhabitantId);
        var (_, action, targetId) = chosen[0];
        if (resolved?.Inhabitant is { } listener && (Knowledge is not { } knowledge || !InhabitantsSystem.HasPurpose(knowledge, inhabitant, listener, action)))
        {
            // Between offer and answer the listener may have learned it themselves (or the asker the name):
            // no walk over for nothing.
            Log.Add(new DecisionLogEntry(DecisionLogKind.Discarded, message.RequestId, world.Tick, message.Json, ReplayNotes.NothingToPass));
            Close(world, question, "question_failed", ReplayNotes.NothingToPass);
            return;
        }

        // An action at the frontier (B2-6 R14) goes where unseen land begins now. If none is left since the
        // offer, the answer is dropped like one whose target is gone: nobody is paid for standing around.
        var atFrontier = _inhabitants.AtFrontier(action);
        var frontier = atFrontier ? _inhabitants.FrontierOf?.Invoke(inhabitant) : null;
        if (atFrontier && frontier is null)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Discarded, message.RequestId, world.Tick, message.Json, ReplayNotes.NothingToExplore));
            Close(world, question, "question_failed", ReplayNotes.NothingToExplore);
            return;
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.Response, message.RequestId, world.Tick, message.Json, null));
        var destination = resolved?.Position ?? frontier ?? inhabitant.Position;
        var decided = new Activity(action, targetId, destination, null);
        if (inhabitant.Activity?.RemainingTicks is not null)
        {
            // An action is in progress (e.g. sleeping): the answer waits, it never breaks it off.
            inhabitant.Planned = decided;
        }
        else
        {
            inhabitant.Activity = decided;
        }

        Close(world, question, "question_answered");
        if (response.Thought is { } thought)
        {
            // Thought bubble (B3): the only player-visible text of a decision, in the display language.
            _inhabitants.Report(new WorldEvent("thought", world.Tick, inhabitant.Id, action, targetId) { Text = thought.Text });
        }
    }

    private static bool IsRetryable(string json)
    {
        try
        {
            return ContractSerializer.Deserialize<ServiceError>(json).Retryable;
        }
        catch (ContractViolationException)
        {
            return false;
        }
    }

    private void Close(World world, OpenQuestion? question, string eventKind, string? reason = null)
    {
        if (question is null)
        {
            return;
        }

        _open.Remove(question.Id);
        _inhabitants.Report(new WorldEvent(eventKind, world.Tick, question.InhabitantId, null, question.Id) { Detail = reason });
    }

    /// <summary>Other inhabitants within the perception radius as it is right now (B2594), nearest first, ties by id.</summary>
    private List<Inhabitant> PerceivedInhabitants(Inhabitant inhabitant)
    {
        var radius = _inhabitants.SightRadiusM;
        return [.. _inhabitants.All
            .Where(i => i.Id != inhabitant.Id)
            .Select(i => (i, d: Distance(inhabitant.Position, i.Position)))
            .Where(x => x.d <= radius)
            .OrderBy(x => x.d)
            .ThenBy(x => x.i.Id, StringComparer.Ordinal)
            .Select(x => x.i)];
    }

    private static double Distance(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
