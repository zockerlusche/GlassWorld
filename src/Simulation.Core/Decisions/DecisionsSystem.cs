using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Decisions;

/// <summary>An open question of an inhabitant (B2487: shown as a question mark until answered).</summary>
/// <param name="Id">Question id, e.g. <c>q-000001</c> (no wording; the UI shows only that there is one).</param>
/// <param name="InhabitantId">Who asks.</param>
/// <param name="Kind">What was not understood (trigger kind).</param>
/// <param name="Subject">Object id or subject key the question is about.</param>
/// <param name="RequestId">Request sent to the decider; empty for a question that was never sent: it waits for the compute night (trigger data: ask night), or it is held back while its inhabitant is busy with the decider (A1487) and is asked later.</param>
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

    /// <summary>
    /// Whether a request is validated against its schema before it leaves (ADR-0009; set by the host from
    /// <see cref="SimulationHostOptions.ValidateRequests"/>): on in tests and check runs, off in the running game.
    /// </summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>Knowledge data (B4a): passing actions as options, knowledge and relationships in the request.</summary>
    public KnowledgeSettings? Knowledge { get; init; }

    /// <summary>
    /// Whether the compute night answers questions in this world (a job port and the night questions data;
    /// set by the host). Only then a trigger with <c>ask: night</c> waits for the night; otherwise its
    /// question goes to the decider at once, so nobody waits for an answer that cannot come.
    /// </summary>
    public bool NightAnswers { get; init; }

    /// <summary>
    /// The player's switch 'learn by day as well' (B2652): while on, no trigger waits for the night. Set only by
    /// the logged command <see cref="SetLearnByDay"/> (and on restore from the log).
    /// </summary>
    public bool LearnByDay { get; internal set; }

    /// <summary>
    /// Whether the compute night can answer questions of a trigger at all: it guesses about object types, so
    /// only a trigger whose questions are about a type qualifies (checked when the world is assembled).
    /// </summary>
    public static bool NightCanAnswer(string kind) => string.Equals(kind, UnknownObject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The inhabitants' own name of an invented type (null for content types, which the data names); set by
    /// the host once the invention system exists. Used for the topic of passing options (D625).
    /// </summary>
    public Func<string, LocalizedName?>? InventedName { get; set; }

    /// <summary>Whether the inhabitant has an open question about a type, out with the decider or waiting.</summary>
    public bool HasOpenQuestion(string inhabitantId, string typeId) =>
        _openOf.TryGetValue(inhabitantId, out var questions) && HasSubject(questions, TypeSubject, typeId);

    /// <summary>
    /// Whether a question of the inhabitant about a type is out with the decider right now (W1595: the routine
    /// waits for that answer, A1551). A question that only waits - held back, for the night, or for its next
    /// try after an error - is not out: nobody is working on an answer, so the routine is not kept waiting.
    /// </summary>
    public bool IsAskingAbout(string inhabitantId, string typeId)
    {
        if (!_openOf.TryGetValue(inhabitantId, out var questions))
        {
            return false;
        }

        foreach (var question in questions)
        {
            if (question.RequestId.Length > 0 && question.RetryAtTick is null && IsSubject(question.Subject, TypeSubject, typeId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Trigger: an object type without any belief.</summary>
    public const string UnknownObject = "unknown_object";

    /// <summary>Trigger: another inhabitant in sight whose own name the inhabitant does not know (B4a R8).</summary>
    public const string UnknownInhabitant = "unknown_inhabitant";

    /// <summary>Trigger: the inhabitant noticed the player (e.g. was clicked).</summary>
    public const string NoticedPlayer = "noticed_player";

    /// <summary>Question kind: a building is stuck and the builder asks what to do (A1373 step 2, D850); subject = invention type.</summary>
    public const string BuildStuck = "build_stuck";

    /// <summary>A1373 step 2 (set by the host): the options of a stuck building (inhabitant, invention type), fresh each time a request is built.</summary>
    public Func<string, string, IReadOnlyList<(string Action, string? TargetId)>>? StuckOptions { get; set; }

    /// <summary>A1373 step 2 (set by the host): what a stuck building's request tells the decider (inhabitant, invention type).</summary>
    public Func<string, string, StuckBuilding?>? StuckContext { get; set; }

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

    /// <summary>
    /// Player command (B2652, A1568, the switch 'learn by day as well'): Text <c>on</c> or <c>off</c>. While on,
    /// questions of triggers that ask at night (<c>ask: night</c>) go to the decider at once, like any other; a
    /// question already waiting for the night goes out the next tick. Logged like every command, so a replay
    /// switches at the same tick; after a restore the last logged switch counts (as for the language).
    /// </summary>
    public const string SetLearnByDay = "set_learn_by_day";

    /// <summary>Text of <see cref="SetLearnByDay"/> that switches learning by day on.</summary>
    public const string LearnByDayOn = "on";

    /// <summary>Text of <see cref="SetLearnByDay"/> that switches it off (the default: new things are learned at night).</summary>
    public const string LearnByDayOff = "off";

    /// <summary>
    /// Detail of <c>question_failed</c> for a question that was held back (A1487) and never asked: when its
    /// inhabitant was free again, what had raised it no longer held (the thing or the other out of sight,
    /// understood meanwhile, the other asleep).
    /// </summary>
    public const string Lapsed = "lapsed";

    /// <summary>Text key: unknown command.</summary>
    public const string ReasonUnknownCommand = "msg.command.unknown";

    /// <summary>Text key: the command names no existing inhabitant.</summary>
    public const string ReasonNoInhabitant = "msg.command.no_inhabitant";

    private readonly SortedDictionary<string, OpenQuestion> _open = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _lastAsked = new(StringComparer.Ordinal);

    // Indexes, not state (they follow from _open and _lastAsked; ForgetDerived builds them anew): the open
    // questions of each inhabitant, and for the unknown_inhabitant trigger when and how often one inhabitant
    // asked who another is. The triggers ask "may it ask?" for every inhabitant and every stranger in sight in
    // every tick; without the indexes each of those walks all open questions of the world and builds key
    // strings. They hold because each table has one way in:
    // - _open is written only by AddOpen, SetOpen and RemoveOpen, which keep _openOf in step;
    // - _lastAsked is written only by NoteAsked (and filled by a load), which drops the pair from _askedWho
    //   (a load clears it), so what is kept there is always what the table says.
    // _askedWho is keyed by the ids of the pair and never shrinks: inhabitants are never taken out of the
    // world, so it holds at most n x (n - 1) small entries (9900 with a hundred inhabitants).
    private readonly Dictionary<string, List<OpenQuestion>> _openOf = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Asker, string Other), (long? LastAsked, long Asks)> _askedWho = [];
    private readonly Queue<PlayerCommand> _commands = new();
    private readonly Dictionary<string, (Func<PlayerCommand, CommandResult> Check, Action<World, PlayerCommand> Apply)> _handlers = new(StringComparer.Ordinal);
    private readonly DeciderSettings _settings;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly IDecisionPort _port;
    private readonly TextGuard _guard;
    private string _displayLocale;
    private readonly IReadOnlyDictionary<string, VocabularyEntryTarget> _actionTargets;
    private ILookup<long, PlayerCommand>? _replayCommands;
    private long _counter;

    // max_requests_per_step: how many requests went out in the step of _sentTick. Derived from the step
    // alone - it starts at 0 in every tick, so it is neither saved nor hashed.
    private long _sentTick = -1;
    private int _sentInStep;

    /// <summary>Creates the system.</summary>
    public DecisionsSystem(DeciderSettings settings, InhabitantsSystem inhabitants, WorldObjects objects, ActionRules actions, UtilityBrain brain, Vocabulary actionVocabulary, IDecisionPort port, TextGuard guard, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(actionVocabulary);
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
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
            AddOpen(question);
        }

        foreach (var (key, tick) in lastAsked)
        {
            _lastAsked.Add(key, tick);
        }

        _askedWho.Clear();

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
        // A question that waits for the night was never sent: nothing to send again.
        foreach (var question in _open.Values.Where(q => q.RetryAtTick is null && q.RequestId.Length > 0).ToList())
        {
            SetOpen(question with { RetryAtTick = world.Tick });
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

    /// <summary>Step part (<see cref="IStepProbe"/>): taking in the decider's answers and the player's commands.</summary>
    public const string PartAnswers = "decisions/answers";

    /// <summary>Step part: questions that were held back and go out now, or lapse (A1487).</summary>
    public const string PartHeld = "decisions/held";

    /// <summary>Step part: looking for things an inhabitant has no belief about (trigger unknown_object).</summary>
    public const string PartUnknownObject = "decisions/unknown_object";

    /// <summary>Step part: looking for others whose name an inhabitant does not know (trigger unknown_inhabitant).</summary>
    public const string PartUnknownInhabitant = "decisions/unknown_inhabitant";

    /// <summary>Step part: sending a request - its id, the log entry, the port (inside the parts that ask).</summary>
    public const string PartRequest = "decisions/request";

    /// <summary>Step part of a request: who and what the asker perceives.</summary>
    public const string PartRequestLook = "decisions/request_look";

    /// <summary>Step part of a request: the options the core allows.</summary>
    public const string PartRequestOptions = "decisions/request_options";

    /// <summary>Step part of a request: the asker's state, its perception and the options as the contract carries them.</summary>
    public const string PartRequestBuild = "decisions/request_build";

    /// <summary>Step part of a request: writing it as JSON (and validating it, where that is on).</summary>
    public const string PartRequestJson = "decisions/request_json";

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var probe = world.Probe;
        probe?.Enter(PartAnswers);
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
            // Over the step's limit the question stays due and goes out in a later step.
            if (!MaySend(world))
            {
                break;
            }

            Resend(world, due);
        }

        probe?.Leave(PartAnswers);
        probe?.Enter(PartHeld);
        if (_open.Count > 0)
        {
            // A1487: questions that were held back go out once their inhabitant is free again, oldest first
            // (each one sent keeps the next back in turn).
            foreach (var held in _open.Values.Where(q => q.RequestId.Length == 0 && !WaitsForNight(q.Kind)).ToList())
            {
                // Over the step's limit the rest waits on as it is, in the same order, for the next step.
                if (!MaySend(world))
                {
                    break;
                }

                AskHeld(world, held);
            }
        }

        probe?.Leave(PartHeld);
        probe?.Enter(PartUnknownObject);
        if (Enabled(UnknownObject))
        {
            foreach (var inhabitant in _inhabitants.All)
            {
                // No room for another question: asking would be refused whatever the thing, so nothing is looked for.
                if (!HasRoom(inhabitant, UnknownObject))
                {
                    continue;
                }

                var unknown = NearestUnknown(world, inhabitant);
                if (unknown is not null)
                {
                    Ask(world, inhabitant, UnknownObject, "type:" + unknown.TypeId, unknown);
                }
            }
        }

        probe?.Leave(PartUnknownObject);
        probe?.Enter(PartUnknownInhabitant);
        if (Enabled(UnknownInhabitant))
        {
            // B4a R8: the other is at first an unknown human; who it is becomes a question (W1595), whose
            // options (approach, talk to, introduce ...) are the core's, the choice the decider's.
            // The nearest stranger (ties by id) it may ask about now: one cooling down or asked about
            // unknown_inhabitant_max_asks times already does not hide a second one (review GW3 #207).
            // Nobody asks who a sleeper is (W1744): every answer would be an action on a sleeper, which the
            // rule checker cancels; the question comes when the other is awake and in sight again.
            var radius = _inhabitants.SightRadiusM;
            var cooldown = (long)_settings.AskCooldownHours * world.Calendar.Settings.TicksPerHour;
            var maxAsks = _settings.UnknownInhabitantMaxAsks ?? int.MaxValue;
            var everyone = _inhabitants.Ordered;
            foreach (var inhabitant in everyone)
            {
                if (!HasRoom(inhabitant, UnknownInhabitant))
                {
                    continue;
                }

                // One pass in id order instead of sorting everyone in sight: the nearest who passes wins,
                // and of two at the same distance the one met first here, which has the lower id. What is
                // asked of each is AskableStranger and MayAsk, piece by piece: a name not known, awake, not
                // asked about too often; room (above), no question open about them, not cooling down.
                var subjects = _openOf.GetValueOrDefault(inhabitant.Id);
                Inhabitant? stranger = null;
                var nearest = double.MaxValue;
                for (var i = 0; i < everyone.Count; i++)
                {
                    var other = everyone[i];
                    if (ReferenceEquals(other, inhabitant))
                    {
                        continue;
                    }

                    var distance = Distance(inhabitant.Position, other.Position);
                    if (distance > radius || distance >= nearest
                        || other.Name is null || inhabitant.KnownNames.Contains(other.Id) || _inhabitants.IsAsleep(other)
                        || (subjects is not null && HasSubject(subjects, InhabitantSubject, other.Id)))
                    {
                        continue;
                    }

                    var asked = AskedWho(inhabitant, other);
                    if ((asked.LastAsked is { } last && world.Tick - last < cooldown) || asked.Asks >= maxAsks)
                    {
                        continue;
                    }

                    stranger = other;
                    nearest = distance;
                }

                if (stranger is not null)
                {
                    Ask(world, inhabitant, UnknownInhabitant, InhabitantSubject + stranger.Id, null);
                }
            }
        }

        probe?.Leave(PartUnknownInhabitant);
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
    /// A1487: whether a question of this trigger waits for the compute night (data: ask night) instead of
    /// going to the decider now. Omitted or immediate = now, as before.
    /// </summary>
    private bool WaitsForNight(string kind) =>
        NightAnswers && !LearnByDay && _settings.Triggers.Any(t => t.Enabled && t.Ask == TriggerAsk.Night && string.Equals(t.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A1487: whether a question of this trigger goes out even while its inhabitant is busy (data: interrupts).
    /// Always false while questions are never held back (no choice_protection_hours).
    /// </summary>
    private bool Interrupts(string kind) =>
        _settings.ChoiceProtectionHours > 0 && _settings.Triggers.Any(t => t.Enabled && t.Interrupts == true && string.Equals(t.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A1487, one thing at a time: whether a question of this trigger has to wait now, because its inhabitant
    /// is busy with the decider - a question of it is out with the decider, or a choice the decider made for
    /// it is queued (<see cref="Inhabitant.Planned"/>) or under way (its <see cref="Inhabitant.Activity"/>,
    /// walking there or doing it; the mark is <see cref="Activity.DecidedTick"/>). Read from the state alone.
    /// The protection ends by itself when the choice has finished, was cancelled or dropped (the activity is
    /// gone). Nothing holds questions back forever: an action that runs ends by its duration, and so does
    /// the wait of a choice queued behind it; what has no end of its own - a question that is out, a walk -
    /// protects only for the data's hours (choice_protection_hours). The rule does not hold for a trigger that
    /// interrupts (data), nor for a choice that has gone stale by neglect
    /// (<see cref="InhabitantsSystem.StaleByNeglect"/>: a need below its neglect threshold that the choice
    /// does not serve). An answer still replaces whatever is queued or walked to, as before; the rule only
    /// keeps the questions from going out.
    /// </summary>
    private bool HeldBack(World world, Inhabitant inhabitant, string kind)
    {
        var hours = _settings.ChoiceProtectionHours ?? 0;
        if (hours <= 0 || Interrupts(kind))
        {
            return false;
        }

        var limit = (long)Math.Round(hours * world.Calendar.Settings.TicksPerHour);
        var walking = inhabitant.Activity is { RemainingTicks: null };

        // Under way: doing it (ends by itself), or walking there within the limit. Queued: its turn comes when
        // the running action ends; behind a walk it waits within the limit only.
        return Protects(inhabitant.Activity, bounded: !walking) || Protects(inhabitant.Planned, bounded: !walking)
            || OutWithTheDecider(inhabitant, world.Tick, limit);

        bool Protects(Activity? choice, bool bounded) =>
            choice?.DecidedTick is { } since && (bounded || world.Tick - since < limit) && !_inhabitants.StaleByNeglect(inhabitant, choice.Action);
    }

    /// <summary>Whether a question of the inhabitant is out with the decider and was asked less than <paramref name="limit"/> ticks ago.</summary>
    private bool OutWithTheDecider(Inhabitant inhabitant, long tick, long limit)
    {
        if (_openOf.TryGetValue(inhabitant.Id, out var questions))
        {
            foreach (var question in questions)
            {
                if (question.RequestId.Length > 0 && tick - question.AskedTick < limit)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A1487: a question that was held back, looked at once its inhabitant is free: it goes to the decider if
    /// what raised it still holds (the trigger's own condition for its subject: a thing of the type in sight
    /// and still not understood; the other in sight, awake, its name unknown, not asked about too often).
    /// Otherwise it lapses. Nothing is lost by that: the trigger raises the question anew the moment the
    /// thing is in sight again (no cooldown has begun), while a request without its subject among the
    /// options would be an AI call for nothing. A question about nothing in the world (the player) is asked.
    /// </summary>
    private void AskHeld(World world, OpenQuestion held)
    {
        var inhabitant = _inhabitants.Get(held.InhabitantId);
        if (HeldBack(world, inhabitant, held.Kind))
        {
            return;
        }

        WorldObject? target = null;
        var holds = true;
        if (held.Kind == UnknownObject)
        {
            var typeId = held.Subject[TypeSubject.Length..];
            target = inhabitant.Beliefs.About(typeId).Any() ? null : Perceived(inhabitant).FirstOrDefault(o => o.TypeId == typeId);
            holds = target is not null;
        }
        else if (held.Kind == UnknownInhabitant)
        {
            holds = PerceivedInhabitants(inhabitant).Any(o => InhabitantSubject + o.Id == held.Subject && AskableStranger(inhabitant, o));
        }

        if (!holds)
        {
            Close(world, held, "question_failed", Lapsed);
            return;
        }

        _counter++;
        SetOpen(Send(world, inhabitant, held.Id, held.Kind, held.Subject, target));
        NoteAsked(world, inhabitant, held.Kind, held.Subject);
    }

    /// <summary>
    /// Whether the inhabitant may ask who the other is (trigger unknown_inhabitant): the other has a name it
    /// does not know, is awake, and it has not asked about them the data's number of times yet.
    /// </summary>
    private bool AskableStranger(Inhabitant inhabitant, Inhabitant other) =>
        other.Name is not null && !inhabitant.KnownNames.Contains(other.Id) && !_inhabitants.IsAsleep(other)
        && AsksAbout(inhabitant, other) < (_settings.UnknownInhabitantMaxAsks ?? int.MaxValue);

    /// <summary>
    /// What begins when a question is really asked (not while it is only held back): the cooldown of its
    /// subject and, for who-is-that, its count for the pair. The only place that writes
    /// <see cref="_lastAsked"/>; for a subject that is an inhabitant it drops what <see cref="AskedWho"/>
    /// keeps about the pair, so that is read again from the table.
    /// </summary>
    private void NoteAsked(World world, Inhabitant inhabitant, string kind, string subject)
    {
        _lastAsked[inhabitant.Id + "|" + subject] = world.Tick;
        if (kind == UnknownInhabitant)
        {
            var asks = AsksKey(inhabitant, subject);
            _lastAsked[asks] = _lastAsked.GetValueOrDefault(asks) + 1;
        }

        if (subject.StartsWith(InhabitantSubject, StringComparison.Ordinal))
        {
            _askedWho.Remove((inhabitant.Id, subject[InhabitantSubject.Length..]));
        }
    }

    /// <summary>
    /// The nearest perceived object whose type the inhabitant has no belief about and is not cooling down on
    /// (ties by id): a thing it asked about lately without learning anything (the answer did not teach the type,
    /// the question lapsed) does not hide a second unknown thing a little farther away (A1551). A thing it is
    /// asking about right now (its cooldown runs from the asking) keeps its place - nothing more is asked about
    /// things meanwhile, so questions about things do not take the room that questions about strangers need.
    /// This runs
    /// every tick for every inhabitant that has room for a question, so it walks only the objects of the
    /// types the inhabitant knows nothing about (none at all once it knows something about every kind of
    /// thing there is), without sorting.
    /// </summary>
    private WorldObject? NearestUnknown(World world, Inhabitant inhabitant)
    {
        var radiusSquared = _inhabitants.SightRadiusM * _inhabitants.SightRadiusM;
        WorldObject? best = null;
        var bestDistance = double.MaxValue;
        var position = inhabitant.Position;
        var byType = _objects.ByType;
        for (var t = 0; t < byType.Count; t++)
        {
            var objects = byType[t].Objects;
            if (objects.Count == 0 || inhabitant.Beliefs.Knows(byType[t].TypeId))
            {
                continue;
            }

            WorldObject? nearest = null;
            var nearestDistance = double.MaxValue;
            for (var i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                var dx = obj.Position.X - position.X;
                var dz = obj.Position.Z - position.Z;
                var d = (dx * dx) + (dz * dz);
                if (d <= radiusSquared && (d < nearestDistance || (d == nearestDistance && string.CompareOrdinal(obj.Id, nearest!.Id) < 0)))
                {
                    nearest = obj;
                    nearestDistance = d;
                }
            }

            if (nearest is not null
                && (nearestDistance < bestDistance || (nearestDistance == bestDistance && string.CompareOrdinal(nearest.Id, best!.Id) < 0))
                && (HasOpenQuestion(inhabitant.Id, nearest.TypeId) || !CoolingDown(world, inhabitant, TypeSubject + nearest.TypeId)))
            {
                best = nearest;
                bestDistance = nearestDistance;
            }
        }

        return best;
    }

    /// <summary>The object types the inhabitant perceives right now, nearest first, each once (W1737).</summary>
    internal IReadOnlyList<string> PerceivedTypes(Inhabitant inhabitant) =>
        [.. Perceived(inhabitant).Select(o => o.TypeId).Distinct(StringComparer.Ordinal)];

    /// <summary>The type of a thing in the world by its id; null when there is no such thing (any more).</summary>
    internal string? TypeOf(string objectId) => _objects.TryGet(objectId, out var thing) ? thing.TypeId : null;

    // Scratch of a look around (one at a time, on the simulation thread): who or what lies in sight, with
    // its distance, before it is put in order.
    private readonly List<(double Distance, WorldObject Thing)> _thingsInSight = [];
    private readonly List<(double Distance, Inhabitant Other)> _othersInSight = [];

    private static readonly Comparison<(double Distance, WorldObject Thing)> NearestThingFirst = (a, b) =>
    {
        var byDistance = a.Distance.CompareTo(b.Distance);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Thing.Id, b.Thing.Id);
    };

    private static readonly Comparison<(double Distance, Inhabitant Other)> NearestOtherFirst = (a, b) =>
    {
        var byDistance = a.Distance.CompareTo(b.Distance);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Other.Id, b.Other.Id);
    };

    /// <summary>
    /// The objects within the perception radius as it is right now (B2594), nearest first, ties by id. Only
    /// what lies in sight is put in order, not every object of the world; ids are unique, so the order is
    /// the same whatever sorts it.
    /// </summary>
    private List<WorldObject> Perceived(Inhabitant inhabitant)
    {
        var radius = _inhabitants.SightRadiusM;
        var position = inhabitant.Position;
        var all = _objects.Ordered;
        var inSight = _thingsInSight;
        inSight.Clear();
        for (var i = 0; i < all.Count; i++)
        {
            var distance = Distance(position, all[i].Position);
            if (distance <= radius)
            {
                inSight.Add((distance, all[i]));
            }
        }

        inSight.Sort(NearestThingFirst);
        var things = new List<WorldObject>(inSight.Count);
        foreach (var (_, thing) in inSight)
        {
            things.Add(thing);
        }

        inSight.Clear();
        return things;
    }

    private const string InhabitantSubject = "inhabitant:";

    /// <summary>
    /// How often the inhabitant has asked who another is (unknown_inhabitant). Kept with the cooldowns under
    /// an own key prefix (value = count, not a tick), so it is saved and hashed with them.
    /// </summary>
    private long AsksAbout(Inhabitant inhabitant, Inhabitant other) => _lastAsked.GetValueOrDefault(AsksKey(inhabitant, InhabitantSubject + other.Id));

    private static string AsksKey(Inhabitant inhabitant, string subject) => "asks|" + inhabitant.Id + "|" + subject;

    /// <summary>
    /// When the inhabitant last asked who the other is (null = never) and how often: the two entries of
    /// <see cref="_lastAsked"/> (cooldown and count), looked up by key once and kept by the pair until
    /// <see cref="NoteAsked"/> writes one of them.
    /// </summary>
    private (long? LastAsked, long Asks) AskedWho(Inhabitant inhabitant, Inhabitant other)
    {
        if (!_askedWho.TryGetValue((inhabitant.Id, other.Id), out var asked))
        {
            asked = (_lastAsked.TryGetValue(inhabitant.Id + "|" + InhabitantSubject + other.Id, out var last) ? last : null, AsksAbout(inhabitant, other));
            _askedWho[(inhabitant.Id, other.Id)] = asked;
        }

        return asked;
    }

    /// <summary>
    /// Throws away everything that is only worked out from the state (the open questions per inhabitant,
    /// who asked about whom) and builds it anew from the state, as a load does. Nothing may depend on
    /// whether this is called: a test runs a world with it before every tick against one without.
    /// </summary>
    internal void ForgetDerived()
    {
        _askedWho.Clear();
        _openOf.Clear();
        foreach (var question in _open.Values)
        {
            Index(question);
        }
    }

    /// <summary>
    /// Whether a question about the subject may be asked now: room for one more, none open about it, not
    /// cooling down. A trigger that interrupts (A1487) is not kept out by questions that only wait, held back
    /// or for the night: the player always goes first.
    /// </summary>
    private bool MayAsk(World world, Inhabitant inhabitant, string subject, string kind)
    {
        var questions = _openOf.GetValueOrDefault(inhabitant.Id);
        return HasRoom(inhabitant, kind)
            && !(questions is not null && HasSubject(questions, string.Empty, subject))
            && !CoolingDown(world, inhabitant, subject);
    }

    /// <summary>Whether the inhabitant asked about the subject less than ask_cooldown_hours ago.</summary>
    private bool CoolingDown(World world, Inhabitant inhabitant, string subject)
    {
        var cooldown = (long)_settings.AskCooldownHours * world.Calendar.Settings.TicksPerHour;
        return _lastAsked.TryGetValue(inhabitant.Id + "|" + subject, out var last) && world.Tick - last < cooldown;
    }

    /// <summary>
    /// Whether the inhabitant has room for another open question of this trigger (the first thing
    /// <see cref="MayAsk"/> asks, whatever the subject): fewer open questions than the data allows, where a
    /// trigger that interrupts (A1487) does not count the ones that only wait (never sent: held back, or for the night).
    /// </summary>
    private bool HasRoom(Inhabitant inhabitant, string kind)
    {
        var questions = _openOf.GetValueOrDefault(inhabitant.Id);
        if (questions is null || questions.Count < _settings.MaxOpenQuestions)
        {
            return true;
        }

        if (!Interrupts(kind))
        {
            return false;
        }

        var sent = 0;
        foreach (var question in questions)
        {
            sent += question.RequestId.Length == 0 ? 0 : 1;
        }

        return sent < _settings.MaxOpenQuestions;
    }

    /// <summary>Whether one of the questions is about <paramref name="prefix"/> followed by <paramref name="id"/> (without building that string).</summary>
    private static bool HasSubject(List<OpenQuestion> questions, string prefix, string id)
    {
        foreach (var question in questions)
        {
            if (IsSubject(question.Subject, prefix, id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a subject is <paramref name="prefix"/> followed by <paramref name="id"/> (without building that string).</summary>
    private static bool IsSubject(string subject, string prefix, string id) =>
        subject.Length == prefix.Length + id.Length && subject.StartsWith(prefix, StringComparison.Ordinal) && subject.EndsWith(id, StringComparison.Ordinal);

    /// <summary>Adds an open question. With <see cref="SetOpen"/> and <see cref="RemoveOpen"/> the only way to write <see cref="_open"/>.</summary>
    private void AddOpen(OpenQuestion question)
    {
        _open.Add(question.Id, question);
        Index(question);
    }

    /// <summary>Puts a changed question (a new request, a retry tick) in the place of the open one with its id.</summary>
    private void SetOpen(OpenQuestion question)
    {
        _open[question.Id] = question;
        var questions = _openOf.GetValueOrDefault(question.InhabitantId);
        var at = questions?.FindIndex(q => q.Id == question.Id) ?? -1;
        if (at >= 0)
        {
            questions![at] = question;
        }
        else
        {
            Index(question);
        }
    }

    /// <summary>Takes an open question away; false if it was not open.</summary>
    private bool RemoveOpen(OpenQuestion question)
    {
        if (!_open.Remove(question.Id))
        {
            return false;
        }

        _openOf.GetValueOrDefault(question.InhabitantId)?.RemoveAll(q => q.Id == question.Id);
        return true;
    }

    private void Index(OpenQuestion question)
    {
        if (!_openOf.TryGetValue(question.InhabitantId, out var questions))
        {
            _openOf[question.InhabitantId] = questions = [];
        }

        questions.Add(question);
    }

    private void Ask(World world, Inhabitant inhabitant, string kind, string subject, WorldObject? target)
    {
        if (!MayAsk(world, inhabitant, subject, kind))
        {
            return;
        }

        _counter++;
        var questionId = string.Create(CultureInfo.InvariantCulture, $"q-{_counter:000000}");

        // A question that waits for the night opens with its question mark but asks nobody by day: no
        // request, no options; the night's question job answers it (NightJobsSystem). A question that is
        // held back (A1487) opens the same way and is asked once its inhabitant is free again (AskHeld).
        // Over the step's limit (max_requests_per_step) a question waits the same way and goes out in a
        // later step as one that was held back.
        var held = !WaitsForNight(kind) && (HeldBack(world, inhabitant, kind) || !MaySend(world));
        var question = held || WaitsForNight(kind)
            ? new OpenQuestion(questionId, inhabitant.Id, kind, subject, string.Empty, world.Tick, [])
            : Send(world, inhabitant, questionId, kind, subject, target);
        AddOpen(question);
        if (!held)
        {
            NoteAsked(world, inhabitant, kind, subject);
        }

        _inhabitants.Report(new WorldEvent("question_opened", world.Tick, inhabitant.Id, null, question.Id));
    }

    // What a passing action of the request that is being built would carry to each of the others it has
    // something for: found when the options are built, read when the request names the topic of an option
    // (what is not kept here is looked for there, as before). The asker's and the others' beliefs do not
    // change in between. Empty outside Send.
    private readonly Dictionary<(string Action, string OtherId), (string TypeId, string PropertyId, Objects.Belief Belief)> _passable = [];

    /// <summary>
    /// Whether one more request may go to the decider in this step (max_requests_per_step; no value = no
    /// limit). The data bound the cost of one step, not what is asked: what is over waits and is asked later.
    /// </summary>
    private bool MaySend(World world)
    {
        if (_settings.MaxRequestsPerStep is not { } most)
        {
            return true;
        }

        return _sentTick != world.Tick || _sentInStep < most;
    }

    private OpenQuestion Send(World world, Inhabitant inhabitant, string questionId, string kind, string subject, WorldObject? target)
    {
        if (_sentTick != world.Tick)
        {
            _sentTick = world.Tick;
            _sentInStep = 0;
        }

        _sentInStep++;
        var probe = world.Probe;
        probe?.Enter(PartRequest);
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id, _counter);

        // One look around per request: the options and the request describe the same moment.
        probe?.Enter(PartRequestLook);
        var others = PerceivedInhabitants(inhabitant);
        var things = Perceived(inhabitant);
        probe?.Leave(PartRequestLook);
        probe?.Enter(PartRequestOptions);
        _passable.Clear();
        var options = kind == BuildStuck && StuckOptions is { } stuck
            ? [.. stuck(inhabitant.Id, subject).Take(MaxOptions).Select((o, i) => (string.Create(CultureInfo.InvariantCulture, $"o{i + 1}"), o.Action, o.TargetId))]
            : BuildOptions(inhabitant, target, world.Tick, others);
        probe?.Leave(PartRequestOptions);
        probe?.Enter(PartRequestBuild);
        var request = BuildRequest(world, inhabitant, requestId, options, others, things);
        if (kind == BuildStuck)
        {
            request.Building = StuckContext?.Invoke(inhabitant.Id, subject);
        }

        _passable.Clear();
        probe?.Leave(PartRequestBuild);
        probe?.Enter(PartRequestJson);
        var json = ContractSerializer.Serialize(request, ValidateRequests);
        probe?.Leave(PartRequestJson);
        Log.Add(new DecisionLogEntry(DecisionLogKind.Request, requestId, world.Tick, json, null));
        _port.Send(requestId, json);
        probe?.Leave(PartRequest);
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
        SetOpen(resent with { AskedTick = question.AskedTick });
    }

    /// <summary>
    /// A1373 step 2 (D850): asks the decider what to do about a stuck building, with the options the core leaves
    /// open (<see cref="StuckOptions"/>) and the building's lines (<see cref="StuckContext"/>). A day decision like
    /// any other: it goes out only within the step's limit (max_requests_per_step); false if it did not go out now
    /// (the building asks again in a later tick). Answered, the choice is an activity like every decision.
    /// </summary>
    public bool AskBuildStuck(World world, string inhabitantId, string typeId)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!MaySend(world) || StuckOptions is null || _openOf.GetValueOrDefault(inhabitantId)?.Any(q => q.Kind == BuildStuck && q.Subject == typeId) == true)
        {
            return false;
        }

        _counter++;
        var questionId = string.Create(CultureInfo.InvariantCulture, $"q-{_counter:000000}");
        var inhabitant = _inhabitants.Get(inhabitantId);
        var question = Send(world, inhabitant, questionId, BuildStuck, typeId, null);
        AddOpen(question);
        _inhabitants.Report(new WorldEvent("question_opened", world.Tick, inhabitant.Id, null, question.Id));
        return true;
    }

    /// <summary>Whether a stuck-building question of an inhabitant about an invention type is open (A1373 step 2).</summary>
    public bool IsAskingAboutBuilding(string inhabitantId, string typeId) =>
        _openOf.GetValueOrDefault(inhabitantId)?.Any(q => q.Kind == BuildStuck && q.Subject == typeId) == true;

    /// <summary>Most options a decision request may hold (contract decision-request: options maxItems).</summary>
    public const int MaxOptions = 32;

    /// <summary>
    /// Options the core allows: object actions on the subject and every action without a target (target
    /// kind from the action vocabulary), each only while the asker's needs suggest it (<see cref="Behaviour.UtilityBrain.NeedSuggests"/>)
    /// and it does not rest after repeats without effect (W1696).
    /// </summary>
    private List<(string OptionId, string Action, string? TargetId)> BuildOptions(Inhabitant asker, WorldObject? target, long tick, List<Inhabitant> others)
    {
        List<Inhabitant>? awake = null;
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
                    // while the conditions of their data hold. Never towards a sleeper (W1744): the rule
                    // checker would cancel the answer, an AI call for nothing.
                    awake ??= [.. others.Where(o => !_inhabitants.IsAsleep(o)).Take(knowledge.RequestMaxInhabitants)];

                    // What kind of action it is, is looked up once for all the others; what a passing action
                    // would carry to each of them is kept for the request, which names it (TopicOf).
                    var meetingOption = InhabitantsSystem.MeetingOptionOf(knowledge, action);
                    foreach (var other in awake)
                    {
                        var purpose = InhabitantsSystem.HasPurpose(knowledge, asker, other, action, meetingOption, out var passable);
                        if (passable is { } carried)
                        {
                            _passable[(action, other.Id)] = carried;
                        }

                        if (purpose && !Rests(asker, action, other.Id, tick))
                        {
                            (meetingOption is not null ? meeting : passOn).Add((action, other.Id));
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

    /// <summary>W1696: an action that rests after repeats without effect (for this target, or here) is not offered.</summary>
    private bool Rests(Inhabitant asker, string action, string? targetId, long tick) =>
        asker.Resting.Count > 0 && asker.Rests(action, targetId, asker.Position, tick, _brain.Weights.Repetition?.SamePlaceM ?? 0);

    private DecisionRequest BuildRequest(World world, Inhabitant inhabitant, string requestId, List<(string OptionId, string Action, string? TargetId)> options, List<Inhabitant> others, List<WorldObject> things)
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
            Inhabitant = InhabitantStates.Of(inhabitant, Knowledge, things.Select(o => o.TypeId), others.Select(i => i.Id), _inhabitants.LabelOf, _inhabitants.TypeLabelOf),
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
                Objects = PerceivedObjects(inhabitant, things),

                // Contract 0.2 (B4a-S5): other inhabitants in sight, nearest first.
                // W1698: how the asker calls the other - its own name once the asker knows it.
                Inhabitants = [.. others.Take(Knowledge?.RequestMaxInhabitants ?? 0)
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

    // What the asker knows about a type and what it calls it, while the things it perceives are listed
    // (most of them are of a few kinds). Empty outside PerceivedObjects.
    private readonly Dictionary<string, (List<KnownProperty> Known, LocalizedName? Label)> _knownOfType = new(StringComparer.Ordinal);

    /// <summary>
    /// The nearest things in sight as the contract carries them (at most 64): each with what the asker
    /// believes about its type and with what the asker itself calls this kind of thing - the name of an
    /// invention it knows something about; absent for what the data name and for a thing it knows nothing of.
    /// Both belong to the type, so they are looked up once per type and shared by the things of that type.
    /// </summary>
    private List<PerceivedObject> PerceivedObjects(Inhabitant inhabitant, List<WorldObject> things)
    {
        var count = Math.Min(64, things.Count);
        var objects = new List<PerceivedObject>(count);
        var ofType = _knownOfType;
        ofType.Clear();
        for (var i = 0; i < count; i++)
        {
            var thing = things[i];
            if (!ofType.TryGetValue(thing.TypeId, out var type))
            {
                var known = new List<KnownProperty>();
                foreach (var (property, belief) in inhabitant.Beliefs.About(thing.TypeId))
                {
                    known.Add(new KnownProperty { Property = property, Value = belief.Value, Confidence = belief.Confidence });
                }

                type = (known, _inhabitants.TypeLabelOf(inhabitant, thing.TypeId));
                ofType.Add(thing.TypeId, type);
            }

            // Every perceived thing of one type shares the SAME list and label instance (the request is only
            // written out as JSON). Whoever edits a built request in place (trimming, a test helper) must copy
            // the list first, or the edit lands on every thing of that type.
            objects.Add(new PerceivedObject
            {
                Id = thing.Id,
                Type = thing.TypeId,
                DistanceM = Distance(inhabitant.Position, thing.Position),
                KnownProperties = type.Known,
                Label = type.Label,
            });
        }

        ofType.Clear();
        return objects;
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
            || _inhabitants.ResolveTarget(targetId)?.Inhabitant is not { } listener
            || (_passable.TryGetValue((action, listener.Id), out var kept) ? kept : InhabitantsSystem.PassableBelief(knowledge, asker, listener, action)) is not { } belief)
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
            // A request the service refused as invalid has its own name in the log and in the event (ADR-0009).
            var note = ReplayNotes.OfError(message.Json);
            Log.Add(new DecisionLogEntry(DecisionLogKind.Error, message.RequestId, world.Tick, message.Json, note));
            if (question is not null && IsRetryable(message.Json))
            {
                // B2487: no fixed time limit; the question stays open and is asked again later.
                SetOpen(question with { RetryAtTick = world.Tick + ((long)_settings.RetryAfterMinutes * world.Calendar.Settings.TicksPerHour / 60) });
                return;
            }

            Close(world, question, "question_failed", note == ReplayNotes.RequestRejected ? note : null);
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
        var decided = new Activity(action, targetId, destination, null, DecidedTick: world.Tick);
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
        if (response.Thought is { } thought && _guard.Check(thought.Text).Allowed)
        {
            // Thought bubble (B3): the only player-visible text of a decision, in the display language.
            // It is text of the AI, so it passes the content guard like every other visible AI text
            // (youth protection, B2466); a thought the guard does not allow is not shown and nothing
            // replaces it. The decision itself stands: the guard judges words, not the choice.
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

    /// <summary>
    /// Settles an open question that was answered another way than by the decider (the compute night's guess,
    /// B7): it closes as answered, with <paramref name="reason"/> as the event's detail. An answer of the
    /// decider that comes later is dropped like every answer to a closed question. False if it is not open.
    /// </summary>
    public bool Settle(World world, string questionId, string reason)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(questionId);
        if (!_open.TryGetValue(questionId, out var question))
        {
            return false;
        }

        Close(world, question, "question_answered", reason);
        return true;
    }

    private void Close(World world, OpenQuestion? question, string eventKind, string? reason = null)
    {
        if (question is null)
        {
            return;
        }

        RemoveOpen(question);
        _inhabitants.Report(new WorldEvent(eventKind, world.Tick, question.InhabitantId, null, question.Id) { Detail = reason });
    }

    /// <summary>Other inhabitants within the perception radius as it is right now (B2594), nearest first, ties by id.</summary>
    private List<Inhabitant> PerceivedInhabitants(Inhabitant inhabitant)
    {
        var radius = _inhabitants.SightRadiusM;
        var position = inhabitant.Position;
        var everyone = _inhabitants.Ordered;
        var inSight = _othersInSight;
        inSight.Clear();
        for (var i = 0; i < everyone.Count; i++)
        {
            var other = everyone[i];
            if (other.Id == inhabitant.Id)
            {
                continue;
            }

            var distance = Distance(position, other.Position);
            if (distance <= radius)
            {
                inSight.Add((distance, other));
            }
        }

        inSight.Sort(NearestOtherFirst);
        var others = new List<Inhabitant>(inSight.Count);
        foreach (var (_, other) in inSight)
        {
            others.Add(other);
        }

        inSight.Clear();
        return others;
    }

    private static double Distance(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
