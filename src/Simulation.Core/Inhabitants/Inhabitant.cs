using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>What an inhabitant is doing right now.</summary>
/// <param name="Action">Action id from the vocabulary.</param>
/// <param name="TargetId">Target object id, or null.</param>
/// <param name="Destination">Where the inhabitant walks before the action starts.</param>
/// <param name="RemainingTicks">Ticks left once the action has started; null while still walking.</param>
/// <param name="Route">Waypoints still to walk on a map (B2 R10), searched once when the walk starts; null without a map.</param>
public sealed record Activity(string Action, string? TargetId, Position Destination, long? RemainingTicks, IReadOnlyList<Position>? Route = null);

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

    /// <summary>How the inhabitant looks (B2 R27); null only in worlds built without look data (tests).</summary>
    public Appearance? Appearance { get; set; }

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
