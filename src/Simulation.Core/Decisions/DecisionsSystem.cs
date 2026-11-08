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
    /// <summary>Object type of a gift (vocabulary id).</summary>
    public string? TypeId { get; init; }

    /// <summary>World position X of a gift (metres).</summary>
    public double? X { get; init; }

    /// <summary>World position Z of a gift (metres).</summary>
    public double? Z { get; init; }

    /// <summary>A whole number for commands that need one (e.g. the start favour).</summary>
    public int? Amount { get; init; }

    /// <summary>Mailbox message a command is about (acknowledge).</summary>
    public string? MessageId { get; init; }
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

    /// <summary>Whether the inhabitant has an open question about a type (W1595; the routine waits for the answer).</summary>
    public bool HasOpenQuestion(string inhabitantId, string typeId) =>
        _open.Values.Any(q => q.InhabitantId == inhabitantId && q.Subject == "type:" + typeId);

    /// <summary>Trigger: an object type without any belief.</summary>
    public const string UnknownObject = "unknown_object";

    /// <summary>Trigger: the inhabitant noticed the player (e.g. was clicked).</summary>
    public const string NoticedPlayer = "noticed_player";

    /// <summary>Player command: an inhabitant was clicked.</summary>
    public const string InhabitantClicked = "inhabitant_clicked";

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
    private readonly string _displayLocale;
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

    /// <summary>The replay log.</summary>
    public DecisionLog Log { get; } = new();

    /// <summary>Open questions in id order.</summary>
    public IEnumerable<OpenQuestion> Open => _open.Values;

    /// <summary>Open questions of one inhabitant (ids only for the UI window, no wording).</summary>
    public IReadOnlyList<string> OpenQuestionIds(string inhabitantId) =>
        [.. _open.Values.Where(q => q.InhabitantId == inhabitantId).Select(q => q.Id)];

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

    private bool Enabled(string kind) =>
        _settings.Triggers.Any(t => t.Enabled && string.Equals(t.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The nearest perceived object whose type the inhabitant has no belief about (ties by id). A single
    /// pass without sorting: this runs every tick for every inhabitant.
    /// </summary>
    private WorldObject? NearestUnknown(Inhabitant inhabitant)
    {
        var radiusSquared = _brain.Weights.PerceptionRadiusM * _brain.Weights.PerceptionRadiusM;
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
        var radius = _brain.Weights.PerceptionRadiusM;
        return [.. _objects.All
            .Select(o => (o, d: Distance(inhabitant.Position, o.Position)))
            .Where(x => x.d <= radius)
            .OrderBy(x => x.d)
            .ThenBy(x => x.o.Id, StringComparer.Ordinal)
            .Select(x => x.o)];
    }

    private void Ask(World world, Inhabitant inhabitant, string kind, string subject, WorldObject? target)
    {
        var cooldownKey = inhabitant.Id + "|" + subject;
        var cooldown = (long)_settings.AskCooldownHours * world.Calendar.Settings.TicksPerHour;
        if (_open.Values.Count(q => q.InhabitantId == inhabitant.Id) >= _settings.MaxOpenQuestions
            || _open.Values.Any(q => q.InhabitantId == inhabitant.Id && q.Subject == subject)
            || (_lastAsked.TryGetValue(cooldownKey, out var last) && world.Tick - last < cooldown))
        {
            return;
        }

        _counter++;
        var questionId = string.Create(CultureInfo.InvariantCulture, $"q-{_counter:000000}");
        var question = Send(world, inhabitant, questionId, kind, subject, target);
        _open.Add(question.Id, question);
        _lastAsked[cooldownKey] = world.Tick;
        _inhabitants.Report(new WorldEvent("question_opened", world.Tick, inhabitant.Id, null, question.Id));
    }

    private OpenQuestion Send(World world, Inhabitant inhabitant, string questionId, string kind, string subject, WorldObject? target)
    {
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id, _counter);
        var options = BuildOptions(inhabitant, target);
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

    /// <summary>Options the core allows: object actions on the subject and every action without a target (target kind from the action vocabulary).</summary>
    private List<(string OptionId, string Action, string? TargetId)> BuildOptions(Inhabitant asker, WorldObject? target)
    {
        var raw = new List<(string Action, string? TargetId)>();
        foreach (var action in _actions.OfferedActions)
        {
            switch (_actionTargets.GetValueOrDefault(action, VocabularyEntryTarget.None))
            {
                case VocabularyEntryTarget.Object when target is not null:
                    raw.Add((action, target.Id));
                    break;
                case VocabularyEntryTarget.None:
                    raw.Add((action, null));
                    break;
                case VocabularyEntryTarget.Inhabitant when Knowledge is { } knowledge:
                    // Passing on knowledge (B4a R4): offered towards someone in sight when the asker knows
                    // something this action carries and the other has not experienced it.
                    foreach (var other in PerceivedInhabitants(asker).Take(knowledge.RequestMaxInhabitants))
                    {
                        if (InhabitantsSystem.PassableBelief(knowledge, asker, other, action) is not null)
                        {
                            raw.Add((action, other.Id));
                        }
                    }

                    break;
                default:
                    // Other inhabitant actions follow with B3/B9; object actions need a subject.
                    break;
            }
        }

        return [.. raw.Select((o, i) => (string.Create(CultureInfo.InvariantCulture, $"o{i + 1}"), o.Action, o.TargetId))];
    }

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
                Inhabitants = [.. PerceivedInhabitants(inhabitant).Take(Knowledge?.RequestMaxInhabitants ?? 0)
                    .Select(i => new PerceivedInhabitant { Id = i.Id, DistanceM = Distance(inhabitant.Position, i.Position) })],
            },
            Options = [.. options.Select(o => new Option { OptionId = o.OptionId, Action = o.Action, Target = o.TargetId })],
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
        if (chosen.Count != 1 || (chosen[0].TargetId is not null && !_objects.TryGet(chosen[0].TargetId!, out _)))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Discarded, message.RequestId, world.Tick, message.Json, ReplayNotes.UnknownOption));
            Close(world, question, "question_failed");
            return;
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.Response, message.RequestId, world.Tick, message.Json, null));
        var inhabitant = _inhabitants.Get(question.InhabitantId);
        var (_, action, targetId) = chosen[0];
        var destination = targetId is not null && _objects.TryGet(targetId, out var target) ? target.Position : inhabitant.Position;
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

    private void Close(World world, OpenQuestion? question, string eventKind)
    {
        if (question is null)
        {
            return;
        }

        _open.Remove(question.Id);
        _inhabitants.Report(new WorldEvent(eventKind, world.Tick, question.InhabitantId, null, question.Id));
    }

    /// <summary>Other inhabitants within the perception radius, nearest first, ties by id.</summary>
    private List<Inhabitant> PerceivedInhabitants(Inhabitant inhabitant)
    {
        var radius = _brain.Weights.PerceptionRadiusM;
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
