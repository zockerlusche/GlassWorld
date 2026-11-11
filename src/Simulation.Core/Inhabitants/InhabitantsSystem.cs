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
    /// <summary>
    /// Text for the player (thought bubble), already in the player's language; for no_answer the text key of the
    /// calm line (the game translates it); null for most events.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Language-neutral detail of the event (e.g. the need of a call for help, the type of a gift).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The object type of the thing an action is aimed at (set for started and finished actions; the thing
    /// itself may be used up by then); null for other events and for actions without a thing.
    /// </summary>
    public string? TargetType { get; init; }

    /// <summary>
    /// What came of a finished action (<see cref="ActionOutcomes"/>), by the measure of the repetition guard
    /// (W1696); null for every other event.
    /// </summary>
    public string? Outcome { get; init; }
}

/// <summary>
/// What the core knows came of a finished action - the two parts of the one measure the repetition guard
/// uses (W1696): the action's own effect, and what was learned through it. Without both it was "without
/// effect", which is what the guard counts.
/// </summary>
public static class ActionOutcomes
{
    /// <summary>The action had its effect: a need, a mood or the will to live changed (by at least the data's min_effect), or the thing was used up.</summary>
    public const string Done = "done";

    /// <summary>The action changed nothing itself, but something was learned through it (knowledge gained, a name learned).</summary>
    public const string FoundOut = "found_out";

    /// <summary>Neither: without any effect (what the repetition guard counts).</summary>
    public const string Nothing = "nothing";

    /// <summary>The outcome for the two parts of the measure.</summary>
    public static string Of(bool effect, bool learned) => effect ? Done : learned ? FoundOut : Nothing;

    /// <summary>Whether an outcome is one the repetition guard counts as without effect.</summary>
    public static bool WithoutEffect(string outcome) => string.Equals(outcome, Nothing, StringComparison.Ordinal);

    /// <summary>All outcomes.</summary>
    public static IReadOnlyList<string> All { get; } = [Done, FoundOut, Nothing];
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
public sealed partial class InhabitantsSystem : IWorldSystem
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

        // What the event does to the inhabitant it is about (data: event_effects, W1744), before anyone hears of it.
        if (_inhabitants.TryGetValue(worldEvent.InhabitantId, out var about))
        {
            foreach (var effect in _actions.EventEffects(worldEvent.Kind, worldEvent.Detail))
            {
                Apply(about, effect, effect.Delta);
            }
        }

