using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>Source of a belief (vocabulary <c>knowledge_source</c>, B4a R3).</summary>
public static class KnowledgeSources
{
    /// <summary>Tried out oneself.</summary>
    public const string Experienced = "experienced";

    /// <summary>Heard from another inhabitant.</summary>
    public const string Heard = "heard";

    /// <summary>A guess of the AI.</summary>
    public const string Guessed = "guessed";

    /// <summary>Read (writing, B9).</summary>
    public const string Read = "read";

    /// <summary>Seen by looking closely: only what can be seen, never the effect (TL #19921).</summary>
    public const string Seen = "seen";
}

/// <summary>
/// What an inhabitant believes about one property of a type: the value, how sure it is, where the belief
/// comes from (B4a R3) and when it was formed. <paramref name="ToldBy"/> names the teller of heard knowledge.
/// </summary>
public readonly record struct Belief(bool Value, double Confidence, string Source = KnowledgeSources.Experienced, string? ToldBy = null, long Tick = 0);

/// <summary>
/// An inhabitant's beliefs about object types, keyed by (type, property), sorted for deterministic
/// iteration and hashing. Beliefs may be wrong; only the <see cref="ObjectCatalog"/> holds the truth.
/// Learning and sharing write here (B1 R6, B4a).
/// </summary>
public sealed class BeliefSet
{
    private readonly SortedDictionary<(string TypeId, string PropertyId), Belief> _beliefs = new(KeyComparer.Instance);

    // Indexes, not state (they follow from _beliefs; beliefs are set, never taken away): every belief by its
    // key for the look-up, and the properties believed about each type in ordinal order. The routine asks
    // about single beliefs and about "everything on this type" for every kind of thing in sight.
    private readonly Dictionary<(string TypeId, string PropertyId), Belief> _byKey = [];
    private readonly Dictionary<string, List<string>> _properties = new(StringComparer.Ordinal);

    /// <summary>Number of beliefs.</summary>
    public int Count => _beliefs.Count;

    /// <summary>Whether there is any belief about a type (the same answer as <see cref="About"/> not being empty).</summary>
    public bool Knows(string typeId) => _properties.ContainsKey(typeId);

    /// <summary>The highest confidence of the beliefs about a type (any property, any value); 0 without any.</summary>
    public double Surest(string typeId)
    {
        var surest = 0.0;
        if (_properties.TryGetValue(typeId, out var properties))
        {
            foreach (var property in properties)
            {
                surest = Math.Max(surest, _byKey[(typeId, property)].Confidence);
            }
        }

        return surest;
    }

    /// <summary>Sets a belief with its source (confidence is clamped to 0..1).</summary>
    public void Set(string typeId, string propertyId, bool value, double confidence, string source = KnowledgeSources.Experienced, string? toldBy = null, long tick = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        ArgumentException.ThrowIfNullOrEmpty(propertyId);
        ArgumentException.ThrowIfNullOrEmpty(source);
        var belief = new Belief(value, Math.Clamp(confidence, 0.0, 1.0), source, toldBy, tick);
        _beliefs[(typeId, propertyId)] = belief;
        if (_byKey.TryAdd((typeId, propertyId), belief))
        {
            if (!_properties.TryGetValue(typeId, out var properties))
            {
                _properties[typeId] = properties = [];
            }

            var at = properties.Count;
            while (at > 0 && string.CompareOrdinal(properties[at - 1], propertyId) > 0)
            {
                at--;
            }

            properties.Insert(at, propertyId);
        }
        else
        {
            _byKey[(typeId, propertyId)] = belief;
        }
    }

    /// <summary>Looks up a belief.</summary>
    public bool TryGet(string typeId, string propertyId, out Belief belief) => _byKey.TryGetValue((typeId, propertyId), out belief);

    /// <summary>All beliefs in (type, property) order.</summary>
    public IEnumerable<(string TypeId, string PropertyId, Belief Belief)> All =>
        _beliefs.Select(b => (b.Key.TypeId, b.Key.PropertyId, b.Value));

    /// <summary>The beliefs about one type, in property order.</summary>
    public IEnumerable<(string PropertyId, Belief Belief)> About(string typeId) =>
        _properties.TryGetValue(typeId, out var properties) ? properties.Select(p => (p, _byKey[(typeId, p)])) : [];

    /// <summary>Writes the beliefs in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_beliefs.Count);
        foreach (var ((typeId, propertyId), belief) in _beliefs)
        {
            hasher.Write(typeId).Write(propertyId).Write(belief.Value).Write(belief.Confidence)
                .Write(belief.Source).Write(belief.ToldBy ?? string.Empty).Write(belief.Tick);
        }
    }

    private sealed class KeyComparer : IComparer<(string TypeId, string PropertyId)>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare((string TypeId, string PropertyId) x, (string TypeId, string PropertyId) y)
        {
            var byType = string.CompareOrdinal(x.TypeId, y.TypeId);
            return byType != 0 ? byType : string.CompareOrdinal(x.PropertyId, y.PropertyId);
        }
    }
}

/// <summary>An inhabitant's feelings towards another (B4a R7), each 0..1.</summary>
public readonly record struct Relation(double Affection, double Trust, double Respect);

/// <summary>Relationships of one inhabitant to others, keyed by inhabitant id, sorted (B4a R7).</summary>
public sealed class RelationshipSet
{
    private readonly SortedDictionary<string, Relation> _relations = new(StringComparer.Ordinal);

    /// <summary>Number of known others.</summary>
    public int Count => _relations.Count;

    /// <summary>All relationships in id order.</summary>
    public IEnumerable<(string Other, Relation Relation)> All => _relations.Select(r => (r.Key, r.Value));

    /// <summary>The relationship to someone, or the start values if they have not met yet (not stored).</summary>
    public Relation Get(string other, Relation start) => _relations.TryGetValue(other, out var r) ? r : start;

    /// <summary>Sets the relationship to someone (values clamped to 0..1).</summary>
    public void Set(string other, Relation relation)
    {
        ArgumentException.ThrowIfNullOrEmpty(other);
        _relations[other] = new Relation(
            Math.Clamp(relation.Affection, 0.0, 1.0),
            Math.Clamp(relation.Trust, 0.0, 1.0),
            Math.Clamp(relation.Respect, 0.0, 1.0));
    }

    /// <summary>Forgets the relationship to someone (true if there was one).</summary>
    public bool Remove(string other) => _relations.Remove(other);

    /// <summary>Writes the relationships in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_relations.Count);
        foreach (var (other, r) in _relations)
        {
            hasher.Write(other).Write(r.Affection).Write(r.Trust).Write(r.Respect);
        }
    }
}
