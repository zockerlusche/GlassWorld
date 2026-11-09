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

/// <summary>What an activity or a decider option is aimed at: another inhabitant or an object of the world.</summary>
/// <param name="Inhabitant">The other inhabitant (passing knowledge on, B4a R4), or null.</param>
/// <param name="Thing">The object, or null.</param>
public readonly record struct ActivityTarget(Inhabitant? Inhabitant, WorldObject? Thing)
{
    /// <summary>Where the target is now.</summary>
    public Position Position => Inhabitant?.Position ?? Thing!.Position;
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

    /// <summary>Event: an action was cancelled (target gone, or the action rests after repeats without effect).</summary>
    public const string ActionCancelled = "action_cancelled";

    /// <summary>Detail of <see cref="ActionFinished"/> when the run without effect reached the limit and the action now rests (W1696).</summary>
    public const string RepeatLimit = "repeat_limit";

    /// <summary>Detail of <see cref="ActionCancelled"/> for an action that rests after repeats without effect (W1696).</summary>
    public const string Resting = "resting";

    private readonly SortedDictionary<string, Inhabitant> _inhabitants = new(StringComparer.Ordinal);
    private readonly List<WorldEvent> _events = [];
    private readonly InhabitantModel _model;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly GaitTable _gaits;
    private readonly WorldObjects _objects;
    private readonly int _ticksPerHour;
    private long _eventsTick = -1;
    private readonly LinkedList<SequencedEvent> _history = new();
    private readonly Dictionary<string, Position> _tickStart = new(StringComparer.Ordinal);

    /// <summary>Creates the system.</summary>
    public InhabitantsSystem(InhabitantModel model, ActionRules actions, UtilityBrain brain, GaitTable gaits, WorldObjects objects, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(gaits);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerHour, 1);
        _model = model;
        _actions = actions;
        _brain = brain;
        _gaits = gaits;
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
                var place = ClothingAt?.Invoke(world, positions[i]) ?? ClothingContext.Unknown;
                inhabitant.Appearance = Appearance.Draw(looks, RandomStreams.Create(world.Seed, $"inhabitant/{id}/appearance"), looks.StartAgeDays, sex, place);
            }

            Add(inhabitant);
        }
    }

    /// <summary>Where and when a start inhabitant is dressed (F4: season, climate, biome, tech level); null = unknown.</summary>
    public Func<World, Position, ClothingContext>? ClothingAt { get; init; }

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

    /// <summary>
    /// The one resolver for targets of activities and decider options (review GW3 #118): another
    /// inhabitant first, then an object; null when it no longer exists. Inhabitant and object ids never collide.
    /// </summary>
    public ActivityTarget? ResolveTarget(string targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        if (_inhabitants.TryGetValue(targetId, out var other))
        {
            return new ActivityTarget(other, null);
        }

        return _objects.TryGet(targetId, out var obj) ? new ActivityTarget(null, obj) : null;
    }

    /// <summary>
    /// The gait the inhabitant walks in (gait id of content/behaviour/gaits.json), or null when it is not on
    /// its way (no activity, or the action has started). Derived from the state, so it is not saved or hashed.
    /// </summary>
    public string? GaitOf(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.Activity is { RemainingTicks: null } activity ? _gaits.GaitFor(inhabitant, activity.Action) : null;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _tickStart.Clear();
        foreach (var inhabitant in _inhabitants.Values)
        {
            _tickStart[inhabitant.Id] = inhabitant.Position;
        }

        foreach (var inhabitant in _inhabitants.Values)
        {
            DecayNeeds(inhabitant);
            UpdateMoods(inhabitant);
            UpdateWillToLive(inhabitant);
            var before = inhabitant.Position;
            var planned = inhabitant.Activity;
            Act(world, inhabitant);
            if (inhabitant.Position != before)
            {
                var walked = inhabitant.Position;
                KeepClearOnTheWay(inhabitant, before);
                if (inhabitant.Position != walked && inhabitant.Activity is { Route: not null } activity
                    && planned is not null && planned.Action == activity.Action && planned.TargetId == activity.TargetId)
                {
                    // Shortened on a route: the waypoints it did not reach stay ahead (no corner cut over blocked cells).
                    inhabitant.Activity = activity with { Route = planned.Route };
                }

                Separate(inhabitant);
            }
        }
    }

    /// <summary>
    /// W1695 (walking through each other): the game shows a move as a straight walk over the tick, both
    /// inhabitants at once. A move that would come closer than the minimum separation to another on the way
    /// (the other standing, or walking its own straight move of this tick) is shortened to the longest part
    /// that stays clear; if hardly anything is left, the walker steps aside (right, else left) when that is
    /// clear, else it waits. Pairs already closer at the start of the tick are left to the separation.
    /// Deterministic: fixed bisection, no random draw.
    /// </summary>
    private void KeepClearOnTheWay(Inhabitant mover, Position from)
    {
        if (Space is not { } space || space.MinSeparationM <= 0 || PathClear(mover, from, mover.Position))
        {
            return;
        }

        var to = mover.Position;
        var (dx, dz) = (to.X - from.X, to.Z - from.Z);
        var (low, high) = (0.0, 1.0);
        for (var i = 0; i < 16; i++)
        {
            var mid = (low + high) / 2;
            (low, high) = PathClear(mover, from, new Position(from.X + (dx * mid), from.Z + (dz * mid))) ? (mid, high) : (low, mid);
        }

        var length = Math.Sqrt((dx * dx) + (dz * dz));
        var part = new Position(from.X + (dx * low), from.Z + (dz * low));

        // A part of a route that cut a corner may end on a blocked cell: then it waits where it was.
        part = Walkable(from, part) ? part : from;
        if ((low * length >= space.YieldOffsetM / 2 && part != from) || length < 1e-9)
        {
            mover.Position = part;
            return;
        }

        // Blocked straight ahead: a step aside, right first (as when meeting head-on), lets it pass next tick.
        var (rx, rz) = (-dz / length * space.YieldOffsetM, dx / length * space.YieldOffsetM);
        foreach (var aside in new[] { new Position(from.X + rx, from.Z + rz), new Position(from.X - rx, from.Z - rz) })
        {
            if (Walkable(from, aside) && PathClear(mover, from, aside))
            {
                mover.Position = aside;
                return;
            }
        }

        mover.Position = part;
    }

    /// <summary>
    /// Whether a straight move of <paramref name="mover"/> from <paramref name="from"/> to <paramref name="to"/>
    /// over this tick stays at least the minimum separation from every other inhabitant moving straight from
    /// where it began the tick to where it is now (pairs closer already at the start do not count).
    /// </summary>
    private bool PathClear(Inhabitant mover, Position from, Position to)
    {
        var limit = Space!.MinSeparationM - 1e-9;
        foreach (var other in _inhabitants.Values)
        {
            if (ReferenceEquals(other, mover))
            {
                continue;
            }

            var start = _tickStart.GetValueOrDefault(other.Id, other.Position);

            // Relative position p + t·v for t in [0, 1]; its closest approach to zero.
            var (px, pz) = (from.X - start.X, from.Z - start.Z);
            var (vx, vz) = ((to.X - from.X) - (other.Position.X - start.X), (to.Z - from.Z) - (other.Position.Z - start.Z));
            if ((px * px) + (pz * pz) < limit * limit)
            {
                continue;
            }

            var vv = (vx * vx) + (vz * vz);
            var t = vv < 1e-12 ? 0 : Math.Clamp(-((px * vx) + (pz * vz)) / vv, 0, 1);
            var (cx, cz) = (px + (vx * t), pz + (vz * t));
            if ((cx * cx) + (cz * cz) < limit * limit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whom the inhabitant turns its whole body to (B2528, W1691), or null. One action owns legs and body
    /// (TL #20001): the actor of a running action aimed at another inhabitant faces its listener; the listener
    /// faces the first such actor (id order) only when it has no action of its own or one the data lets turn
    /// (spacing.listener_turns_during). Nobody turns while walking. Derived from the state, never stored.
    /// </summary>
    public string? FacingOf(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (Space is not { } space || inhabitant.Activity is { RemainingTicks: null })
        {
            return null;
        }

        if (inhabitant.Activity is { TargetId: { } id } && _inhabitants.ContainsKey(id))
        {
            return id;
        }

        if (inhabitant.Activity is { } own && !space.ListenerTurnsDuring.Contains(own.Action, StringComparer.Ordinal))
        {
            return null;
        }

        return _inhabitants.Values.FirstOrDefault(o => o.Activity is { RemainingTicks: not null } a && a.TargetId == inhabitant.Id)?.Id;
    }

    /// <summary>
    /// What an action aimed at another inhabitant is about: the type id of the belief it would pass on
    /// (B4a R4), or null (no such action, no knowledge data, nothing to pass). For the UI; never stored.
    /// </summary>
    public string? TopicOf(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.Activity is { TargetId: { } id } activity
            && Knowledge is { } knowledge
            && _inhabitants.TryGetValue(id, out var listener)
            && PassableBelief(knowledge, inhabitant, listener, activity.Action) is { } belief
                ? belief.TypeId
                : null;
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

    /// <summary>Event kind of an answered question whose queued answer was dropped before it started (vocabulary event).</summary>
    public const string AnswerExpired = "answer_expired";

    /// <summary>Reason of a queued answer dropped because a need fell below its neglect threshold while it waited.</summary>
    public const string NeedNeglected = "need_neglected";

    private void Act(World world, Inhabitant inhabitant)
    {
        if (inhabitant.Activity is null && inhabitant.Planned is { } planned)
        {
            inhabitant.Planned = null;
            // A queued answer (an action the decider may be offered; a building step never is, the content
            // check guarantees it) goes stale when a need fell below its neglect threshold while it waited
            // and the answer serves none of the neglected needs (F5275 B, review GW3 #137): it is dropped
            // and logged, and the routine or the decider decides anew. A thirsty inhabitant keeps a queued
            // drink.
            var neglected = _model.Needs.Where(n => n.NeglectBelow > 0 && inhabitant.Needs.GetValueOrDefault(n.Id, 1.0) < n.NeglectBelow).Select(n => n.Id).ToList();
            if (_actions.OfferedActions.Contains(planned.Action, StringComparer.Ordinal)
                && neglected.Count > 0 && !neglected.Any(need => _brain.Serves(planned.Action, need)))
            {
                Report(new WorldEvent(AnswerExpired, world.Tick, inhabitant.Id, planned.Action, planned.TargetId) { Detail = NeedNeglected });
            }
            else
            {
                inhabitant.Activity = planned;
            }
        }

        inhabitant.Activity ??= Choose(world, inhabitant);
        var activity = inhabitant.Activity;
        if (activity is null)
        {
            return;
        }

        if (activity.RemainingTicks is null && Rests(inhabitant, activity.Action, activity.TargetId, world.Tick))
        {
            // W1696: an answer that waited for an action which now rests after repeats without effect is dropped.
            Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = Resting });
            inhabitant.Activity = null;
            return;
        }

        WorldObject? target = null;
        Inhabitant? listener = null;
        if (activity.TargetId is not null)
        {
            if (ResolveTarget(activity.TargetId) is not { } resolved)
            {
                Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
                inhabitant.Activity = null;
                return;
            }

            target = resolved.Thing;
            listener = resolved.Inhabitant;
        }

        if (listener is not null && activity.RemainingTicks is null && Following(activity, listener))
        {
            // Actions towards another inhabitant (warn, show, teach): walk over to them and follow when they
            // move. On a route map the way is planned again only once they have moved more than the action's
            // reach, not every tick. Follow-up (review GW3 #118): a give-up rule for a listener who keeps
            // walking away.
            activity = activity with { Destination = listener.Position, Route = null };
            inhabitant.Activity = activity;
        }

        if (activity.RemainingTicks is null)
        {
            var timing = _actions.Timing(activity.Action);
            var destination = target?.Position ?? activity.Destination;
            var from = inhabitant.Position;
            if (!MoveTowards(world, inhabitant, destination, ReachOf(activity.Action, listener)))
            {
                if (inhabitant.Activity is not null)
                {
                    Yield(inhabitant, from, listener);
                }

                return;
            }

            if (listener is not null)
            {
                KeepConversationDistance(inhabitant, listener);
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

        var seq = LastEventSeq;
        var changed = Finish(world, inhabitant, activity, target);
        if (listener is not null)
        {
            Pass(world, inhabitant, listener, activity.Action);
        }

        changed |= _history.Reverse().TakeWhile(e => e.Seq > seq).Any(e => e.Event.Kind == KnowledgeGained);
        var limit = CountRepeat(world, inhabitant, activity, changed);
        Report(new WorldEvent(ActionFinished, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = limit ? RepeatLimit : null });
        inhabitant.Activity = null;
    }

    /// <summary>
    /// Counts runs of the same action on the same target at one spot that change nothing (W1696, data:
    /// repetition): an action with an effect ends the run; at the limit the action rests for the data's hours.
    /// Returns true when the limit was reached now.
    /// </summary>
    private bool CountRepeat(World world, Inhabitant inhabitant, Activity activity, bool changed)
    {
        inhabitant.Resting.RemoveAll(r => r.Until <= world.Tick);

        if (_brain.Weights.Repetition is not { } repetition || changed || repetition.ExemptActions?.Contains(activity.Action) == true)
        {
            inhabitant.Repeat = null;
            return false;
        }

        var run = inhabitant.Repeat is { } r && r.Action == activity.Action && r.TargetId == activity.TargetId
            && Distance(r.Place, inhabitant.Position) <= repetition.SamePlaceM
            ? r with { Count = r.Count + 1 }
            : new RepeatRun(activity.Action, activity.TargetId, inhabitant.Position, 1);
        if (run.Count < repetition.MaxRepeatsWithoutEffect)
        {
            inhabitant.Repeat = run;
            return false;
        }

        inhabitant.Repeat = null;
        inhabitant.Resting.RemoveAll(r => r.Action == activity.Action && r.TargetId == activity.TargetId
            && (r.TargetId is not null || Distance(r.Place, inhabitant.Position) <= repetition.SamePlaceM));
        inhabitant.Resting.Add(new RestingAction(activity.Action, activity.TargetId, inhabitant.Position, world.Tick + Math.Max(1, (long)Math.Round(repetition.RestHours * _ticksPerHour))));
        return true;
    }

    /// <summary>Whether the action on that target (or, without one, here) rests for the inhabitant (W1696).</summary>
    private bool Rests(Inhabitant inhabitant, string action, string? targetId, long tick) =>
        inhabitant.Resting.Count > 0 && inhabitant.Rests(action, targetId, inhabitant.Position, tick, _brain.Weights.Repetition?.SamePlaceM ?? 0);

    /// <summary>Whether a walk to a listener must head for the listener's new position.</summary>
    private bool Following(Activity activity, Inhabitant listener) =>
        Routes is null
            ? activity.Destination != listener.Position
            : Distance(activity.Destination, listener.Position) > ReachOf(activity.Action, listener);

    /// <summary>Personal space between inhabitants (content/behaviour/gaits.json spacing), or null.</summary>
    private Spacing? Space => _gaits.Data.Spacing;

    /// <summary>
    /// Distance at which an action can start: towards another inhabitant the upper conversation distance
    /// (W1691), else the action's reach.
    /// </summary>
    private double ReachOf(string action, Inhabitant? listener) =>
        listener is not null && Space is { } space ? space.ConversationMaxM : _actions.Timing(action).ReachM;

    /// <summary>Whether a step from <paramref name="from"/> to <paramref name="to"/> stays on walkable ground.</summary>
    private bool Walkable(Position from, Position to) => Routes is null || Routes.Connected(from, to);

    /// <summary>
    /// W1691: an actor that arrived closer to its listener than the lower conversation distance steps back
    /// on the line between them (stays where it is when that is not walkable).
    /// </summary>
    private void KeepConversationDistance(Inhabitant actor, Inhabitant listener)
    {
        if (Space is not { } space)
        {
            return;
        }

        var dx = actor.Position.X - listener.Position.X;
        var dz = actor.Position.Z - listener.Position.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        if (distance >= space.ConversationMinM || distance < 1e-9)
        {
            return;
        }

        var back = new Position(listener.Position.X + (dx * space.ConversationMinM / distance), listener.Position.Z + (dz * space.ConversationMinM / distance));
        if (Walkable(actor.Position, back))
        {
            actor.Position = back;
        }
    }

    /// <summary>
    /// B2569 (Marcel W1688): a walker that has another inhabitant ahead within the look-ahead and closer to
    /// its line than the minimum separation steps to its RIGHT (relative to its walking direction this tick),
    /// in addition to its step; the one it walks to is no obstacle. Two walkers meeting head-on both step
    /// to their right and pass each other. Deterministic: no random draw.
    /// </summary>
    private void Yield(Inhabitant walker, Position from, Inhabitant? walkingTo)
    {
        if (Space is not { } space || space.YieldOffsetM <= 0)
        {
            return;
        }

        var dx = walker.Position.X - from.X;
        var dz = walker.Position.Z - from.Z;
        var length = Math.Sqrt((dx * dx) + (dz * dz));
        if (length < 1e-9)
        {
            return;
        }

        // Map x and z are the ground plane as the game shows it (x to the right, z towards the viewer), so
        // the right-hand side of the walking direction (ux, uz) is (-uz, ux).
        var (ux, uz) = (dx / length, dz / length);
        var (rx, rz) = (-uz, ux);
        var threatened = false;
        foreach (var other in _inhabitants.Values)
        {
            if (ReferenceEquals(other, walker) || ReferenceEquals(other, walkingTo))
            {
                continue;
            }

            var ox = other.Position.X - from.X;
            var oz = other.Position.Z - from.Z;
            var along = (ox * ux) + (oz * uz);
            var aside = (ox * rx) + (oz * rz);
            if (along > 0 && along <= space.YieldLookaheadM && Math.Abs(aside) < space.MinSeparationM)
            {
                threatened = true;
                break;
            }
        }

        var right = new Position(walker.Position.X + (rx * space.YieldOffsetM), walker.Position.Z + (rz * space.YieldOffsetM));
        if (threatened && Walkable(walker.Position, right))
        {
            walker.Position = right;
        }
    }

    /// <summary>
    /// W1687: no inhabitant ends a move closer than the minimum separation to another. The one that moved
    /// goes to the free spot at exactly that distance beside the other that lies nearest to where it is
    /// heading (its target, else its own spot): eight directions around the other, starting with straight
    /// away from it, all walkable and clear of everyone else. Deterministic, square roots only (no trigonometry).
    /// </summary>
    private void Separate(Inhabitant mover)
    {
        if (Space is not { } space || space.MinSeparationM <= 0)
        {
            return;
        }

        var limit = space.MinSeparationM - 1e-9;
        var c = Math.Sqrt(0.5);
        for (var round = 0; round < 3; round++)
        {
            var other = _inhabitants.Values.FirstOrDefault(o => !ReferenceEquals(o, mover) && Distance(o.Position, mover.Position) < limit);
            if (other is null)
            {
                return;
            }

            var anchor = AnchorOf(mover);
            var bx = mover.Position.X - other.Position.X;
            var bz = mover.Position.Z - other.Position.Z;
            var length = Math.Sqrt((bx * bx) + (bz * bz));
            (bx, bz) = length < 1e-9 ? (1.0, 0.0) : (bx / length, bz / length);
            (double X, double Z)[] directions =
            [
                (bx, bz),
                ((bx * c) - (bz * c), (bx * c) + (bz * c)),
                ((bx * c) + (bz * c), (bz * c) - (bx * c)),
                (-bz, bx),
                (bz, -bx),
                ((-bx * c) - (bz * c), (bx * c) - (bz * c)),
                ((bz * c) - (bx * c), (-bx * c) - (bz * c)),
                (-bx, -bz),
            ];
            Position? best = null;
            var bestDistance = double.MaxValue;
            foreach (var (x, z) in directions)
            {
                var spot = new Position(other.Position.X + (x * space.MinSeparationM), other.Position.Z + (z * space.MinSeparationM));
                if (!Walkable(mover.Position, spot)
                    || _inhabitants.Values.Any(o => !ReferenceEquals(o, mover) && !ReferenceEquals(o, other) && Distance(o.Position, spot) < limit)
                    || !BesideNotBeyond(mover.Position, other.Position, spot))
                {
                    continue;
                }

                var d = Distance(spot, anchor);
                if (d < bestDistance - 1e-9)
                {
                    (best, bestDistance) = (spot, d);
                }
            }

            if (best is not { } chosen)
            {
                return;
            }

            mover.Position = chosen;
        }
    }

    /// <summary>
    /// W1695: a separation spot on the near side of the other (not past it), so the shown move never leads
    /// through the other: the spot lies no further along the line from the mover through the other than the
    /// other itself.
    /// </summary>
    private static bool BesideNotBeyond(Position mover, Position other, Position spot)
    {
        var (ax, az) = (other.X - mover.X, other.Z - mover.Z);
        return ((spot.X - other.X) * ax) + ((spot.Z - other.Z) * az) <= 1e-9;
    }

    /// <summary>Where an inhabitant is heading: its target's position, its activity's destination, or its own spot.</summary>
    private Position AnchorOf(Inhabitant inhabitant) =>
        inhabitant.Activity is not { } activity ? inhabitant.Position
            : activity.TargetId is { } id && ResolveTarget(id) is { } target ? target.Position
            : activity.Destination;

    private Activity? Choose(World world, Inhabitant inhabitant)
    {
        var radius = _brain.Weights.PerceptionRadiusM;
        // On a map only what can be walked to counts (B2 R10): things beyond deep water or cliffs are no goals.
        var perceived = _objects.All
            .Where(o => DistanceSquared(inhabitant.Position, o.Position) <= radius * radius && (Routes is null || Routes.Connected(inhabitant.Position, o.Position)))
            .ToList();

        // Looking further away gives up after the data's hours, until the need is met or something that
        // seems as good is in sight again (review GW3 #118: no endless search for what is gone).
        var giveUpTicks = (long)(_brain.Weights.SearchGiveUpHours * _ticksPerHour);
        var givenUp = inhabitant.SearchSince.Where(s => world.Tick - s.Value >= giveUpTicks).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var searching = new HashSet<string>(StringComparer.Ordinal);
        var scored = _brain.Score(inhabitant, perceived, givenUp, searching);
        if (inhabitant.Resting.Count > 0 && scored.Any(o => Rests(inhabitant, o.Action, o.TargetId, world.Tick)))
        {
            // W1696: an action resting after repeats without effect is not chosen, and nothing is done in its
            // place that no need asks for (score 0): the inhabitant then just stands there instead of being
            // sent off on a stroll that leads it away from water and food.
            scored = [.. scored.Where(o => !Rests(inhabitant, o.Action, o.TargetId, world.Tick) && o.Score > 0)];
        }

        foreach (var over in inhabitant.SearchSince.Keys.Where(k => !searching.Contains(k)).ToList())
        {
            inhabitant.SearchSince.Remove(over);
        }

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
        if (best.SearchFor is { } search)
        {
            inhabitant.SearchSince.TryAdd(search, world.Tick);
        }

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

        var step = StepOf(inhabitant);
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

        var budget = StepOf(inhabitant) * routes.SpeedFactor(inhabitant.Position);
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

    /// <summary>Metres the inhabitant walks this tick on flat ground, by its gait (data, F5264).</summary>
    private double StepOf(Inhabitant inhabitant) => _gaits.MetresPerTick(GaitOf(inhabitant) ?? _gaits.Data.DefaultGait);

    private static double Distance(Position a, Position b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));

    /// <summary>Applies the action's effects; true if a need, a mood or the will to live changed (by at least the data's min_effect) or the target was used up.</summary>
    private bool Finish(World world, Inhabitant inhabitant, Activity activity, WorldObject? target)
    {
        var applying = _actions.Applying(activity.Action, target?.TypeId);
        var changed = false;
        var least = Math.Max(_brain.Weights.Repetition?.MinEffect ?? 0, double.Epsilon);
        foreach (var effect in applying.SelectMany(r => r.Effects))
        {
            switch (effect.Kind)
            {
                case EffectKind.Need when inhabitant.Needs.TryGetValue(effect.Id, out var need):
                    inhabitant.Needs[effect.Id] = Math.Clamp(need + effect.Delta, 0.0, 1.0);
                    changed |= Math.Abs(inhabitant.Needs[effect.Id] - need) >= least;
                    break;
                case EffectKind.Mood when inhabitant.Moods.TryGetValue(effect.Id, out var mood):
                    inhabitant.Moods[effect.Id] = Math.Clamp(mood + effect.Delta, 0.0, 1.0);
                    changed |= Math.Abs(inhabitant.Moods[effect.Id] - mood) >= least;
                    break;
                case EffectKind.Will_to_live:
                    var will = inhabitant.WillToLive;
                    inhabitant.WillToLive = Math.Clamp(will + effect.Delta, 0.0, 1.0);
                    changed |= Math.Abs(inhabitant.WillToLive - will) >= least;
                    break;
                default:
                    break;
            }
        }

        if (target is not null)
        {
            Learn(world, inhabitant, activity.Action, target.TypeId);
            if (Knowledge?.Looking is { } looking && string.Equals(activity.Action, looking.Action, StringComparison.Ordinal))
            {
                Look(world, inhabitant, looking, target);
            }
            if (applying.Any(r => r.ConsumesTarget))
            {
                _objects.Remove(target.Id);
                changed = true;
            }
        }

        return changed;
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
    /// Looking closely (TL #19921): the inhabitant now sees the thing's visible properties (true or false, as
    /// sure as the data says, source seen; never over something tried or surer), guesses from them what it might be good for (source guessed, never replacing
    /// a belief it has) and remembers where the thing stands. Effects are never learned by looking.
    /// </summary>
    private void Look(World world, Inhabitant inhabitant, KnowledgeLooking looking, WorldObject target)
    {
        var typeId = target.TypeId;
        foreach (var property in looking.VisibleProperties)
        {
            // Looking never overrides what was tried or what the inhabitant is at least as sure of (review GW2 #142).
            if (inhabitant.Beliefs.TryGet(typeId, property, out var old) && (old.Source == KnowledgeSources.Experienced || old.Confidence >= looking.InspectConfidence))
            {
                continue;
            }

            var truth = _objects.Catalog.HasProperty(typeId, property);
            inhabitant.Beliefs.Set(typeId, property, truth, looking.InspectConfidence, KnowledgeSources.Seen, null, world.Tick);
            if (truth)
            {
                Report(new WorldEvent(KnowledgeGained, world.Tick, inhabitant.Id, looking.Action, typeId) { Detail = property + "/" + KnowledgeSources.Seen });
            }
        }

        foreach (var guess in looking.Guesses)
        {
            if (_objects.Catalog.HasProperty(typeId, guess.Seen) && !inhabitant.Beliefs.TryGet(typeId, guess.Property, out _))
            {
                inhabitant.Beliefs.Set(typeId, guess.Property, guess.Value, looking.GuessedConfidence, KnowledgeSources.Guessed, null, world.Tick);
                Report(new WorldEvent(KnowledgeGained, world.Tick, inhabitant.Id, looking.Action, typeId) { Detail = guess.Property + "/" + KnowledgeSources.Guessed });
            }
        }

        inhabitant.RememberPlace(target.Id, new PlaceMemory(typeId, target.Position, world.Tick), looking.PlaceMemoryMax);
    }

    /// <summary>Whether the inhabitant has looked at a type: it believes something about a visible property of it.</summary>
    public static bool HasLookedAt(KnowledgeSettings? knowledge, Inhabitant inhabitant, string typeId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return knowledge?.Looking is { } looking && looking.VisibleProperties.Any(p => inhabitant.Beliefs.TryGet(typeId, p, out _));
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
    /// type and property by id. A belief the listener already holds the same way and at least as surely as it
    /// would hear it is not passed again (W1696: telling what the other already knows brings nothing).
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

        var trust = listener.Relationships.Get(teller.Id, Start(knowledge)).Trust;
        (string TypeId, string PropertyId, Belief Belief)? best = null;
        foreach (var b in teller.Beliefs.All)
        {
            if (b.Belief.Confidence < knowledge.MinTellerConfidence
                || (rule.Properties.Count > 0 && !rule.Properties.Contains(b.PropertyId))
                || (rule.Value is { } value && b.Belief.Value != value)
                || (listener.Beliefs.TryGet(b.TypeId, b.PropertyId, out var own)
                    && (own.Source == KnowledgeSources.Experienced || (own.Value == b.Belief.Value && own.Confidence >= b.Belief.Confidence * trust))))
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
