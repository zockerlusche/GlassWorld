using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>What an inhabitant is doing right now.</summary>
/// <param name="Action">Action id from the vocabulary.</param>
/// <param name="TargetId">Target object id, or null.</param>
/// <param name="Destination">Where the inhabitant walks before the action starts.</param>
/// <param name="RemainingTicks">Ticks left once the action has started; null while still walking.</param>
/// <param name="Route">Waypoints still to walk on a map (B2 R10), searched once when the walk starts; null without a map.</param>
/// <param name="DecidedTick">
/// The mark of a choice of the decider (A1487): the tick its answer was accepted or, for a choice that waited
/// behind another action, the tick its turn came (so its walk has the whole time limit of the decider data).
/// Null for everything the routine or another system chose. It stays with the activity while it waits, walks and runs.
/// </param>
public sealed record Activity(string Action, string? TargetId, Position Destination, long? RemainingTicks, IReadOnlyList<Position>? Route = null, long? DecidedTick = null);

/// <summary>
/// The same action done again and again at one spot without any effect (W1696): which action on which
/// target, where the run began and how many times in a row it brought nothing.
/// </summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Target id, or null.</param>
/// <param name="Place">Where the run began.</param>
/// <param name="Count">Times in a row without effect.</param>
public sealed record RepeatRun(string Action, string? TargetId, Position Place, int Count);

/// <summary>
/// An action that rests after repeats without effect (W1696), for one target: the target id, or for an
/// action without a target the place where it rested (it counts within the data's same_place_m).
/// </summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Target id, or null for an action without a target.</param>
/// <param name="Place">Where the run without effect ended.</param>
/// <param name="Until">First tick at which it may be chosen again.</param>
public sealed record RestingAction(string Action, string? TargetId, Position Place, long Until);

/// <summary>A remembered place (TL #19921): a thing of a type stood here when the inhabitant looked at it.</summary>
/// <param name="TypeId">Object type.</param>
/// <param name="Position">Where it stood.</param>
/// <param name="Tick">When the inhabitant saw it there.</param>
public readonly record struct PlaceMemory(string TypeId, Position Position, long Tick);

/// <summary>
/// What an imprinting counter counts (B4a R6): an action, what it was about (kind <c>type</c> or
/// <c>inhabitant</c> and its id; both empty for an action without a target) and the time of day it began.
/// </summary>
/// <param name="Action">Action id.</param>
/// <param name="AboutKind">Subject kind (<c>type</c>, <c>inhabitant</c>) or empty.</param>
/// <param name="AboutId">Subject id or empty.</param>
/// <param name="TimeOfDay">Time of day id (vocabulary time_of_day).</param>
public readonly record struct ImprintKey(string Action, string AboutKind, string AboutId, string TimeOfDay)
{
    /// <summary>Ordinal order by action, kind, id and time of day (saving, hashing, ties).</summary>
    public static IComparer<ImprintKey> Order { get; } = Comparer<ImprintKey>.Create((x, y) =>
    {
        var c = string.CompareOrdinal(x.Action, y.Action);
        c = c != 0 ? c : string.CompareOrdinal(x.AboutKind, y.AboutKind);
        c = c != 0 ? c : string.CompareOrdinal(x.AboutId, y.AboutId);
        return c != 0 ? c : string.CompareOrdinal(x.TimeOfDay, y.TimeOfDay);
    });
}

