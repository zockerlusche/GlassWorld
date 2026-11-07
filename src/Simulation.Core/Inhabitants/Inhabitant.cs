using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>What an inhabitant is doing right now.</summary>
/// <param name="Action">Action id from the vocabulary.</param>
/// <param name="TargetId">Target object id, or null.</param>
/// <param name="Destination">Where the inhabitant walks before the action starts.</param>
/// <param name="RemainingTicks">Ticks left once the action has started; null while still walking.</param>
public sealed record Activity(string Action, string? TargetId, Position Destination, long? RemainingTicks);

/// <summary>
/// An inhabitant's state. Needs, moods and traits are registers keyed by vocabulary id (not enums), so
/// data and later inventions can add new ones without code changes (B4b R11).
/// </summary>
public sealed class Inhabitant
{
    /// <summary>Creates an inhabitant.</summary>
    public Inhabitant(string id, Position position, IDictionary<string, double> needs, IDictionary<string, double> moods, IDictionary<string, double> traits, double willToLive)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
        Position = position;
        Needs = new SortedDictionary<string, double>(needs, StringComparer.Ordinal);
        Moods = new SortedDictionary<string, double>(moods, StringComparer.Ordinal);
        Traits = new SortedDictionary<string, double>(traits, StringComparer.Ordinal);
        WillToLive = willToLive;
    }

    /// <summary>Entity id, e.g. <c>inh-000001</c>.</summary>
    public string Id { get; }

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

    /// <summary>Current activity, or null when the routine has to choose.</summary>
    public Activity? Activity { get; internal set; }

    /// <summary>Writes the complete state in canonical order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Id).Write(Position.X).Write(Position.Z).Write(WillToLive).Write(NeglectTicks);
        WriteRegister(hasher, Needs);
        WriteRegister(hasher, Moods);
        WriteRegister(hasher, Traits);
        Beliefs.WriteState(hasher);
        hasher.Write(Activity is not null);
        if (Activity is { } a)
        {
            hasher.Write(a.Action).Write(a.TargetId ?? string.Empty).Write(a.Destination.X).Write(a.Destination.Z).Write(a.RemainingTicks ?? -1);
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
