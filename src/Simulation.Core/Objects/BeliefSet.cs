using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>What an inhabitant believes about a property of an object type.</summary>
/// <param name="Value">Believed value.</param>
/// <param name="Confidence">Confidence 0..1.</param>
public readonly record struct Belief(bool Value, double Confidence);

/// <summary>
/// The beliefs of one inhabitant about object types ("the world knows the truth, the AI guesses").
/// Beliefs may be wrong; only the <see cref="ObjectCatalog"/> holds the truth. Learning and sharing
/// beliefs follow in B4a; B1 provides the structure that goes into decision requests (KnownProperty).
/// </summary>
public sealed class BeliefSet
{
    private readonly SortedDictionary<(string TypeId, string PropertyId), Belief> _beliefs = new(KeyComparer.Instance);

    /// <summary>Number of beliefs.</summary>
    public int Count => _beliefs.Count;

    /// <summary>Sets or replaces a belief; confidence is clamped to 0..1.</summary>
    public void Set(string typeId, string propertyId, bool value, double confidence)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        ArgumentException.ThrowIfNullOrEmpty(propertyId);
        _beliefs[(typeId, propertyId)] = new Belief(value, Math.Clamp(confidence, 0.0, 1.0));
    }

    /// <summary>The belief about a property, if any.</summary>
    public bool TryGet(string typeId, string propertyId, out Belief belief) => _beliefs.TryGetValue((typeId, propertyId), out belief);

    /// <summary>All beliefs about one type, in property order.</summary>
    public IEnumerable<(string PropertyId, Belief Belief)> About(string typeId) =>
        _beliefs.Where(b => b.Key.TypeId == typeId).Select(b => (b.Key.PropertyId, b.Value));

    /// <summary>Writes all beliefs in canonical order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_beliefs.Count);
        foreach (var ((typeId, propertyId), belief) in _beliefs)
        {
            hasher.Write(typeId).Write(propertyId).Write(belief.Value).Write(belief.Confidence);
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
