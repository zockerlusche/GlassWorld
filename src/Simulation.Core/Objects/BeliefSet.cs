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

    // All beliefs in (type, property) order as one list, laid out when it is asked for after a change.
    private (string TypeId, string PropertyId, Belief Belief)[]? _ordered;

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
        _ordered = null;
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

    /// <summary>
    /// All beliefs in (type, property) order, as <see cref="All"/> gives them, for a pass by index: whoever
    /// goes through everything an inhabitant believes again and again (what it could tell each of the
    /// others in sight) does not walk the sorted tree every time.
    /// </summary>
    internal IReadOnlyList<(string TypeId, string PropertyId, Belief Belief)> Ordered => _ordered ??= [.. All];

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
    // Two lists side by side in id order: with a hundred inhabitants everybody has a relationship to
    // everybody, and fading walks all of them in every tick.
    private readonly List<string> _others = [];
    private readonly List<Relation> _relations = [];

    /// <summary>Number of known others.</summary>
    public int Count => _others.Count;

    /// <summary>All relationships in id order.</summary>
    public IEnumerable<(string Other, Relation Relation)> All
    {
        get
        {
            for (var i = 0; i < _others.Count; i++)
            {
                yield return (_others[i], _relations[i]);
            }
        }
    }

    /// <summary>The relationship to someone, or the start values if they have not met yet (not stored).</summary>
    public Relation Get(string other, Relation start) => Place(other) is var at && at >= 0 ? _relations[at] : start;

    /// <summary>Sets the relationship to someone (values clamped to 0..1).</summary>
    public void Set(string other, Relation relation)
    {
        ArgumentException.ThrowIfNullOrEmpty(other);
        var clamped = Clamped(relation);
        var at = Place(other);
        if (at >= 0)
        {
            _relations[at] = clamped;
        }
        else
        {
            _others.Insert(~at, other);
            _relations.Insert(~at, clamped);
        }
    }

    /// <summary>Forgets the relationship to someone (true if there was one).</summary>
    public bool Remove(string other)
    {
        var at = Place(other);
        if (at < 0)
        {
            return false;
        }

        _others.RemoveAt(at);
        _relations.RemoveAt(at);
        return true;
    }

    /// <summary>
    /// Fading (B4a R7): every value that lies above its value in <paramref name="to"/> sinks by
    /// <paramref name="step"/>, never below it; only towards others for whom <paramref name="counts"/> says
    /// yes (asked only where something would change).
    /// </summary>
    public void Fade(Relation to, double step, Func<string, bool> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        for (var i = 0; i < _relations.Count; i++)
        {
            var r = _relations[i];
            var faded = new Relation(Sink(r.Affection, to.Affection, step), Sink(r.Trust, to.Trust, step), Sink(r.Respect, to.Respect, step));
            if (faded != r && counts(_others[i]))
            {
                _relations[i] = Clamped(faded);
            }
        }
    }

    /// <summary>Writes the relationships in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_others.Count);
        for (var i = 0; i < _others.Count; i++)
        {
            var r = _relations[i];
            hasher.Write(_others[i]).Write(r.Affection).Write(r.Trust).Write(r.Respect);
        }
    }

    private static double Sink(double value, double to, double step) => value > to ? Math.Max(to, value - step) : value;

    private static Relation Clamped(Relation relation) => new(
        Math.Clamp(relation.Affection, 0.0, 1.0),
        Math.Clamp(relation.Trust, 0.0, 1.0),
        Math.Clamp(relation.Respect, 0.0, 1.0));

    /// <summary>Index of someone in the lists, or the complement of where they belong.</summary>
    private int Place(string other)
    {
        var (low, high) = (0, _others.Count - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var order = string.CompareOrdinal(_others[mid], other);
            if (order == 0)
            {
                return mid;
            }

            (low, high) = order < 0 ? (mid + 1, high) : (low, mid - 1);
        }

        return ~low;
    }
}