        Reported?.Invoke(worldEvent);
    }

    /// <summary>
    /// Creates the start inhabitants from the world seed (B2503): traits and start needs are drawn from
    /// the data ranges with one stream per inhabitant, so the same seed always gives the same people.
    /// </summary>
    /// <param name="world">The world (seed and streams).</param>
    /// <param name="positions">Where they stand, in the order of the starters.</param>
    /// <param name="count">
    /// How many inhabitants the world starts with; null = those the data lists. See <see cref="StartersFor"/>.
    /// </param>
    public void SpawnStartInhabitants(World world, IReadOnlyList<Position> positions, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(positions);
        var starters = StartersFor(_model.Start, count);
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

        MeetStartGroups();
        GiveNames(world.Seed);
    }

    /// <summary>
    /// Who starts a world with <paramref name="count"/> inhabitants (null = those the data lists): the data's
    /// start inhabitants in their order; when more are asked for than the data lists, the list repeats, each
    /// repeat as groups of its own (the data's pairs 1 and 2 are followed by the pairs 3 and 4, and so on).
    /// So a bigger start population (measurements, B2 acceptance with 100 inhabitants) is made of the same
    /// start inhabitants as every world: same data ranges, one random stream each, same start relationships.
    /// </summary>
    public static IReadOnlyList<StartInhabitant> StartersFor(StartModel start, int? count)
    {
        ArgumentNullException.ThrowIfNull(start);
        var listed = start.Inhabitants.ToList();
        if (count is not { } wanted)
        {
            return listed;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(wanted, nameof(count));
        if (wanted > listed.Count && listed.Count == 0)
        {
            throw new InvalidDataException("The inhabitant model lists no start inhabitants to repeat.");
        }

        var groups = listed.Count == 0 ? 0 : listed.Max(s => s.Group);
        var starters = new List<StartInhabitant>(wanted);
        for (var n = 0; n < wanted; n++)
        {
            var like = listed[n % listed.Count];
            starters.Add(n < listed.Count ? like : new StartInhabitant { Sex = like.Sex, Group = like.Group + (n / listed.Count * groups) });
        }

        return starters;
    }

    /// <summary>Own names to pick from (content/inhabitants/names.json, W1698); empty = nobody gets a name.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>
    /// Gives every inhabitant without a name one from <see cref="Names"/> (W1698): in id order, drawn from the
    /// world seed with an own stream (not kept among the world's streams), distinct while the list allows.
    /// Also used when a save game from before names is loaded.
    /// </summary>
    public void GiveNames(ulong seed)
    {
        if (Names.Count == 0)
        {
            return;
        }

        var rng = RandomStreams.Create(seed, "inhabitant/names");
        var pool = Names.Where(n => !_inhabitants.Values.Any(i => i.Name == n)).ToList();
        foreach (var inhabitant in _inhabitants.Values.Where(i => i.Name is null))
        {
            if (pool.Count == 0)
            {
                pool.AddRange(Names);
            }

            var index = rng.NextInt(0, pool.Count);
            inhabitant.Name = pool[index];
            pool.RemoveAt(index);
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

    /// <summary>
    /// How far the inhabitants see right now (B2594: the weather lowers it; B5 adds the light); full sight
    /// when the host sets none. Every system that asks what an inhabitant perceives reads it here.
    /// </summary>
    public Sight Sight { get; init; } = Sight.Clear;

    /// <summary>
    /// The perception radius right now in metres: the data's <c>perception_radius_m</c> as far as
    /// <see cref="Sight"/> lets the inhabitants see.
    /// </summary>
    public double SightRadiusM => Sight.Metres(_brain.Weights.PerceptionRadiusM);

    /// <summary>
    /// What an inhabitant can aim at right now (B2594), the one answer for the routine's goals and for the
    /// check whether it sees anything that helps: every thing it sees (within <see cref="SightRadiusM"/>)
    /// and, while the sight is shorter than the radius of the data, every thing it remembers (place memory,
    /// #142) within that usual radius. So the weather hides what the inhabitant does not know, and nobody
    /// goes thirsty in the fog next to the water it knows. Seen things first, in the order of the objects,
    /// then remembered ones in the order of their ids; whether a thing can be walked to is the caller's question.
    /// <para>
    /// A remembered place counts only while its thing still stands there. That is a small piece of the
    /// world's truth in the routine: in the fog the inhabitant cannot see that the thing is gone. It is
    /// chosen on purpose, so nobody walks to nothing; B5 "last seen" (D634 R35) must pick this up and let
    /// the inhabitant find out for itself.
    /// </para>
    /// </summary>
    public List<WorldObject> InAim(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var radius = _brain.Weights.PerceptionRadiusM;
        var sight = Sight.Metres(radius);
        var aim = _objects.All.Where(o => DistanceSquared(inhabitant.Position, o.Position) <= sight * sight).ToList();
        if (sight < radius)
        {
            foreach (var (objectId, place) in inhabitant.Places)
            {
                var distance = DistanceSquared(inhabitant.Position, place.Position);
                if (distance > sight * sight && distance <= radius * radius
                    && _objects.TryGet(objectId, out var known) && known.TypeId == place.TypeId && known.Position == place.Position)
                {
                    aim.Add(known);
                }
            }
        }

        return aim;
    }

    /// <summary>
    /// Where the land an inhabitant has not seen yet begins (B2-6 R14, set by the host on worlds with an
    /// island); null from it = nothing left to explore on foot. Without it no action happens at the frontier.
    /// </summary>
    public Func<Inhabitant, Position?>? FrontierOf { get; set; }

    /// <summary>Whether an action without a target happens at the frontier (destination <c>frontier</c> of the data).</summary>
    public bool AtFrontier(string action) => _actions.Timing(action).Frontier;

    /// <summary>Knowledge with its source and trust (B4a R3/R4/R7); without it learning works as in B1.</summary>
    public KnowledgeSettings? Knowledge { get; init; }

    /// <summary>Event kinds from the vocabulary; reporting another kind is a data error (empty = no check).</summary>
    public IReadOnlySet<string> EventKinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether an inhabitant has an open question to the AI about a type (W1595); set by the decisions
    /// system. While it is open the routine leaves the listed actions on that type alone.
    /// </summary>
    public Func<string, string, bool>? QuestionOpen { get; set; }

    /// <summary>
    /// The inhabitants' name of an invented type as far as one inhabitant knows it (set by the host once the
    /// invention system exists); null from it = it knows no name for that type.
    /// </summary>
    public Func<Inhabitant, string, LocalizedName?>? InventionNameKnownTo { get; set; }

    /// <summary>
    /// What an inhabitant calls the subject of a remembered event, by its own knowledge only (GW2 #227):
    /// another inhabitant's own name once it has learned it (W1698), the name of an invention it knows;
    /// null when it knows none and for things the data name. Asked when a request or job is built, so a
    /// name learned after the event is there; never a truth the inhabitant does not know.
    /// </summary>
    public LocalizedName? LabelOf(Inhabitant inhabitant, MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.AboutId is not { } id)
        {
            return null;
        }

        if (entry.AboutKind == AboutInhabitant)
        {
            return inhabitant.KnownNames.Contains(id) && _inhabitants.TryGetValue(id, out var other) && other.Name is { } name
                ? new LocalizedName { De = name, En = name }
                : null;
        }

        return entry.AboutKind == AboutType ? InventionNameKnownTo?.Invoke(inhabitant, id) : null;
    }

    /// <summary>Event: an inhabitant now knows or believes something new (B4a R3).</summary>
    public const string KnowledgeGained = "knowledge_gained";

    /// <summary>Event: an inhabitant now knows another's own name (W1698); target = whose name.</summary>
    public const string NameLearned = "name_learned";

    /// <summary>Detail of a cancelled action whose destination cannot be reached on foot.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Detail of a cancelled action whose route search ran out of its budget.</summary>
    public const string NoRouteInBudget = "no_route_in_budget";

    /// <summary>
    /// Detail of a cancelled action towards an inhabitant who is asleep (B2466: nobody addresses a sleeper).
    /// Until D715 (W1743, A1509) the rule is the same for every action aimed at an inhabitant, a warning
    /// included: which actions count as addressing somebody, and whether one of them (a warning) may wake
    /// the other, becomes data with the per-action flag decided there.
    /// </summary>
    public const string ListenerAsleep = "listener_asleep";

    /// <summary>Detail of a cancelled action towards an inhabitant who was no longer where the actor last saw them.</summary>
    public const string ListenerOutOfSight = "listener_out_of_sight";

    /// <summary>
    /// Detail of a cancelled walk whose next step would end on ground nobody can walk on although the way
    /// was just searched anew (a safety net: such a walk would otherwise search again every tick).
    /// </summary>
    public const string NoWalkableStep = "no_walkable_step";

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

    /// <summary>Step part (<see cref="IStepProbe"/>): being together, first meetings and bonds of the tick.</summary>
    public const string PartMeeting = "inhabitants/meeting";

    /// <summary>Step part: needs, the air, moods and the will to live of every inhabitant.</summary>
    public const string PartState = "inhabitants/state";

    /// <summary>Step part: what every inhabitant does (without the two parts inside it).</summary>
    public const string PartAct = "inhabitants/act";

    /// <summary>Step part: the routine choosing the next action (inside <see cref="PartAct"/>).</summary>
    public const string PartChoose = "inhabitants/choose";

    /// <summary>Step part: walking, with the route searches (inside <see cref="PartAct"/>).</summary>
    public const string PartWalk = "inhabitants/walk";

    /// <summary>Step part: keeping clear of each other after a move (personal space).</summary>
    public const string PartSpacing = "inhabitants/spacing";

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var probe = world.Probe;
        probe?.Enter(PartMeeting);
        _tickStart.Clear();
        foreach (var inhabitant in _inhabitants.Values)
        {
            _tickStart[inhabitant.Id] = inhabitant.Position;
        }

        var together = Together();
        Meet(world);
        Bond();
        probe?.Leave(PartMeeting);
        foreach (var inhabitant in _inhabitants.Values)
        {
            probe?.Enter(PartState);
            FadeImprints(world, inhabitant);
            DecayNeeds(inhabitant);
            FeelTheAir(world, inhabitant);
            if (together.Contains(inhabitant.Id))
            {
                FillTogether(inhabitant, _model.Togetherness!);
            }

            UpdateMoods(inhabitant);
            UpdateWillToLive(inhabitant);
            probe?.Leave(PartState);
            var before = inhabitant.Position;
            var planned = inhabitant.Activity;
            probe?.Enter(PartAct);
            Act(world, inhabitant);
            probe?.Leave(PartAct);
            if (inhabitant.Position != before)
            {
                probe?.Enter(PartSpacing);
                var walked = inhabitant.Position;
                KeepClearOnTheWay(inhabitant, before);
                if (inhabitant.Position != walked && inhabitant.Activity is { Route: not null } activity
                    && planned is not null && planned.Action == activity.Action && planned.TargetId == activity.TargetId)
                {
                    // Shortened on a route: the waypoints it did not reach stay ahead (no corner cut over blocked cells).
                    inhabitant.Activity = activity with { Route = planned.Route };
                }

                Separate(inhabitant);
                probe?.Leave(PartSpacing);
            }
        }
    }

    /// <summary>
    /// W1695 (walking through each other): the game shows a move as a straight walk over the tick, both
    /// inhabitants at once. A move that would come closer than the minimum separation to another on the way
    /// (the other standing, or walking its own straight move of this tick) is shortened to the longest part
    /// that stays clear; if hardly anything is left, the walker steps aside (right, else left) when that is
    /// clear. Where there is no room aside (a path one cell wide, W1726 seed 2), the walker gives way to a
    /// walking inhabitant with the lower id that blocks it: it steps back its move, so the other walks on
    /// and both can pass where the way is wider; otherwise it waits. Pairs already closer at the start of
    /// the tick are left to the separation. Deterministic: fixed bisection, id order, no random draw.
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

        // No room aside: give way to a walker with the lower id in the way (never to one standing still,
        // which would only send it back and forth).
        var back = new Position(from.X - dx, from.Z - dz);
        if (_inhabitants.Values.Any(other => !ReferenceEquals(other, mover) && string.CompareOrdinal(other.Id, mover.Id) < 0 && Walking(other) && !Clear(mover, other, from, to))
            && Walkable(from, back) && PathClear(mover, from, back))
        {
            mover.Position = back;
            return;
        }

        mover.Position = part;
    }

    /// <summary>Whether the inhabitant is on its way somewhere (an action without a running duration and a destination elsewhere).</summary>
    private static bool Walking(Inhabitant inhabitant) =>
        inhabitant.Activity is { RemainingTicks: null } activity && Distance(inhabitant.Position, activity.Destination) > 1e-9;

    /// <summary>
    /// Whether a straight move of <paramref name="mover"/> from <paramref name="from"/> to <paramref name="to"/>
    /// over this tick stays at least the minimum separation from every other inhabitant moving straight from
    /// where it began the tick to where it is now (pairs closer already at the start do not count).
    /// </summary>
    private bool PathClear(Inhabitant mover, Position from, Position to) =>
        _inhabitants.Values.All(other => ReferenceEquals(other, mover) || Clear(mover, other, from, to));

    /// <summary>Whether the straight move of <paramref name="mover"/> stays clear of <paramref name="other"/> (see <see cref="PathClear"/>).</summary>
    private bool Clear(Inhabitant mover, Inhabitant other, Position from, Position to)
    {
        var limit = Space!.MinSeparationM - 1e-9;
        var start = _tickStart.GetValueOrDefault(other.Id, other.Position);

        // Relative position p + t·v for t in [0, 1]; its closest approach to zero.
        var (px, pz) = (from.X - start.X, from.Z - start.Z);
        var (vx, vz) = ((to.X - from.X) - (other.Position.X - start.X), (to.Z - from.Z) - (other.Position.Z - start.Z));
        if ((px * px) + (pz * pz) < limit * limit)
        {
            return true;
        }

        var vv = (vx * vx) + (vz * vz);
        var t = vv < 1e-12 ? 0 : Math.Clamp(-((px * vx) + (pz * vz)) / vv, 0, 1);
        var (cx, cz) = (px + (vx * t), pz + (vz * t));
        return (cx * cx) + (cz * cz) >= limit * limit;
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

    /// <summary>
    /// Being together (TL #20190, data: togetherness in the inhabitant model): the inhabitants that, at the start
    /// of the tick, stand within the distance of another they are familiar with (own relationship at least the
    /// data's trust and affection; start values when they have not met), neither of them asleep. Only a need
    /// effect follows, no behaviour. Deterministic: id order, no random draw.
    /// </summary>
    private HashSet<string> Together()
    {
        var together = new HashSet<string>(StringComparer.Ordinal);
        if (_model.Togetherness is not { } data || Knowledge is not { } knowledge || (data.DistanceM ?? Space?.ConversationMaxM) is not { } reach)
        {
            return together;
        }

        var start = Start(knowledge);
        foreach (var inhabitant in _inhabitants.Values.Where(i => !Asleep(i, data)))
        {
            if (_inhabitants.Values.Any(other => other != inhabitant && !Asleep(other, data)
                && DistanceSquared(inhabitant.Position, other.Position) <= reach * reach
                && inhabitant.Relationships.Get(other.Id, start) is var relation
                && relation.Trust >= data.MinTrust && relation.Affection >= data.MinAffection))
            {
                together.Add(inhabitant.Id);
            }
        }

        return together;
    }

    /// <summary>
    /// Whether an inhabitant is asleep (the one definition, data: togetherness.sleep_action of the inhabitant
    /// model, once that action has started): nobody addresses a sleeper (B2466), so the rule checker cancels
    /// an action aimed at one, and the decider is neither offered such an action nor asked who a sleeper is.
    /// Always false without the togetherness data.
    /// </summary>
    public bool IsAsleep(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return _model.Togetherness is { } data && Asleep(inhabitant, data);
    }

    private static bool Asleep(Inhabitant inhabitant, Togetherness data) =>
        inhabitant.Activity is { RemainingTicks: not null } activity && string.Equals(activity.Action, data.SleepAction, StringComparison.Ordinal);

    /// <summary>Togetherness fills its need by the data's rate per hour, never above the cap (TL #20190).</summary>
    private void FillTogether(Inhabitant inhabitant, Togetherness data)
    {
        if (inhabitant.Needs.TryGetValue(data.Need, out var value) && value < data.Cap)
        {
            inhabitant.Needs[data.Need] = Math.Min(value + PerTick(data.PerHour), data.Cap);
        }
    }

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

        if (inhabitant.Activity is null)
        {
            world.Probe?.Enter(PartChoose);
            inhabitant.Activity = Choose(world, inhabitant);
            world.Probe?.Leave(PartChoose);
        }

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

        if (listener is not null && activity.RemainingTicks is null && IsAsleep(listener))
        {
            // B2466 (calm world): nobody addresses a sleeper. Whoever is on the way to one gives it up. The
            // decider is not offered sleepers; this is the net for someone who falls asleep in between.
            Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = ListenerAsleep });
            inhabitant.Activity = null;
            return;
        }

        // The actor knows where the other is only while it sees them (W1744: beliefs, never the truth);
        // out of sight it walks on to where it saw them last.
        var seen = listener is null || Sees(inhabitant, listener);
        if (listener is not null && activity.RemainingTicks is null && seen && Following(activity, listener))
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
            world.Probe?.Enter(PartWalk);
            var there = MoveTowards(world, inhabitant, destination, ReachOf(activity.Action, listener));
            world.Probe?.Leave(PartWalk);
            if (!there)
            {
                if (inhabitant.Activity is not null)
                {
                    Yield(inhabitant, from, listener);
                }

                return;
            }

            if (listener is not null && !seen)
            {
                // Arrived where it last saw the other, who is not there any more: nothing to say to nobody.
                Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = ListenerOutOfSight });
                inhabitant.Activity = null;
                return;
            }

            if (listener is not null)
            {
                KeepConversationDistance(inhabitant, listener);
            }

            inhabitant.Activity = activity with { RemainingTicks = timing.DurationTicks, Route = null };
            Report(new WorldEvent(ActionStarted, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { TargetType = target?.TypeId });
            Imprint(world, inhabitant, activity, target, listener);
            return;
        }

        var remaining = activity.RemainingTicks.Value - 1;
        if (remaining > 0)
        {
            inhabitant.Activity = activity with { RemainingTicks = remaining };
            return;
        }

        var seq = LastEventSeq;
        var effect = Finish(world, inhabitant, activity, target);
        if (listener is not null)
        {
            Share(listener, activity.Action);
            Pass(world, inhabitant, listener, activity.Action);
            Introduce(world, inhabitant, listener, activity.Action);
            BondByAction(inhabitant, listener, activity.Action);
        }

        var learned = _history.Reverse().TakeWhile(e => e.Seq > seq).Any(e => e.Event.Kind is KnowledgeGained or NameLearned);

        // One measure for the repetition guard and for what the inhabitant remembers of the action.
        var outcome = ActionOutcomes.Of(effect, learned);
        var limit = CountRepeat(world, inhabitant, activity, changed: !ActionOutcomes.WithoutEffect(outcome));
        Report(new WorldEvent(ActionFinished, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = limit ? RepeatLimit : null, TargetType = target?.TypeId, Outcome = outcome });
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
        // The radius of the data stays the measure for walking (distance falloff, a random spot nearby);
        // what the inhabitant can aim at ends where the weather lets it see, known places aside (B2594, InAim).
        var radius = _brain.Weights.PerceptionRadiusM;
        // On a map only what can be walked to counts (B2 R10): things beyond deep water or cliffs are no goals.
        var perceived = InAim(inhabitant)
            .Where(o => Routes is null || Routes.Connected(inhabitant.Position, o.Position))
            .ToList();

        // Looking further away gives up after the data's hours, until the need is met or something that
        // seems as good is in sight again (review GW3 #118: no endless search for what is gone).
        var giveUpTicks = (long)(_brain.Weights.SearchGiveUpHours * _ticksPerHour);
        var givenUp = inhabitant.SearchSince.Where(s => world.Tick - s.Value >= giveUpTicks).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var searching = new HashSet<string>(StringComparer.Ordinal);
        var scored = _brain.Score(inhabitant, perceived, givenUp, searching, _brain.AimsAtInhabitants ? InSight(inhabitant) : null);
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

        // B2-6 R14: an action at the frontier is an option only while its score counts and land the
        // inhabitant has not seen can be reached on foot (never on worlds without a map). The frontier is
        // looked for only when such an action would be chosen, so the search is paid once per walk.
        Position? frontier = null;
        while (scored.Count > 0 && AtFrontier(scored[0].Action))
        {
            frontier = scored[0].Score > 0 ? FrontierOf?.Invoke(inhabitant) : null;
            if (frontier is not null)
            {
                break;
            }

            scored = [.. scored.Where(o => !AtFrontier(o.Action))];
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

        if (best.TargetId is not null && ResolveTarget(best.TargetId) is { } target)
        {
            // A thing, or another inhabitant where it is seen now (W1744); the walk follows them (Act).
            return new Activity(best.Action, best.TargetId, target.Position, null);
        }

        // Actions without a target happen here or, if the data says so, at a random spot nearby
        // (drawn from the inhabitant's own stream).
        var destination = inhabitant.Position;
        if (frontier is { } edge && AtFrontier(best.Action))
        {
            destination = edge;
        }
        else if (_actions.Timing(best.Action).RandomNearby)
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

    /// <summary>
    /// The other inhabitants the routine may aim at (W1744): those the inhabitant sees right now
    /// (<see cref="SightRadiusM"/>) and, on a map, can walk to; in id order. Who of them is familiar and
    /// awake is the brain's question.
    /// </summary>
    private List<Inhabitant> InSight(Inhabitant inhabitant) =>
        [.. _inhabitants.Values.Where(other => !ReferenceEquals(other, inhabitant) && Sees(inhabitant, other)
            && (Routes is null || Routes.Connected(inhabitant.Position, other.Position)))];

    /// <summary>Whether an inhabitant sees another right now: within the sight of this tick (B2594).</summary>
    private bool Sees(Inhabitant inhabitant, Inhabitant other)
    {
        var sight = SightRadiusM;
        return DistanceSquared(inhabitant.Position, other.Position) <= sight * sight;
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
    /// <para>
    /// A walker that was pushed off its route (giving way, keeping apart) may have its next waypoint beyond
    /// ground nobody can walk on. A step that would end there is not taken: the way is searched anew from
    /// where the walker stands (a new route begins with a neighbouring cell, so its first step stays on
    /// walkable ground). Without this the walker stood on a blocked cell, from where nothing can be walked to.
    /// The search is bounded: one new route per step, and if the first step of a route just searched would
    /// leave the walkable ground as well, the walk is cancelled (<see cref="NoWalkableStep"/>). A new search
    /// from the same spot would find the same route, so trying again tick after tick could only loop.
    /// </para>
    /// </summary>
    private bool FollowRoute(World world, Inhabitant inhabitant, Activity activity, Map.IRouteFinder routes, Position destination, double reach)
    {
        if (Distance(inhabitant.Position, destination) <= reach)
        {
            return true;
        }

        var from = inhabitant.Position;
        var route = activity.Route;
        var kept = route is not null;
        if (route is null && (route = FindRoute(world, inhabitant, activity, routes, destination)) is null)
        {
            return false;
        }

        var budget = StepOf(inhabitant) * routes.SpeedFactor(from);
        var (position, ahead, arrived) = Step(inhabitant, from, route, budget, reach, routes);
        if (OnWalkableGround(routes, from) && !routes.Connected(from, position))
        {
            if (kept)
            {
                if ((route = FindRoute(world, inhabitant, activity, routes, destination)) is null)
                {
                    return false;
                }

                (position, ahead, arrived) = Step(inhabitant, from, route, budget, reach, routes);
            }

            if (!routes.Connected(from, position))
            {
                Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId) { Detail = NoWalkableStep });
                inhabitant.Activity = null;
                return false;
            }
        }

        inhabitant.Position = position;
        inhabitant.Activity = activity with { Route = ahead };
        return arrived || Distance(inhabitant.Position, destination) <= reach;
    }

    /// <summary>
    /// One tick's walk along a route: where it ends, the waypoints still ahead and whether it arrived.
    /// <para>
    /// A waypoint somebody stands on cannot be reached: the minimum separation keeps the walker off it, and
    /// heading for it again and again it circled the one standing there until they left (W1744, seed 42:
    /// seven hours around a sleeping partner on the way to food). So the walk leaves out a waypoint
    /// somebody stands on - never the last one, the destination - and passes beside, kept apart by the
    /// rules of personal space. If that step would leave the walkable ground, the route is walked as it is.
    /// </para>
    /// <para>
    /// Only the waypoints this step heads for are looked at (the next one and those it passes with its
    /// budget), not the whole route: the cost per walker and tick stays a handful of waypoints times the
    /// inhabitants, whatever the length of the route, and a place far ahead is judged when the walker gets
    /// there, not by who stands on it now.
    /// </para>
    /// <para>
    /// A waypoint left out is gone from the kept route, also when the one standing there leaves a tick
    /// later; the walker does not turn back to it. (Only a step that keeping clear shortened puts the route
    /// of before the step back, see <see cref="Advance"/>.) That is safe because a waypoint is nothing the
    /// walk has to touch: the waypoints still ahead are the same cells of the same route, and every step
    /// towards the next of them is checked where it lands - never on ground nobody can walk on, else the
    /// route as it is or a new search (<see cref="FollowRoute"/>).
    /// </para>
    /// </summary>
    private (Position Position, IReadOnlyList<Position> Ahead, bool Arrived) Step(Inhabitant walker, Position from, IReadOnlyList<Position> route, double budget, double reach, Map.IRouteFinder routes)
    {
        if (Space is { MinSeparationM: > 0 } space)
        {
            var limit = space.MinSeparationM - 1e-9;
            var leftOut = false;
            bool Taken(Position point)
            {
                foreach (var other in _inhabitants.Values)
                {
                    if (!ReferenceEquals(other, walker) && !Walking(other) && Distance(other.Position, point) < limit)
                    {
                        leftOut = true;
                        return true;
                    }
                }

                return false;
            }

            var beside = Along(from, route, budget, reach, Taken);
            if (!leftOut || routes.Connected(from, beside.Position))
            {
                return (beside.Position, [.. route.Skip(beside.Next)], beside.Arrived);
            }
        }

        var (position, next, arrived) = Along(from, route, budget, reach, null);
        return (position, [.. route.Skip(next)], arrived);
    }

    /// <summary>
    /// Whether a position lies on ground that can be walked on (a cell of a walkable area). Somebody who
    /// stands elsewhere (an older save) is let walk off it.
    /// </summary>
    private static bool OnWalkableGround(Map.IRouteFinder routes, Position position) => routes.Connected(position, position);

    /// <summary>Searches the route to the destination; without one the action is cancelled with the reason and null returned.</summary>
    private IReadOnlyList<Position>? FindRoute(World world, Inhabitant inhabitant, Activity activity, Map.IRouteFinder routes, Position destination)
    {
        var found = routes.Find(inhabitant.Position, destination);
        if (found.Status == Map.PathStatus.Found)
        {
            return found.Waypoints;
        }

        Report(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId)
        {
            Detail = found.Status == Map.PathStatus.Unreachable ? Unreachable : NoRouteInBudget,
        });
        inhabitant.Activity = null;
        return null;
    }

    /// <summary>
    /// One tick's walk along the waypoints with the given budget in metres: where it ends, how many waypoints
    /// lie behind it (reached, or left out) and whether the last one was reached (within the reach). A
    /// waypoint the walk is about to head for is left out when <paramref name="taken"/> says so (never the
    /// last one); waypoints beyond what the budget reaches are not asked about.
    /// </summary>
    private static (Position Position, int Next, bool Arrived) Along(Position position, IReadOnlyList<Position> route, double budget, double reach, Func<Position, bool>? taken)
    {
        var next = 0;
        var arrived = false;
        while (budget > 1e-9 && next < route.Count)
        {
            if (taken is not null && next < route.Count - 1 && taken(route[next]))
            {
                next++;
                continue;
            }

            var point = route[next];
            var last = next == route.Count - 1;
            var stop = last ? reach : 0.0;
            var distance = Distance(position, point);
            var travel = Math.Min(budget, Math.Max(0.0, distance - stop));
            if (distance > 0)
            {
                position = new Position(
                    position.X + ((point.X - position.X) * travel / distance),
                    position.Z + ((point.Z - position.Z) * travel / distance));
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

        return (position, next, arrived);
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
            changed |= Apply(inhabitant, effect, effect.Delta) >= least;
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
            else if (applying.Count > 0 && Knowledge?.Looking is { } memory)
            {
                // B2594: a thing the inhabitant has used and that is still there is a place it knows, like
                // one it has looked at: it finds its way back to it when the weather hides it.
                inhabitant.RememberPlace(target.Id, new PlaceMemory(target.TypeId, target.Position, world.Tick), memory.PlaceMemoryMax);
            }
        }

        return changed;
    }

    /// <summary>
    /// Applies one effect with the given delta (clamped to 0..1) and returns how much the value changed.
    /// </summary>
    private static double Apply(Inhabitant inhabitant, Effect effect, double delta)
    {
        switch (effect.Kind)
        {
            case EffectKind.Need when inhabitant.Needs.TryGetValue(effect.Id, out var need):
                inhabitant.Needs[effect.Id] = Math.Clamp(need + delta, 0.0, 1.0);
                return Math.Abs(inhabitant.Needs[effect.Id] - need);
            case EffectKind.Mood when inhabitant.Moods.TryGetValue(effect.Id, out var mood):
                inhabitant.Moods[effect.Id] = Math.Clamp(mood + delta, 0.0, 1.0);
                return Math.Abs(inhabitant.Moods[effect.Id] - mood);
            case EffectKind.Will_to_live:
                var will = inhabitant.WillToLive;
                inhabitant.WillToLive = Math.Clamp(will + delta, 0.0, 1.0);
                return Math.Abs(inhabitant.WillToLive - will);
            default:
                return 0;
        }
    }

    /// <summary>
    /// The other participant of an action aimed at an inhabitant gets the data's listener share of each effect
    /// of the action as well (TL #20190); effects without a share reach only the actor.
    /// </summary>
    private void Share(Inhabitant listener, string action)
    {
        foreach (var effect in _actions.Applying(action, null).SelectMany(r => r.Effects).Where(e => e.ListenerShare > 0))
        {
            Apply(listener, effect, effect.Delta * effect.ListenerShare!.Value);
        }
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
                BondByProof(inhabitant, teller, old.Value == truth);
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
    /// Learning names on a first meeting (W1698, data: knowledge.naming): the listener learns the asker's
    /// name; the asker learns the listener's unless the listener is busy with an urgent need (a need below
    /// busy_need_below). One name_learned event per learner.
    /// </summary>
    private void Introduce(World world, Inhabitant asker, Inhabitant listener, string action)
    {
        if (Knowledge?.Naming is not { } naming || !string.Equals(naming.Action, action, StringComparison.Ordinal))
        {
            return;
        }

        Learn(world, listener, asker, action);
        if (!listener.Needs.Values.Any(v => v < naming.BusyNeedBelow))
        {
            Learn(world, asker, listener, action);
        }
    }

    private void Learn(World world, Inhabitant learner, Inhabitant other, string action)
    {
        if (other.Name is not null && learner.KnownNames.Add(other.Id))
        {
            Report(new WorldEvent(NameLearned, world.Tick, learner.Id, action, other.Id));
        }
    }

    /// <summary>
    /// Whether an action aimed at another inhabitant still has something to bring (an option the decider may
    /// take): knowledge to pass on (B4a R4), for the naming action a name the asker does not know yet (W1698),
    /// for a meeting action (approach, talk to ...) the conditions of its data (B4a R8, <see cref="Offered"/>).
    /// </summary>
    public static bool HasPurpose(KnowledgeSettings knowledge, Inhabitant asker, Inhabitant other, string action)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(asker);
        ArgumentNullException.ThrowIfNull(other);
        if (knowledge.Meeting?.Options.FirstOrDefault(o => string.Equals(o.Action, action, StringComparison.Ordinal)) is { } meeting)
        {
            return Offered(knowledge, meeting, asker, other);
        }

        return knowledge.Naming is { } naming && string.Equals(naming.Action, action, StringComparison.Ordinal)
            ? other.Name is not null && !asker.KnownNames.Contains(other.Id)
            : PassableBelief(knowledge, asker, other, action) is not null;
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
