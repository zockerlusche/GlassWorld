using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Objects;

/// <summary>
/// Combination rules from content/rules/*.json. Inputs match by TRUE properties, never by fixed types,
/// so new objects and inventions take part without code changes (frame, not outcome).
/// </summary>
public sealed class CombinationRuleSet
{
    private readonly IReadOnlyList<CombinationRule> _rules;
    private readonly ObjectCatalog _catalog;

    private CombinationRuleSet(IReadOnlyList<CombinationRule> rules, ObjectCatalog catalog)
    {
        _rules = rules;
        _catalog = catalog;
    }

    /// <summary>Rule ids in evaluation order (ordinal).</summary>
    public IEnumerable<string> RuleIds => _rules.Select(r => r.Id);

    /// <summary>Builds and cross-checks the rules against catalog and action vocabulary.</summary>
    public static CombinationRuleSet Create(IEnumerable<CombinationRules> files, ObjectCatalog catalog, Vocabulary actionVocabulary)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(actionVocabulary);
        if (actionVocabulary.Kind != VocabularyKind.Action)
        {
            throw new InvalidDataException("Combination rules need the action vocabulary.");
        }

        var actions = actionVocabulary.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var rules = files.SelectMany(f => f.Rules).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var problems = new List<string>();
            if (!seen.Add(rule.Id))
            {
                problems.Add("id is defined twice");
            }

            if (!actions.Contains(rule.Action))
            {
                problems.Add($"unknown action '{rule.Action}'");
            }

            problems.AddRange(rule.Inputs.SelectMany(i => i.Properties)
                .Where(p => !catalog.PropertyVocabulary.Contains(p)).Select(p => $"unknown property '{p}'"));
            problems.AddRange(rule.Creates.Where(t => !catalog.Contains(t)).Select(t => $"creates unknown type '{t}'"));
            problems.AddRange(rule.Consumes.Where(i => i >= rule.Inputs.Count).Select(i => $"consumes missing input {i}"));
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"Combination rule '{rule.Id}': {string.Join("; ", problems)}.");
            }
        }

        return new CombinationRuleSet(rules, catalog);
    }

    /// <summary>
    /// Rules that fit an action and the given object types. Each input slot needs its own object that has
    /// all slot properties; the order of <paramref name="typeIds"/> does not matter. Result is in rule-id
    /// order with the assignment slot → index into <paramref name="typeIds"/>.
    /// </summary>
    public IReadOnlyList<(CombinationRule Rule, int[] Assignment)> Match(string action, IReadOnlyList<string> typeIds)
    {
        ArgumentNullException.ThrowIfNull(typeIds);
        var result = new List<(CombinationRule, int[])>();
        foreach (var rule in _rules.Where(r => r.Action == action && r.Inputs.Count == typeIds.Count))
        {
            var slots = rule.Inputs.ToList();
            var assignment = new int[slots.Count];
            if (Assign(slots, typeIds, 0, new bool[typeIds.Count], assignment))
            {
                result.Add((rule, assignment));
            }
        }

        return result;
    }

    private bool Assign(List<InputSlot> slots, IReadOnlyList<string> typeIds, int slot, bool[] used, int[] assignment)
    {
        if (slot == slots.Count)
        {
            return true;
        }

        for (var i = 0; i < typeIds.Count; i++)
        {
            if (used[i] || !slots[slot].Properties.All(p => _catalog.TrueProperties(typeIds[i]).Contains(p)))
            {
                continue;
            }

            used[i] = true;
            assignment[slot] = i;
            if (Assign(slots, typeIds, slot + 1, used, assignment))
            {
                return true;
            }

            used[i] = false;
        }

        return false;
    }
}
