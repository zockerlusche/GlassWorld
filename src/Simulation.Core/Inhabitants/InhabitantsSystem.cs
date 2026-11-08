using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>Something that happened in a tick (for the UI, sound, chronicle and tests).</summary>
/// <param name="Kind">Event kind (language-neutral id).</param>
/// <param name="Tick">Tick at which it happened.</param>
/// <param name="InhabitantId">Inhabitant involved.</param>
/// <param name="Action">Action id, if any.</param>
/// <param name="TargetId">Target object id, if any.</param>
public sealed record WorldEvent(string Kind, long Tick, string InhabitantId, string? Action, string? TargetId)
{
    /// <summary>Text for the player (thought bubble), already in the player's language; null for most events.</summary>
    public string? Text { get; init; }

    /// <summary>Language-neutral detail of the event (e.g. the need of a call for help, the type of a gift).</summary>
    public string? Detail { get; init; }
}

/// <summary>An event with its running number (UI and sound read everything since their last number).</summary>
/// <param name="Seq">Running number, starting at 1, never reused.</param>
/// <param name="Event">The event.</param>
public sealed record SequencedEvent(long Seq, WorldEvent Event);

/// <summary>
/// All inhabitants (a world system): needs decay, moods follow drivers, the will to live sinks only after
/// long neglect (B2484), and the routine layer chooses and performs actions whose effects come from the
/// TRUE properties of the target. Experience updates beliefs (the frame for learning, extended in B4a).
/// Every number comes from data; the code names no need, mood or action.
/// </summary>
public sealed class InhabitantsSystem : IWorldSystem
{
    /// <summary>Event: an action started (after walking to the target).</summary>
    public const string ActionStarted = "action_started";

    /// <summary>Event: an action finished and its effects applied.</summary>
    public const string ActionFinished = "action_finished";

    /// <summary>Event: an action was cancelled (target gone).</summary>
    public const string ActionCancelled = "action_cancelled";

    private readonly SortedDictionary<string, Inhabitant> _inhabitants = new(StringComparer.Ordinal);
    private readonly List<WorldEvent> _events = [];
    private readonly InhabitantModel _model;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly WorldObjects _objects;
    private readonly int _ticksPerHour;
    private long _eventsTick = -1;
    private readonly LinkedList<SequencedEvent> _history = new();