/// <summary>
/// An inhabitant's state. Needs, moods and traits are registers keyed by vocabulary id (not enums), so
/// data and later inventions can add new ones without code changes (B4b R11).
/// </summary>
public sealed class Inhabitant
{
    /// <summary>Creates an inhabitant.</summary>
    public Inhabitant(string id, string sex, int group, Position position, IDictionary<string, double> needs, IDictionary<string, double> moods, IDictionary<string, double> traits, double willToLive)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(sex);
        Id = id;
        Sex = sex;
        Group = group;
        Position = position;
        Needs = new SortedDictionary<string, double>(needs, StringComparer.Ordinal);
        Moods = new SortedDictionary<string, double>(moods, StringComparer.Ordinal);
        Traits = new SortedDictionary<string, double>(traits, StringComparer.Ordinal);
        WillToLive = willToLive;
    }

    /// <summary>Entity id, e.g. <c>inh-000001</c>.</summary>
    public string Id { get; }

    /// <summary>Sex (<c>female</c> or <c>male</c>, B2517); partnership rules in B9 (B2519).</summary>
    public string Sex { get; }

    /// <summary>Start group (members wake up near each other, F5249).</summary>
    public int Group { get; }

    /// <summary>Where the inhabitant is.</summary>
    public Position Position { get; internal set; }

    /// <summary>
    /// Where the inhabitant stood when the tick that is running began (set by the inhabitants system at the
    /// start of every tick, read within the tick by the rules of personal space). Not state: never saved or hashed.
    /// </summary>
    internal Position StartOfTick { get; set; }

    /// <summary>The inhabitant's place in the id order of its inhabitants system (kept by the system; not state).</summary>
    internal int Ordinal { get; set; }

    /// <summary>
    /// Whom of the others it has met, as a row over their places in the system's id order: worked out from
    /// <see cref="Met"/> by the system and valid for the versions in <see cref="MetRowFor"/> (not state).
    /// </summary>
    internal bool[] MetRow { get; set; } = [];

    /// <summary>The version of <see cref="Met"/> and of the system's population <see cref="MetRow"/> was worked out for.</summary>
    internal (int Met, int Population) MetRowFor { get; set; } = (-1, -1);

    /// <summary>Need levels 0 (urgent) … 1 (satisfied), by need id.</summary>
    public SortedDictionary<string, double> Needs { get; }

    /// <summary>Feelings 0 … 1, by mood id.</summary>
    public SortedDictionary<string, double> Moods { get; }

    /// <summary>Character traits 0 … 1, by trait id.</summary>
    public SortedDictionary<string, double> Traits { get; }

    /// <summary>Will to live 0 … 1.</summary>
    public double WillToLive { get; internal set; }

    /// <summary>Continuous ticks with at least one neglected need.</summary>
    public long NeglectTicks { get; internal set; }

    /// <summary>What the inhabitant believes about object types.</summary>
    public BeliefSet Beliefs { get; } = new();

    /// <summary>Affection, trust and respect towards others (B4a R7).</summary>
    public RelationshipSet Relationships { get; } = new();

    /// <summary>Current activity, or null when the routine has to choose.</summary>
    public Activity? Activity { get; internal set; }

    /// <summary>
    /// A decided activity that waits until the current one is finished (an answer of the decider never
    /// breaks off an action in progress, e.g. sleep).
    /// </summary>
    public Activity? Planned { get; internal set; }

    /// <summary>
    /// Since which tick the inhabitant has been looking further away, per search key (need); removed once the
    /// need is met or something helpful is in sight again. Searches give up after search_give_up_hours.
    /// </summary>
    public SortedDictionary<string, long> SearchSince { get; } = new(StringComparer.Ordinal);

    /// <summary>The current run of one action without effect at one spot (W1696), or null.</summary>
    public RepeatRun? Repeat { get; internal set; }

    /// <summary>
    /// Actions that rest after too many repeats without effect (W1696), each for its target or, without a
    /// target, for its place (content/behaviour/utility.json: repetition), in the order they began to rest.
    /// </summary>
    public List<RestingAction> Resting { get; } = [];

    /// <summary>
    /// Whether <paramref name="action"/> on <paramref name="targetId"/> rests at <paramref name="tick"/>; an action
    /// without a target rests only within <paramref name="samePlaceM"/> of where it began to rest
    /// (<paramref name="at"/>: where the inhabitant would do it). Resting with one target never blocks another.
    /// </summary>
    public bool Rests(string action, string? targetId, Position at, long tick, double samePlaceM)
    {
        foreach (var r in Resting)
        {
            if (r.Until > tick && r.Action == action && r.TargetId == targetId
                && (targetId is not null || Math.Sqrt(((r.Place.X - at.X) * (r.Place.X - at.X)) + ((r.Place.Z - at.Z) * (r.Place.Z - at.Z))) <= samePlaceM))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Remembered places by object id (TL #19921), at most the knowledge data's place_memory_max.</summary>
    public SortedDictionary<string, PlaceMemory> Places { get; } = new(StringComparer.Ordinal);

    /// <summary>Remembers where a thing stands; beyond <paramref name="max"/> places the oldest is forgotten (ties by id).</summary>
    public void RememberPlace(string objectId, PlaceMemory place, int max)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        if (max <= 0)
        {
            return;
        }

        Places[objectId] = place;
        while (Places.Count > max)
        {
            var oldest = Places.MinBy(p => (p.Value.Tick, p.Key), PlaceOrder.Instance).Key;
            Places.Remove(oldest);
        }
    }

    /// <summary>How the inhabitant looks (B2 R27); null only in worlds built without look data (tests).</summary>
    public Appearance? Appearance { get; set; }

    /// <summary>The inhabitant's own name (W1698, a ProperName from the data list); null in worlds without names.</summary>
    public string? Name { get; internal set; }

    /// <summary>Ids of the inhabitants whose own name this one knows (per observer, W1698).</summary>
    public IdSet KnownNames { get; } = [];

    /// <summary>
    /// Ids of the inhabitants this one has met (B4a R8): the own start group from the start, everybody else
    /// once it has seen them (first_meeting).
    /// </summary>
    public IdSet Met { get; } = [];

    /// <summary>
    /// Imprinting counters (B4a R6): repeated experiences, each counter grows by one per repetition and
    /// fades with time (data: knowledge imprinting). The core only counts; habits are read from here.
    /// </summary>
    public SortedDictionary<ImprintKey, double> Imprints { get; } = new(ImprintKey.Order);

    /// <summary>
    /// What the inhabitant remembers (B4a R5): its summary and its remembered events (data: knowledge memory).
    /// The requests to the AI carry it; the core itself decides nothing by it.
    /// </summary>
    public InhabitantMemory Memory { get; } = new();

    /// <summary>Writes the complete state in canonical order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Id).Write(Sex).Write(Group).Write(Position.X).Write(Position.Z).Write(WillToLive).Write(NeglectTicks);
        WriteRegister(hasher, Needs);
        WriteRegister(hasher, Moods);
        WriteRegister(hasher, Traits);
        Beliefs.WriteState(hasher);

        // Relationships only once there are any, so worlds without them keep their hashes.
        if (Relationships.Count > 0)
        {
            Relationships.WriteState(hasher);
        }
        WriteActivity(hasher, Activity);
        WriteActivity(hasher, Planned);
        hasher.Write(Appearance is not null);
        Appearance?.WriteState(hasher);

        // Searches only while there are any, so states without them keep their hashes.
        if (SearchSince.Count > 0)
        {
            hasher.Write("search").Write(SearchSince.Count);
            foreach (var (key, tick) in SearchSince)
            {
                hasher.Write(key).Write(tick);
            }
        }

        // Places only once there are any, so states without them keep their hashes.
        if (Places.Count > 0)
        {
            hasher.Write("places").Write(Places.Count);
            foreach (var (objectId, place) in Places)
            {
                hasher.Write(objectId).Write(place.TypeId).Write(place.Position.X).Write(place.Position.Z).Write(place.Tick);
            }
        }

        // Repeats without effect only while there are any, so states without them keep their hashes.
        if (Repeat is { } run)
        {
            hasher.Write("repeat").Write(run.Action).Write(run.TargetId ?? string.Empty).Write(run.Place.X).Write(run.Place.Z).Write(run.Count);
        }

        if (Resting.Count > 0)
        {
            hasher.Write("resting").Write(Resting.Count);
            foreach (var r in Resting)
            {
                hasher.Write(r.Action).Write(r.TargetId ?? string.Empty).Write(r.Place.X).Write(r.Place.Z).Write(r.Until);
            }
        }

        // Own name and known names only while there are any, so worlds without them keep their hashes.
        if (Name is not null)
        {
            hasher.Write("name").Write(Name);
        }

        if (KnownNames.Count > 0)
        {
            hasher.Write("known_names").Write(KnownNames.Count);
            foreach (var other in KnownNames)
            {
                hasher.Write(other);
            }
        }

        // Met inhabitants and imprinting counters only while there are any (B4a R6/R8).
        if (Met.Count > 0)
        {
            hasher.Write("met").Write(Met.Count);
            foreach (var other in Met)
            {
                hasher.Write(other);
            }
        }

        if (Imprints.Count > 0)
        {
            hasher.Write("imprints").Write(Imprints.Count);
            foreach (var (key, count) in Imprints)
            {
                hasher.Write(key.Action).Write(key.AboutKind).Write(key.AboutId).Write(key.TimeOfDay).Write(count);
            }
        }

        // The memory only once it holds anything (B4a R5), so worlds without memory data keep their hashes.
        if (Memory.HasState)
        {
            hasher.Write("memory");
            Memory.WriteState(hasher);
        }
    }

    private static void WriteActivity(StateHasher hasher, Activity? activity)
    {
        hasher.Write(activity is not null);
        if (activity is { } a)
        {
            hasher.Write(a.Action).Write(a.TargetId ?? string.Empty).Write(a.Destination.X).Write(a.Destination.Z).Write(a.RemainingTicks ?? -1);
            hasher.Write(a.Route?.Count ?? -1);
            foreach (var point in a.Route ?? [])
            {
                hasher.Write(point.X).Write(point.Z);
            }

            // The decider's mark only where there is one, so activities without it keep their hashes.
            if (a.DecidedTick is { } decided)
            {
                hasher.Write("decided").Write(decided);
            }
        }
    }

    private sealed class PlaceOrder : IComparer<(long Tick, string Id)>
    {
        public static readonly PlaceOrder Instance = new();

        public int Compare((long Tick, string Id) x, (long Tick, string Id) y)
        {
            var byTick = x.Tick.CompareTo(y.Tick);
            return byTick != 0 ? byTick : string.CompareOrdinal(x.Id, y.Id);
        }
    }

    private static void WriteRegister(StateHasher hasher, SortedDictionary<string, double> register)
    {
        hasher.Write(register.Count);
        foreach (var (id, value) in register)
        {
            hasher.Write(id).Write(value);
        }
    }
}
