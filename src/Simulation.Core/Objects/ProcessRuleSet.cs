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
    /// <summary>The most candidate processes the contract allows (D1025, GW2 #25006).</summary>
    public const int ContractMaxCandidates = 16;

    /// <summary>
    /// At most this many candidate processes go to the judge: <c>physics.max_candidates</c> of the data, else 8.
    /// Large enough for every process physics allows, so the ranking by our data rules only orders them and never
    /// decides which ones the judge sees (D1025).
    /// </summary>
    public int MaxCandidates => Math.Clamp(_physics?.MaxCandidates ?? 8, 1, ContractMaxCandidates);

    private readonly IReadOnlyList<ProcessRule> _rules;
    private IReadOnlyList<string> _processes = [];
    private Physics? _physics;
    private readonly IReadOnlyList<string> _techLevels;
    private readonly int _worldTech;

    private ProcessRuleSet(IReadOnlyList<ProcessRule> rules, IReadOnlyList<string> techLevels, string worldTechLevel, IReadOnlyList<string> heatLevels)
    {
        HeatLevels = heatLevels;
        _rules = rules;
        _techLevels = techLevels;
        _worldTech = IndexOf(techLevels, worldTechLevel);
        TechLevel = worldTechLevel;
    }

    /// <summary>The world's tech level (content/invention/settings.json), sent with a job.</summary>
    public string TechLevel { get; }

    /// <summary>The heat levels, lowest first (B2700).</summary>
    public IReadOnlyList<string> HeatLevels { get; }

    /// <summary>
    /// What a step of a plan makes of its materials by the data's rules (D1097): the most specific rule of the step's process
    /// that fits them - two materials as a pair, or one alone - at the given tech level and any heat (a plan does not say how
    /// hot; the tech level bounds what it can reach). The result is the target's properties with the rule's additions and
    /// removals, or null when no rule fits.
    /// </summary>
    public FrozenSet<string>? StepResult(string process, IReadOnlyList<FrozenSet<string>> materials, int? worldTech)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var hottest = HeatLevels.Count - 1;
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Process, process, StringComparison.Ordinal) || !Applies(rule, worldTech, hottest))
            {
                continue;
            }

            FrozenSet<string>? target = null;
            if (materials.Count >= 2 && Fit(rule, [materials[0], materials[1]]) is { } pair)
            {
                target = materials[pair.Inputs[0]];
            }
            else if (materials.Count >= 1 && rule.Inputs.Count == 1 && rule.Tool is null && FitsOne(rule, materials[0]))
            {
                target = materials[0];
            }

            if (target is not null)
            {
                return target.Union(rule.Result.AddProperties, StringComparer.Ordinal).Except(rule.Result.RemoveProperties, StringComparer.Ordinal)
                    .ToFrozenSet(StringComparer.Ordinal);
            }
        }

        return null;
    }

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

        // B2700: a burning type's heat level must be one of the table's.
        foreach (var typeId in catalog.TypeIds)
        {
            if (catalog.Params(typeId).HeatLevel is { } level && !data.HeatLevels.Contains(level, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Object type '{typeId}': unknown heat level '{level}'.");
            }
        }

        return new ProcessRuleSet([.. data.Rules.OrderBy(r => r, Specificity.Instance)], techLevels, worldTechLevel, [.. data.HeatLevels])
        {
            _processes = [.. processes.Order(StringComparer.Ordinal)],
            _physics = data.Physics,
        };
    }

    /// <summary>
    /// The most specific data rule that fits the try (things[0] = target, things[1] = partner, each with
    /// its true properties), or null.
    /// </summary>
    /// <param name="things">The true properties of the things, target first.</param>
    /// <param name="worldTech">The world's reached tech level as a number (A1625); null = the start level of the data.</param>
    /// <param name="heat">The heat level at the try (position in <see cref="HeatLevels"/>), or null.</param>
    /// <param name="heatSource">Index of the thing that is the heat source itself (a fire put together with food), or null.</param>
    public ProcessMatch? Match(IReadOnlyList<FrozenSet<string>> things, int? worldTech = null, int? heat = null, int? heatSource = null)
    {
        ArgumentNullException.ThrowIfNull(things);
        foreach (var rule in _rules)
        {
            if (Applies(rule, worldTech, heat) && Fit(rule, things, heatSource) is { } match)
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
    public IReadOnlyList<string> Candidates(int things, int? worldTech = null, int? heat = null, bool withHeatSource = false) =>
        [.. _rules.Where(r => Applies(r, worldTech, heat) && Shaped(r, things, withHeatSource)).Select(r => r.Process).Distinct(StringComparer.Ordinal).Take(MaxCandidates)];

    /// <summary>
    /// The processes the judge may choose from for an unknown try, derived from the data as a whole (TL #23700, GW2
    /// D991): every process of the vocabulary that the judge's physics table does not rule out for these things
    /// (heat processes without heat, needs_any, never_if), most promising first - the most required properties the
    /// things share with a rule of that process (any tech level and shape: the judge may find a new rule), ties by
    /// id - at most <see cref="MaxCandidates"/>. Rules of the data apply before a try ever gets here.
    /// </summary>
    /// <param name="inputs">The true properties of the things the process would work on.</param>
    /// <param name="heat">The heat level at the try, or null.</param>
    public IReadOnlyList<string> CandidatesFor(IReadOnlyList<FrozenSet<string>> inputs, int? heat)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var all = inputs.SelectMany(i => i).ToHashSet(StringComparer.Ordinal);
        var physics = _physics;
        bool Ruled(string process) =>
            (heat is null && physics?.HeatProcesses.Contains(process) == true)
            || (physics?.NeedsAny is { } needs && needs.TryGetValue(process, out var anyOf) && !anyOf.Any(all.Contains))
            || (physics?.NeverIf is { } never && never.TryGetValue(process, out var not) && not.Any(all.Contains));
        // How close a rule of the process comes (GW2 D999): first the conditions (inputs and tool) one of the things
        // fully meets, then the required properties they share - so a rule with many inputs does not win by
        // collecting single properties (two stones: strike and split before bind).
        int Promise(string process) => _rules.Where(r => r.Process == process)
            .Select(r =>
            {
                var conditions = r.Tool is { } tool ? r.Inputs.Append(tool) : r.Inputs;
                var met = MostMet([.. conditions], inputs, 0, new bool[inputs.Count]);
                var shared = conditions.Sum(c => c.PropertiesAll.Count(all.Contains));
                // The share of the rule's conditions met (GW2 D1016): one of two (strike) before one of three (bind).
                var count = conditions.Count();
                return (count == 0 ? 0 : met * 1000 / count * 1000) + shared;
            })
            .DefaultIfEmpty(0).Max();
        return [.. _processes.Where(p => !Ruled(p))
            .Select(p => (Process: p, Promise: Promise(p)))
            .OrderByDescending(p => p.Promise).ThenBy(p => p.Process, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(p => p.Process)];
    }

    /// <summary>Whether a rule (of the data or found in the world) can apply here: no heat needed, tech level reached.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="worldTech">The world's reached tech level as a number (A1625); null = the start level of the data.</param>
    /// <param name="heat">The heat level at the try (position in <see cref="HeatLevels"/>, B2700), or null = no heat.</param>
    public bool Applies(ProcessRule rule, int? worldTech = null, int? heat = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var hotEnough = rule.HeatMin is null || (heat is { } h && IndexOf(HeatLevels, rule.HeatMin) is var need && need >= 0 && need <= h);
        return hotEnough && IndexOf(_techLevels, rule.TechLevel) is var level && level >= 0 && level <= (worldTech ?? _worldTech);
    }

    /// <summary>The position of a heat level, or -1.</summary>
    public int HeatIndex(string level) => IndexOf(HeatLevels, level);

    /// <summary>
    /// How a rule fits the things: two inputs on both (target first), or one input and a tool (the target
    /// as the input first). Null when it does not fit.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="things">The true properties of the things, target first.</param>
    /// <param name="heatSource">Index of the thing that is the heat source (B2700): a rule with one input and no tool then works on the other thing.</param>
    public static ProcessMatch? Fit(ProcessRule rule, IReadOnlyList<FrozenSet<string>> things, int? heatSource = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(things);
        if (things.Count != 2)
        {
            return null;
        }

        if (heatSource is { } source && rule.Inputs.Count == 1 && rule.Tool is null && Fits(rule.Inputs.ElementAt(0), things[1 - source]))
        {
            // Food held to the fire: the fire is the heat, the other thing the input.
            return new ProcessMatch(rule, [1 - source], null);
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

    private static bool Shaped(ProcessRule rule, int things, bool withHeatSource) =>
        things == 2 && ((rule.Inputs.Count == 2 && rule.Tool is null) || (rule.Inputs.Count == 1 && (rule.Tool is not null || withHeatSource)));

    /// <summary>Whether one input fits a set of true properties (a judged one-input rule against its try).</summary>
    public static bool FitsOne(ProcessRule rule, FrozenSet<string> properties)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Inputs.Count == 1 && rule.Tool is null && Fits(rule.Inputs.ElementAt(0), properties);
    }

    /// <summary>
    /// The most conditions the things can meet, each thing for one condition only (review GW1 #376): the best
    /// assignment, as <see cref="Fit"/> tries both orders. Small: at most four conditions and a few things.
    /// </summary>
    private static int MostMet(List<PropertyCondition> conditions, IReadOnlyList<FrozenSet<string>> things, int from, bool[] used)
    {
        if (from == conditions.Count)
        {
            return 0;
        }

        var best = MostMet(conditions, things, from + 1, used);
        for (var i = 0; i < things.Count; i++)
        {
            if (!used[i] && Fits(conditions[from], things[i]))
            {
                used[i] = true;
                best = Math.Max(best, 1 + MostMet(conditions, things, from + 1, used));
                used[i] = false;
            }
        }

        return best;
    }

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