    /// <summary>Creates the system.</summary>
    public InhabitantsSystem(InhabitantModel model, ActionRules actions, UtilityBrain brain, WorldObjects objects, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerHour, 1);
        _model = model;
        _actions = actions;
        _brain = brain;
        _objects = objects;
        _ticksPerHour = ticksPerHour;
    }

    /// <inheritdoc/>
    public string Name => "inhabitants";

    /// <summary>All inhabitants in id order.</summary>
    public IEnumerable<Inhabitant> All => _inhabitants.Values;

    /// <summary>Events of the most recent tick that had events (UI, sound and tests read them each tick).</summary>
    public IReadOnlyList<WorldEvent> Events => _events;

    /// <summary>
    /// How many events are kept for <see cref="EventsSince"/>. Technical buffer, not a game value: at
    /// x16 the game advances up to max_ticks_per_frame ticks per frame and reads once per frame.
    /// </summary>
    public const int EventHistoryCapacity = 4096;

    /// <summary>Running number of the last event (0 = none yet).</summary>
    public long LastEventSeq { get; private set; }

    /// <summary>
    /// All kept events with a running number greater than <paramref name="seq"/>, oldest first. A reader
    /// that falls behind by more than <see cref="EventHistoryCapacity"/> events sees a gap in the numbers.
    /// </summary>
    public IReadOnlyList<SequencedEvent> EventsSince(long seq) => [.. _history.Where(e => e.Seq > seq)];

    /// <summary>Raised for every reported event, right away and in report order (chronicle, D471 D8).</summary>
    public event Action<WorldEvent>? Reported;

    /// <summary>Adds an event; the per-tick list is cleared when the first event of a new tick arrives.</summary>
    public void Report(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (EventKinds.Count > 0 && !EventKinds.Contains(worldEvent.Kind))
        {
            throw new InvalidDataException($"Event kind '{worldEvent.Kind}' is not in the event vocabulary (content/vocabulary/events.json).");
        }

        if (worldEvent.Tick != _eventsTick)
        {
            _events.Clear();
            _eventsTick = worldEvent.Tick;
        }

        _events.Add(worldEvent);
        LastEventSeq++;
        _history.AddLast(new SequencedEvent(LastEventSeq, worldEvent));
        if (_history.Count > EventHistoryCapacity)
        {
            _history.RemoveFirst();
        }

        Reported?.Invoke(worldEvent);
    }

    /// <summary>
    /// Creates the start inhabitants from the world seed (B2503): traits and start needs are drawn from
    /// the data ranges with one stream per inhabitant, so the same seed always gives the same people.
    /// </summary>
    public void SpawnStartInhabitants(World world, IReadOnlyList<Position> positions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(positions);
        var starters = _model.Start.Inhabitants.ToList();
        if (positions.Count < starters.Count)
        {
            throw new ArgumentException($"Need {starters.Count} start positions.", nameof(positions));
        }

        for (var i = 0; i < starters.Count; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"inh-{i + 1:000000}");
            var rng = world.Random.Get($"inhabitant/{id}/start");
            var traits = _model.Start.Traits.ToDictionary(t => t.Id, t => t.Min + ((t.Max - t.Min) * rng.NextDouble()), StringComparer.Ordinal);
            var needs = _model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
            foreach (var range in _model.Start.Needs)
            {
                needs[range.Id] = range.Min + ((range.Max - range.Min) * rng.NextDouble());
            }

            var moods = _model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
            var sex = starters[i].Sex == StartInhabitantSex.Female ? "female" : "male";
            var inhabitant = new Inhabitant(id, sex, starters[i].Group, positions[i], needs, moods, traits, _model.WillToLive.Start);
            if (Looks is { } looks)
            {
                // Own stream, not kept among the world's streams: the record is drawn once and saved.
                inhabitant.Appearance = Appearance.Draw(looks, RandomStreams.Create(world.Seed, $"inhabitant/{id}/appearance"), looks.StartAgeDays);
            }

            Add(inhabitant);
        }
    }

    /// <summary>Adds an inhabitant (start, tests, save games).</summary>
    public void Add(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        _inhabitants.Add(inhabitant.Id, inhabitant);
    }

    /// <summary>Routes on the island map (B2 R10); null on worlds without a map, where walking is straight.</summary>
    public Map.IRouteFinder? Routes { get; init; }

    /// <summary>Knowledge with its source and trust (B4a R3/R4/R7); without it learning works as in B1.</summary>
    public KnowledgeSettings? Knowledge { get; init; }

    /// <summary>Event kinds from the vocabulary; reporting another kind is a data error (empty = no check).</summary>
    public IReadOnlySet<string> EventKinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether an inhabitant has an open question to the AI about a type (W1595); set by the decisions
    /// system. While it is open the routine leaves the listed actions on that type alone.
    /// </summary>
    public Func<string, string, bool>? QuestionOpen { get; set; }

    /// <summary>Event: an inhabitant now knows or believes something new (B4a R3).</summary>
    public const string KnowledgeGained = "knowledge_gained";

    /// <summary>Detail of a cancelled action whose destination cannot be reached on foot.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Detail of a cancelled action whose route search ran out of its budget.</summary>
    public const string NoRouteInBudget = "no_route_in_budget";

    /// <summary>Value ranges for the appearance of new inhabitants (null = no appearance, tests).</summary>
    public LookAppearance? Looks { get; init; }

    /// <summary>Looks up an inhabitant.</summary>
    public Inhabitant Get(string id) => _inhabitants[id];

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var inhabitant in _inhabitants.Values)
        {
            DecayNeeds(inhabitant);
            UpdateMoods(inhabitant);
            UpdateWillToLive(inhabitant);
            Act(world, inhabitant);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_inhabitants.Count);
        foreach (var inhabitant in _inhabitants.Values)
        {
            inhabitant.WriteState(hasher);
        }
    }

    /// <summary>True if the will to live is below the "fading" threshold (consequences in B9).</summary>
    public bool IsFading(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.WillToLive < _model.WillToLive.FadingBelow;
    }

    private double PerTick(double perHour) => perHour / _ticksPerHour;

    private void DecayNeeds(Inhabitant inhabitant)
    {
        foreach (var need in _model.Needs)
        {
            if (inhabitant.Needs.TryGetValue(need.Id, out var value))
            {
                inhabitant.Needs[need.Id] = Math.Clamp(value - PerTick(need.DecayPerHour), 0.0, 1.0);
            }
        }
    }

    private void UpdateMoods(Inhabitant inhabitant)
    {
        foreach (var mood in _model.Moods)
        {
            var value = inhabitant.Moods.GetValueOrDefault(mood.Id, mood.Baseline);
            var step = PerTick(mood.ReturnPerHour);
            value = value > mood.Baseline ? Math.Max(mood.Baseline, value - step) : Math.Min(mood.Baseline, value + step);
            inhabitant.Moods[mood.Id] = value;
        }

        foreach (var driver in _model.MoodDrivers)
        {
            if (inhabitant.Needs.TryGetValue(driver.Need, out var need) && need < driver.Below && inhabitant.Moods.TryGetValue(driver.Mood, out var mood))
            {
                inhabitant.Moods[driver.Mood] = Math.Clamp(mood + PerTick(driver.DeltaPerHour), 0.0, 1.0);
            }
        }
    }

    private void UpdateWillToLive(Inhabitant inhabitant)
    {
        var w = _model.WillToLive;
        var neglected = _model.Needs.Any(n => n.NeglectBelow > 0 && inhabitant.Needs.GetValueOrDefault(n.Id, 1.0) < n.NeglectBelow);
        inhabitant.NeglectTicks = neglected ? inhabitant.NeglectTicks + 1 : 0;
        if (inhabitant.NeglectTicks > (long)w.NeglectHoursBeforeDecline * _ticksPerHour)
        {
            inhabitant.WillToLive = Math.Max(0.0, inhabitant.WillToLive - PerTick(w.DeclinePerHour));
        }
        else if (!neglected && inhabitant.WillToLive < w.Start)
        {
            inhabitant.WillToLive = Math.Min(w.Start, inhabitant.WillToLive + PerTick(w.RecoverPerHour));
        }
    }

    private void Act(World world, Inhabitant inhabitant)
    {
        if (inhabitant.Activity is null && inhabitant.Planned is not null)
        {
            inhabitant.Activity = inhabitant.Planned;
            inhabitant.Planned = null;
        }

        inhabitant.Activity ??= Choose(world, inhabitant);
        var activity = inhabitant.Activity;
        if (activity is null)
        {
            return;
        }

        WorldObject? target = null;
        Inhabitant? listener = null;
        if (activity.TargetId is not null && _inhabitants.TryGetValue(activity.TargetId, out var other))
        {
            // Actions towards another inhabitant (warn, show, teach): walk over to them.
            listener = other;
            // The listener may move: follow them, and plan the way again when they did.
            activity = activity.Destination == other.Position ? activity : activity with { Destination = other.Position, Route = null };
            inhabitant.Activity = activity;
        }
        else if (activity.TargetId is not null && !_objects.TryGet(activity.TargetId, out target))
        {
            Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
            inhabitant.Activity = null;
            return;
        }

        if (activity.RemainingTicks is null)
        {
            var timing = _actions.Timing(activity.Action);
            var destination = target?.Position ?? activity.Destination;
            if (!MoveTowards(world, inhabitant, destination, timing.ReachM))
            {
                return;
            }

            inhabitant.Activity = activity with { RemainingTicks = timing.DurationTicks, Route = null };
            Report(new WorldEvent(ActionStarted, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
            return;
        }

        var remaining = activity.RemainingTicks.Value - 1;
        if (remaining > 0)
        {
            inhabitant.Activity = activity with { RemainingTicks = remaining };
            return;
        }

        Finish(world, inhabitant, activity, target);
        if (listener is not null)
        {
            Pass(world, inhabitant, listener, activity.Action);
        }

        inhabitant.Activity = null;
    }

    private Activity? Choose(World world, Inhabitant inhabitant)
    {
        var radius = _brain.Weights.PerceptionRadiusM;
        // On a map only what can be walked to counts (B2 R10): things beyond deep water or cliffs are no goals.
        var perceived = _objects.All
            .Where(o => DistanceSquared(inhabitant.Position, o.Position) <= radius * radius && (Routes is null || Routes.Connected(inhabitant.Position, o.Position)))
            .ToList();
        var scored = _brain.Score(inhabitant, perceived);
        if (Knowledge is { } knowledge && QuestionOpen is { } open && knowledge.WaitWhileQuestionOpen.Count > 0)
        {
            // W1595: what the inhabitant asked about is not eaten (or drunk …) before the answer comes.
            scored = [.. scored.Where(o => o.TargetId is null
                || !knowledge.WaitWhileQuestionOpen.Contains(o.Action)
                || !_objects.TryGet(o.TargetId, out var thing)
                || !open(inhabitant.Id, thing.TypeId))];
        }

        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];

        if (best.TargetId is not null && _objects.TryGet(best.TargetId, out var target))
        {
            return new Activity(best.Action, best.TargetId, target.Position, null);
        }

        // Actions without a target happen here or, if the data says so, at a random spot nearby
        // (drawn from the inhabitant's own stream).
        var destination = inhabitant.Position;
        if (_actions.Timing(best.Action).RandomNearby)
        {
            var rng = world.Random.Get($"inhabitant/{inhabitant.Id}/wander");
            destination = new Position(
                inhabitant.Position.X + (((rng.NextDouble() * 2.0) - 1.0) * radius),
                inhabitant.Position.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
            if (Routes is not null && !Routes.Connected(inhabitant.Position, destination))
            {
                // A spot in the sea or beyond a cliff: stay here instead (no search for the impossible).
                destination = inhabitant.Position;
            }
        }

        return new Activity(best.Action, null, destination, null);
    }

    private bool MoveTowards(World world, Inhabitant inhabitant, Position destination, double reach)
    {
        if (Routes is { } routes && inhabitant.Activity is { } activity)
        {
            return FollowRoute(world, inhabitant, activity, routes, destination, reach);
        }

        var dx = destination.X - inhabitant.Position.X;
        var dz = destination.Z - inhabitant.Position.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        if (distance <= reach)
        {
            return true;
        }

        var step = _brain.Weights.MoveSpeedMPerMinute * 60.0 / _ticksPerHour;
        var travel = Math.Min(step, distance - reach);
        inhabitant.Position = new Position(inhabitant.Position.X + (dx * travel / distance), inhabitant.Position.Z + (dz * travel / distance));
        return distance - travel <= reach;
    }

    /// <summary>
    /// Walks along the route to the destination (B2 R10): the route is searched once when the walk starts
    /// and kept in the activity (saved and hashed, so a loaded game walks on identically); speed follows
    /// the ground (shallow water, slopes). No route: the action is cancelled with the reason.
    /// </summary>
    private bool FollowRoute(World world, Inhabitant inhabitant, Activity activity, Map.IRouteFinder routes, Position destination, double reach)
    {
        if (Distance(inhabitant.Position, destination) <= reach)
        {
            return true;
        }

        var route = activity.Route;
        if (route is null)
        {
            var found = routes.Find(inhabitant.Position, destination);
            if (found.Status != Map.PathStatus.Found)
            {
                Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId)
                {
                    Detail = found.Status == Map.PathStatus.Unreachable ? Unreachable : NoRouteInBudget,
                });
                inhabitant.Activity = null;
                return false;
            }

            route = found.Waypoints;
        }

        var budget = _brain.Weights.MoveSpeedMPerMinute * 60.0 / _ticksPerHour * routes.SpeedFactor(inhabitant.Position);
        var next = 0;
        var arrived = false;
        while (budget > 1e-9 && next < route.Count)
        {
            var point = route[next];
            var last = next == route.Count - 1;
            var stop = last ? reach : 0.0;
            var distance = Distance(inhabitant.Position, point);
            var travel = Math.Min(budget, Math.Max(0.0, distance - stop));
            if (distance > 0)
            {
                inhabitant.Position = new Position(
                    inhabitant.Position.X + ((point.X - inhabitant.Position.X) * travel / distance),
                    inhabitant.Position.Z + ((point.Z - inhabitant.Position.Z) * travel / distance));
            }

            budget -= travel;
            if (distance - travel <= stop + 1e-9)
            {
                next++;
                arrived = last;
            }
            else
            {
                break;
            }
        }

        inhabitant.Activity = activity with { Route = [.. route.Skip(next)] };
        return arrived || Distance(inhabitant.Position, destination) <= reach;
    }

    private static double Distance(Position a, Position b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));

    private void Finish(World world, Inhabitant inhabitant, Activity activity, WorldObject? target)
    {
        var applying = _actions.Applying(activity.Action, target?.TypeId);
        foreach (var effect in applying.SelectMany(r => r.Effects))
        {
            switch (effect.Kind)
            {
                case EffectKind.Need when inhabitant.Needs.TryGetValue(effect.Id, out var need):
                    inhabitant.Needs[effect.Id] = Math.Clamp(need + effect.Delta, 0.0, 1.0);
                    break;
                case EffectKind.Mood when inhabitant.Moods.TryGetValue(effect.Id, out var mood):
                    inhabitant.Moods[effect.Id] = Math.Clamp(mood + effect.Delta, 0.0, 1.0);
                    break;
                case EffectKind.Will_to_live:
                    inhabitant.WillToLive = Math.Clamp(inhabitant.WillToLive + effect.Delta, 0.0, 1.0);
                    break;
                default:
                    break;
            }
        }

        if (target is not null)
        {
            Learn(world, inhabitant, activity.Action, target.TypeId);
            if (applying.Any(r => r.ConsumesTarget))
            {
                _objects.Remove(target.Id);
            }
        }

        Report(new WorldEvent(ActionFinished, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
    }

    /// <summary>
    /// Experience: for every rule of the action that depends on a property, the inhabitant now believes
    /// what really happened (the property was there or not). With knowledge data the belief is
    /// "experienced" and certain (B4a R3); without it confidence grows with repeated experience (B1).
    /// </summary>
    private void Learn(World world, Inhabitant inhabitant, string action, string typeId)
    {
        var step = _brain.Weights.LearningStep;
        foreach (var property in _actions.Rules(action).Select(r => r.RequiresProperty).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var truth = _objects.Catalog.HasProperty(typeId, property);
            var known = inhabitant.Beliefs.TryGet(typeId, property, out var old);
            if (Knowledge is not { } knowledge)
            {
                // B1: confidence grows with repeated experience.
                inhabitant.Beliefs.Set(typeId, property, truth, known && old.Value == truth ? old.Confidence + step : step);
                continue;
            }

            // B4a R3: one's own experience is certain and overrides what one heard; whoever told the
            // inhabitant something false is trusted less afterwards (and a little more when it was true).
            if (known && old.Source == KnowledgeSources.Heard && old.ToldBy is { } teller)
            {
                var start = Start(knowledge);
                var relation = inhabitant.Relationships.Get(teller, start);
                var change = old.Value == truth ? knowledge.TrustTrueHeardGain : -knowledge.TrustFalseHeardLoss;
                inhabitant.Relationships.Set(teller, relation with { Trust = relation.Trust + change });
            }

            var confidence = Math.Max(knowledge.ExperiencedConfidence, known && old.Value == truth && old.Source == KnowledgeSources.Experienced ? old.Confidence : 0);
            inhabitant.Beliefs.Set(typeId, property, truth, confidence, KnowledgeSources.Experienced, null, world.Tick);
            if (!known || old.Value != truth || old.Source != KnowledgeSources.Experienced)
            {
                Report(new WorldEvent(KnowledgeGained, world.Tick, inhabitant.Id, action, typeId) { Detail = property + "/" + KnowledgeSources.Experienced });
            }
        }
    }

    /// <summary>
    /// Passing on knowledge (B4a R4): the teller's surest belief that the action carries (data) goes to the
    /// listener as heard, as sure as the teller is times the listener's trust in the teller. False beliefs
    /// are passed on just the same; the listener's own experience is never overwritten.
    /// </summary>
    private void Pass(World world, Inhabitant teller, Inhabitant listener, string action)
    {
        if (Knowledge is not { } knowledge || PassableBelief(knowledge, teller, listener, action) is not { } belief)
        {
            return;
        }

        var trust = listener.Relationships.Get(teller.Id, Start(knowledge)).Trust;
        listener.Beliefs.Set(belief.TypeId, belief.PropertyId, belief.Belief.Value, belief.Belief.Confidence * trust, KnowledgeSources.Heard, teller.Id, world.Tick);
        Report(new WorldEvent(KnowledgeGained, world.Tick, listener.Id, action, belief.TypeId) { Detail = belief.PropertyId + "/" + KnowledgeSources.Heard });
    }

    /// <summary>
    /// The belief a passing action would carry from teller to listener, or null: matching the action's data
    /// (properties, value), sure enough, not something the listener experienced itself; surest first, then
    /// type and property by id.
    /// </summary>
    public static (string TypeId, string PropertyId, Belief Belief)? PassableBelief(KnowledgeSettings knowledge, Inhabitant teller, Inhabitant listener, string action)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(teller);
        ArgumentNullException.ThrowIfNull(listener);
        var rule = knowledge.Passing.FirstOrDefault(p => p.Action == action);
        if (rule is null)
        {
            return null;
        }

        (string TypeId, string PropertyId, Belief Belief)? best = null;
        foreach (var b in teller.Beliefs.All)
        {
            if (b.Belief.Confidence < knowledge.MinTellerConfidence
                || (rule.Properties.Count > 0 && !rule.Properties.Contains(b.PropertyId))
                || (rule.Value is { } value && b.Belief.Value != value)
                || (listener.Beliefs.TryGet(b.TypeId, b.PropertyId, out var own) && own.Source == KnowledgeSources.Experienced))
            {
                continue;
            }

            if (best is null || b.Belief.Confidence > best.Value.Belief.Confidence)
            {
                best = b;
            }
        }

        return best;
    }

    private static Relation Start(KnowledgeSettings knowledge) =>
        new(knowledge.RelationshipStart.Affection, knowledge.RelationshipStart.Trust, knowledge.RelationshipStart.Respect);

    private static double DistanceSquared(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
