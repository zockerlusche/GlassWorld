using System.Collections.Frozen;
using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Objects;

/// <summary>
/// The truth about object types: which properties each type really has (content/objects/*.json).
/// Inhabitants never read this directly; they only hold beliefs (<see cref="BeliefSet"/>).
/// </summary>
public sealed class ObjectCatalog : ITypeTruth
{
    private static readonly ObjectParams NoParams = new();

    private readonly FrozenDictionary<string, FrozenSet<string>> _properties;
    private readonly FrozenDictionary<string, ObjectParams> _params;

    private ObjectCatalog(FrozenDictionary<string, FrozenSet<string>> properties, FrozenDictionary<string, ObjectParams> parameters, FrozenSet<string> vocabulary)
    {
        _properties = properties;
        _params = parameters;
        PropertyVocabulary = vocabulary;
    }

    /// <summary>All known type ids (ordinal order).</summary>
    public IEnumerable<string> TypeIds => _properties.Keys.Order(StringComparer.Ordinal);

    /// <summary>All property ids of the vocabulary.</summary>
    public FrozenSet<string> PropertyVocabulary { get; }

    /// <summary>
    /// Builds the catalog from type files and the property vocabulary. Every property must exist in the
    /// vocabulary and every type id must be unique (cross-file checks the schema cannot express).
    /// </summary>
    public static ObjectCatalog Create(IEnumerable<ObjectTypes> files, Vocabulary propertyVocabulary)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(propertyVocabulary);
        if (propertyVocabulary.Kind != VocabularyKind.Property)
        {
            throw new InvalidDataException("Object catalog needs the property vocabulary.");
        }

        var vocabulary = propertyVocabulary.Entries.Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal);
        var types = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        var parameters = new Dictionary<string, ObjectParams>(StringComparer.Ordinal);
        foreach (var type in files.SelectMany(f => f.Types))
        {
            var unknown = type.Properties.Where(p => !vocabulary.Contains(p)).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidDataException($"Object type '{type.Id}' uses unknown properties: {string.Join(", ", unknown)}.");
            }

            if (!types.TryAdd(type.Id, type.Properties.ToFrozenSet(StringComparer.Ordinal)))
            {
                throw new InvalidDataException($"Object type '{type.Id}' is defined twice.");
            }

            if (type.Params is { } typeParams)
            {
                var problems = ParamProblems(typeParams);
                if (problems.Count > 0)
                {
                    throw new InvalidDataException($"Object type '{type.Id}' params: {string.Join("; ", problems)}.");
                }

                parameters[type.Id] = typeParams;
            }
        }

        return new ObjectCatalog(types.ToFrozenDictionary(StringComparer.Ordinal), parameters.ToFrozenDictionary(StringComparer.Ordinal), vocabulary);
    }

    /// <summary>
    /// The number parameters of a type (B5 R8); a type without any has all fields empty. Unknown types throw.
    /// </summary>
    public ObjectParams Params(string typeId) =>
        _properties.ContainsKey(typeId)
            ? _params.GetValueOrDefault(typeId, NoParams)
            : throw new KeyNotFoundException($"Unknown object type '{typeId}'.");

    /// <summary>
    /// Fields that only make sense together (the schema cannot express it): a shade needs its height and its
    /// radius, a heat its strength and its reach, a light its radius and its strength.
    /// </summary>
    private static List<string> ParamProblems(ObjectParams p)
    {
        var problems = new List<string>();
        Pair("shade_height_cm", p.ShadeHeightCm, "crown_radius_cm", p.CrownRadiusCm);
        Pair("heat_tenth_c", p.HeatTenthC, "heat_radius_cm", p.HeatRadiusCm);
        Pair("light_radius_cm", p.LightRadiusCm, "light_permille", p.LightPermille);
        return problems;

        void Pair(string a, int? valueA, string b, int? valueB)
        {
            if (valueA.HasValue != valueB.HasValue)
            {
                problems.Add($"{a} and {b} go together");
            }
        }
    }

    /// <summary>True if the type exists.</summary>
    public bool Contains(string typeId) => _properties.ContainsKey(typeId);

    /// <summary>The true value of a property for a type (unlisted = false).</summary>
    public bool HasProperty(string typeId, string propertyId)
    {
        if (!PropertyVocabulary.Contains(propertyId))
        {
            throw new ArgumentException($"Unknown property '{propertyId}'.", nameof(propertyId));
        }

        return TrueProperties(typeId).Contains(propertyId);
    }

    /// <summary>All true properties of a type.</summary>
    public FrozenSet<string> TrueProperties(string typeId) =>
        _properties.TryGetValue(typeId, out var properties)
            ? properties
            : throw new KeyNotFoundException($"Unknown object type '{typeId}'.");
}
