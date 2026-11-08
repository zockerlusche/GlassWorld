using System.Collections.Frozen;

namespace GlassWorld.Simulation.Objects;

/// <summary>
/// The truth about object types as the rules see it: which types exist and which properties they truly
/// have. Implemented by the content catalog and by the per-world catalog that adds runtime types.
/// </summary>
public interface ITypeTruth
{
    /// <summary>All property ids of the vocabulary.</summary>
    FrozenSet<string> PropertyVocabulary { get; }

    /// <summary>True if the type exists.</summary>
    bool Contains(string typeId);

    /// <summary>The true value of a property for a type (unlisted = false).</summary>
    bool HasProperty(string typeId, string propertyId);

    /// <summary>All true properties of a type.</summary>
    FrozenSet<string> TrueProperties(string typeId);
}

/// <summary>
/// The type truth of one world: the content catalog plus runtime types that came into the world later
/// (inventions, B4b R5/R6). Runtime types are world state; they are registered once, never changed, and
/// kept in ordinal id order so hashing and saving are independent of registration timing.
/// </summary>
public sealed class WorldTypeCatalog : ITypeTruth
{
    private readonly ObjectCatalog _content;
    private readonly SortedDictionary<string, FrozenSet<string>> _runtime = new(StringComparer.Ordinal);

    /// <summary>Creates the world catalog on top of the content catalog.</summary>
    public WorldTypeCatalog(ObjectCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    /// <inheritdoc/>
    public FrozenSet<string> PropertyVocabulary => _content.PropertyVocabulary;

    /// <summary>Runtime types in ordinal id order with their true properties.</summary>
    public IEnumerable<KeyValuePair<string, FrozenSet<string>>> RuntimeTypes => _runtime;

    /// <inheritdoc/>
    public bool Contains(string typeId) => _content.Contains(typeId) || _runtime.ContainsKey(typeId);

    /// <inheritdoc/>
    public bool HasProperty(string typeId, string propertyId)
    {
        if (!PropertyVocabulary.Contains(propertyId))
        {
            throw new ArgumentException($"Unknown property '{propertyId}'.", nameof(propertyId));
        }

        return TrueProperties(typeId).Contains(propertyId);
    }

    /// <inheritdoc/>
    public FrozenSet<string> TrueProperties(string typeId) =>
        _runtime.TryGetValue(typeId, out var properties) ? properties : _content.TrueProperties(typeId);

    /// <summary>
    /// Adds a runtime type. Its id must be new and every property must be in the vocabulary (the world
    /// never learns a property the data do not know).
    /// </summary>
    public void Register(string typeId, IEnumerable<string> properties)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        ArgumentNullException.ThrowIfNull(properties);
        if (Contains(typeId))
        {
            throw new InvalidOperationException($"Object type '{typeId}' already exists.");
        }

        var set = properties.ToFrozenSet(StringComparer.Ordinal);
        var unknown = set.Where(p => !PropertyVocabulary.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Runtime type '{typeId}' uses unknown properties: {string.Join(", ", unknown)}.", nameof(properties));
        }

        _runtime.Add(typeId, set);
    }
}
