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
public sealed record OpenQuestion(string Id, string InhabitantId, string Kind, string Subject, string RequestId, long AskedTick, IReadOnlyList<(string OptionId, string Action, string? TargetId)> Options);

/// <summary>A command from the player, checked by the core at the next tick (ADR-0005).</summary>
/// <param name="Kind">Command kind (in B1: <c>inhabitant_clicked</c>).</param>
/// <param name="InhabitantId">Inhabitant concerned.</param>
public sealed record PlayerCommand(string Kind, string InhabitantId);

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
    /// <summary>Trigger: an object type without any belief.</summary>
    public const string UnknownObject = "unknown_object";

    /// <summary>Trigger: the inhabitant noticed the player (e.g. was clicked).</summary>
    public const string NoticedPlayer = "noticed_player";

    /// <summary>Player command: an inhabitant was clicked.</summary>
    public const string InhabitantClicked = "inhabitant_clicked";

    private readonly SortedDictionary<string, OpenQuestion> _open = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _lastAsked = new(StringComparer.Ordinal);
    private readonly Queue<PlayerCommand> _commands = new();
    private readonly DeciderSettings _settings;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly IDecisionPort _port;
    private readonly string _displayLocale;
    private long _counter;

    /// <summary>Creates the system.</summary>
    public DecisionsSystem(DeciderSettings settings, InhabitantsSystem inhabitants, WorldObjects objects, ActionRules actions, UtilityBrain brain, IDecisionPort port, string displayLocale)
    {
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

    /// <summary>Queues a player command; it takes effect at the next tick.</summary>
    public CommandResult Submit(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != InhabitantClicked)
        {
            return new CommandResult(false, "command.rejected.unknown");
        }

        if (!_inhabitants.All.Any(i => i.Id == command.InhabitantId))
        {
            return new CommandResult(false, "command.rejected.no_inhabitant");
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

        while (_commands.TryDequeue(out var command))
        {
            if (Enabled(NoticedPlayer))
            {
                Ask(world, _inhabitants.Get(command.InhabitantId), NoticedPlayer, "player", null);
            }
        }

        if (Enabled(UnknownObject))
        {
            foreach (var inhabitant in _inhabitants.All)
            {
                var unknown = Perceived(inhabitant).FirstOrDefault(o => !inhabitant.Beliefs.About(o.TypeId).Any());
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
            hasher.Write(q.Id).Write(q.InhabitantId).Write(q.Kind).Write(q.Subject).Write(q.RequestId).Write(q.AskedTick).Write(q.Options.Count);
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
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id, _counter);
        var options = BuildOptions(target);
        var request = BuildRequest(world, inhabitant, requestId, options);
        var json = ContractSerializer.Serialize(request);
        var question = new OpenQuestion(string.Create(CultureInfo.InvariantCulture, $"q-{_counter:000000}"), inhabitant.Id, kind, subject, requestId, world.Tick, options);
        _open.Add(question.Id, question);
        _lastAsked[cooldownKey] = world.Tick;
        Log.Add(new DecisionLogEntry(DecisionLogKind.Request, requestId, world.Tick, json, null));
        _inhabitants.Report(new WorldEvent("question_opened", world.Tick, inhabitant.Id, null, question.Id));
        _port.Send(requestId, json);
    }

    /// <summary>Options the core allows: target actions on the subject (reach &gt; 0), and every action without a target.</summary>
    private List<(string OptionId, string Action, string? TargetId)> BuildOptions(WorldObject? target)
    {
        var raw = new List<(string Action, string? TargetId)>();
        foreach (var action in _actions.Actions)
        {
            var timing = _actions.Timing(action);
            if (timing.ReachM > 0)
            {
                if (target is not null)
                {
                    raw.Add((action, target.Id));
                }
            }
            else
            {
                raw.Add((action, null));
            }
        }

        return [.. raw.Select((o, i) => (string.Create(CultureInfo.InvariantCulture, $"o{i + 1}"), o.Action, o.TargetId))];
    }

    private DecisionRequest BuildRequest(World world, Inhabitant inhabitant, string requestId, List<(string OptionId, string Action, string? TargetId)> options)
    {
        var seed = world.Random.Get("decisions/seed").NextInt(0, int.MaxValue);
        return new DecisionRequest
        {
            ContractVersion = "0.1",
            RequestId = requestId,
            Tick = world.Tick,
            Seed = seed,
            DeadlineMs = _settings.DeadlineMs,
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Inhabitant = new InhabitantState
            {
                Id = inhabitant.Id,
                Needs = [.. inhabitant.Needs.Select(n => new LevelEntry { Id = n.Key, Value = n.Value })],
                Mood = [.. inhabitant.Moods.Where(m => m.Value > 0).Select(m => new LevelEntry { Id = m.Key, Value = m.Value })],
                Traits = [.. inhabitant.Traits.Select(t => new LevelEntry { Id = t.Key, Value = t.Value })],
                WillToLive = inhabitant.WillToLive,
            },
            Environment = new EnvironmentState
            {
                TimeOfDay = world.Now.Phase.ToString().ToLowerInvariant(),
                TemperatureC = _settings.FallbackEnvironment.TemperatureC,
                Weather = [.. _settings.FallbackEnvironment.Weather],
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
            },
            Options = [.. options.Select(o => new Option { OptionId = o.OptionId, Action = o.Action, Target = o.TargetId })],
        };
    }

    private void Apply(World world, PortMessage message)
    {
        var question = _open.Values.FirstOrDefault(q => q.RequestId == message.RequestId);
        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.Error, message.RequestId, world.Tick, message.Json, ReplayNotes.ServiceError));
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
        inhabitant.Activity = new Activity(action, targetId, destination, null);
        Close(world, question, "question_answered");
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

    private static double Distance(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
