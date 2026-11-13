using System.Collections.Frozen;
using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Objects;

/// <summary>
/// A processing rule that fits a try: <see cref="Inputs"/> = for each input of the rule the index of the
/// thing in the try, <see cref="Tool"/> = the index of the thing used as the tool, or null.
/// </summary>
public sealed record ProcessMatch(ProcessRule Rule, IReadOnlyList<int> Inputs, int? Tool);

/// <summary>
/// The basic processing rules of the data (content/processes/rules.json, B2537) as the core applies them
/// to a combine trial (B2687 K2, D932): two things put together, the target first. Inputs and tool match by
/// TRUE properties, never by type names. Of several fitting rules the most specific wins (schema: more
/// inputs, more required properties, a tool, a heat condition; then the id). Rules that need heat do not
/// apply until the heat sources are known (A1574); rules above the world's tech level never apply.
/// </summary>
public sealed class ProcessRuleSet
{
    /// <summary>At most this many candidate processes go to the judge (contract: 1..8).</summary>
    public const int MaxCandidates = 8;

    private readonly IReadOnlyList<ProcessRule> _rules;
    private readonly IReadOnlyList<string> _techLevels;
    private readonly int _worldTech;

    private ProcessRuleSet(IReadOnlyList<ProcessRule> rules, IReadOnlyList<string> techLevels, string worldTechLevel)
    {
        _rules = rules;
        _techLevels = techLevels;
        _worldTech = IndexOf(techLevels, worldTechLevel);
        TechLevel = worldTechLevel;
    }

    /// <summary>The world's tech level (content/invention/settings.json), sent with a job.</summary>
    public string TechLevel { get; }

    /// <summary>Rule ids in specificity order.</summary>
    public IEnumerable<string> RuleIds => _rules.Select(r => r.Id);

    /// <summary>Builds and checks the rules against the vocabularies and the catalog.</summary>
    public static ProcessRuleSet Create(ProcessRules data, ObjectCatalog catalog, IReadOnlySet<string> processes, IReadOnlyList<string> techLevels, string worldTechLevel)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(techLevels);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in data.Rules)
        {
            var problems = new List<string>();
            if (!seen.Add(rule.Id))
            {
                problems.Add("id is defined twice");
            }

            if (!processes.Contains(rule.Process))
            {
                problems.Add($"unknown process '{rule.Process}'");
            }

            if (IndexOf(techLevels, rule.TechLevel) < 0)
            {
                problems.Add($"unknown tech level '{rule.TechLevel}'");
            }

            if (rule.HeatMin is { } heat && !data.HeatLevels.Contains(heat, StringComparer.Ordinal))
            {
                problems.Add($"unknown heat level '{heat}'");
            }

            problems.AddRange(Properties(rule).Where(p => !catalog.PropertyVocabulary.Contains(p)).Select(p => $"unknown property '{p}'"));
            if (rule.Result.Produces is { } produces && !catalog.Contains(produces))
            {
                problems.Add($"produces unknown type '{produces}'");
            }

            problems.AddRange(rule.Result.Consumes.Where(i => i >= rule.Inputs.Count).Select(i => $"consumes missing input {i}"));
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"Process rule '{rule.Id}': {string.Join("; ", problems)}.");
            }
        }

        return new ProcessRuleSet([.. data.Rules.OrderBy(r => r, Specificity.Instance)], techLevels, worldTechLevel);
    }

    /// <summary>
    /// The most specific data rule that fits the try (things[0] = target, things[1] = partner, each with
    /// its true properties), or null.
    /// </summary>
    /// <param name="things">The true properties of the things, target first.</param>
    /// <param name="worldTech">The world's reached tech level as a number (A1625); null = the start level of the data.</param>
    public ProcessMatch? Match(IReadOnlyList<FrozenSet<string>> things, int? worldTech = null)
    {
        ArgumentNullException.ThrowIfNull(things);
        foreach (var rule in _rules)
        {
            if (Applies(rule, worldTech) && Fit(rule, things) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// The processes the judge may choose from for an unknown try (B2687 K2): those with a rule of the
    /// right shape for these things (two inputs, or one input and a tool) that can apply here (no heat
    /// needed, tech level reached), by their most specific such rule, each once, at most <see cref="MaxCandidates"/>.
    /// </summary>
    public IReadOnlyList<string> Candidates(int things, int? worldTech = null) =>
        [.. _rules.Where(r => Applies(r, worldTech) && Shaped(r, things)).Select(r => r.Process).Distinct(StringComparer.Ordinal).Take(MaxCandidates)];

    /// <summary>Whether a rule (of the data or found in the world) can apply here: no heat needed, tech level reached.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="worldTech">The world's reached tech level as a number (A1625); null = the start level of the data.</param>
    public bool Applies(ProcessRule rule, int? worldTech = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.HeatMin is null && IndexOf(_techLevels, rule.TechLevel) is var level && level >= 0 && level <= (worldTech ?? _worldTech);
    }

    /// <summary>
    /// How a rule fits the things: two inputs on both (target first), or one input and a tool (the target
    /// as the input first). Null when it does not fit.
    /// </summary>
    public static ProcessMatch? Fit(ProcessRule rule, IReadOnlyList<FrozenSet<string>> things)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(things);
        if (things.Count != 2)
        {
            return null;
        }

        int[][] orders = [[0, 1], [1, 0]];
        foreach (var order in orders)
        {
            if (rule.Inputs.Count == 2 && rule.Tool is null
                && Fits(rule.Inputs.ElementAt(0), things[order[0]]) && Fits(rule.Inputs.ElementAt(1), things[order[1]]))
            {
                return new ProcessMatch(rule, order, null);
            }

            if (rule.Inputs.Count == 1 && rule.Tool is { } tool
                && Fits(rule.Inputs.ElementAt(0), things[order[0]]) && Fits(tool, things[order[1]]))
            {
                return new ProcessMatch(rule, [order[0]], order[1]);
            }
        }

        return null;
    }

    private static bool Shaped(ProcessRule rule, int things) =>
        things == 2 && ((rule.Inputs.Count == 2 && rule.Tool is null) || (rule.Inputs.Count == 1 && rule.Tool is not null));

    private static bool Fits(PropertyCondition condition, FrozenSet<string> properties) =>
        condition.PropertiesAll.All(properties.Contains) && !condition.PropertiesNone.Any(properties.Contains);

    private static IEnumerable<string> Properties(ProcessRule rule) =>
        rule.Inputs.SelectMany(i => i.PropertiesAll.Concat(i.PropertiesNone))
            .Concat(rule.Tool is { } tool ? tool.PropertiesAll.Concat(tool.PropertiesNone) : [])
            .Concat(rule.Result.AddProperties).Concat(rule.Result.RemoveProperties);

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The schema's order: more inputs, more required properties, a tool, a heat condition, then the id.</summary>
    private sealed class Specificity : IComparer<ProcessRule>
    {
        public static readonly Specificity Instance = new();

        public int Compare(ProcessRule? x, ProcessRule? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var c = y.Inputs.Count.CompareTo(x.Inputs.Count);
            if (c == 0)
            {
                c = y.Inputs.Sum(i => i.PropertiesAll.Count).CompareTo(x.Inputs.Sum(i => i.PropertiesAll.Count));
            }

            if (c == 0)
            {
                c = (y.Tool is not null).CompareTo(x.Tool is not null);
            }

            if (c == 0)
            {
                c = (y.HeatMin is not null).CompareTo(x.HeatMin is not null);
            }

            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
