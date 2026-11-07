using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Content;

/// <summary>
/// All data files the core needs, loaded from a content folder. Every file goes through
/// <see cref="ContractSerializer"/> (schema check before use, ADR-0007); cross-file rules are checked
/// by the receiving types. Files are read in ordinal name order so the result never depends on the
/// file system's enumeration order.
/// </summary>
public sealed class ContentSet
{
    private ContentSet(WorldCalendar calendar, ObjectCatalog objects, CombinationRuleSet combinations, IReadOnlyDictionary<VocabularyKind, Vocabulary> vocabularies)
    {
        Calendar = calendar;
        Objects = objects;
        Combinations = combinations;
        Vocabularies = vocabularies;
    }

    /// <summary>World time.</summary>
    public WorldCalendar Calendar { get; }

    /// <summary>Object types and their truth.</summary>
    public ObjectCatalog Objects { get; }

    /// <summary>Combination rules.</summary>
    public CombinationRuleSet Combinations { get; }

    /// <summary>Vocabularies by kind.</summary>
    public IReadOnlyDictionary<VocabularyKind, Vocabulary> Vocabularies { get; }

    /// <summary>Loads and checks the content folder.</summary>
    public static ContentSet Load(string contentDirectory)
    {
        var vocabularies = Files(contentDirectory, "vocabulary")
            .Select(f => ContractSerializer.Deserialize<Vocabulary>(File.ReadAllText(f)))
            .ToDictionary(v => v.Kind);
        var calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(contentDirectory, "world", "time.json")));
        var objects = ObjectCatalog.Create(
            Files(contentDirectory, "objects").Select(f => ContractSerializer.Deserialize<ObjectTypes>(File.ReadAllText(f))),
            Required(vocabularies, VocabularyKind.Property));
        var combinations = CombinationRuleSet.Create(
            Files(contentDirectory, "rules").Select(f => ContractSerializer.Deserialize<CombinationRules>(File.ReadAllText(f))),
            objects,
            Required(vocabularies, VocabularyKind.Action));
        return new ContentSet(calendar, objects, combinations, vocabularies);
    }

    private static IEnumerable<string> Files(string contentDirectory, string folder) =>
        Directory.GetFiles(Path.Combine(contentDirectory, folder), "*.json").Order(StringComparer.Ordinal);

    private static Vocabulary Required(Dictionary<VocabularyKind, Vocabulary> vocabularies, VocabularyKind kind) =>
        vocabularies.TryGetValue(kind, out var vocabulary)
            ? vocabulary
            : throw new InvalidDataException($"Vocabulary '{kind}' is missing.");
}
