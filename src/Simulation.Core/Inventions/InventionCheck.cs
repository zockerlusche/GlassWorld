using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Safety;

namespace GlassWorld.Simulation.Inventions;

/// <summary>
/// What the core's final check of an invention knows (D471 stage 3): vocabularies, the tech level order,
/// limits and the B2531 list from the data, and the content guard for names.
/// </summary>
/// <param name="Settings">Invention settings (content/invention/settings.json).</param>
/// <param name="Properties">Property vocabulary.</param>
/// <param name="Actions">Action vocabulary (purposes).</param>
/// <param name="Processes">Process vocabulary (steps).</param>
/// <param name="TechLevels">Tech levels in their order (vocabulary order).</param>
/// <param name="Guard">Content guard for generated names (B2466).</param>
/// <param name="Build">Construction kit, build settings and compute-night phases (D471 stages 4, 5, 7).</param>
public sealed record InventionRules(
    InventionSettings Settings,
    FrozenSet<string> Properties,
    FrozenSet<string> Actions,
    FrozenSet<string> Processes,
    IReadOnlyList<string> TechLevels,
    TextGuard Guard,
    BuildRules Build)
{
    /// <summary>
    /// The base functions of the data (D827 R31, content/invention/base_functions.json): id -> the properties a thing with
    /// that function gets. The core acts on these properties only, never on the function itself (D1022).
    /// </summary>
    public FrozenDictionary<string, FrozenSet<string>> BaseFunctions { get; init; } = FrozenDictionary<string, FrozenSet<string>>.Empty;

    /// <summary>
    /// Properties a made thing has only if one of its materials has them (process physics <c>no_new</c>: never food or
    /// drink from nothing; D1096 #1). Empty without process data.
    /// </summary>
    public FrozenSet<string> NoNew { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Properties a made thing keeps when one of its materials has them (process physics <c>never_remove</c>: poison
    /// stays poison; D1096 #1). Empty without process data.
    /// </summary>
    public FrozenSet<string> NeverRemove { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The data's process rules: what a plan's steps make of its materials (D1097); null = steps add nothing.</summary>
    public Objects.ProcessRuleSet? StepRules { get; init; }

    /// <summary>Whether the core derives a made thing's truth and introduces are only the inventor's expectation (D1097).</summary>
    public bool Derives => Settings.MaterialTransfer is not null;

    /// <summary>
    /// What a type made from <paramref name="invention"/> is: its own properties, what being made implies, what its base
    /// function grants, and - with <paramref name="truth"/> (the world's true properties of a type) - what of
    /// <see cref="NeverRemove"/> its materials have (the world knows the truth, D1096 #1).
    /// </summary>
    public IEnumerable<string> PropertiesOf(Invention invention, Func<string, IEnumerable<string>>? truth = null)
    {
        ArgumentNullException.ThrowIfNull(invention);
        var granted = invention.BaseFunction is { } function && BaseFunctions.TryGetValue(function, out var properties) ? properties : [];
        var kept = truth is null ? [] : MaterialsOf(invention).SelectMany(truth).Where(NeverRemove.Contains);
        var own = Derives ? Transferred(invention, truth) : invention.Introduces;
        return own.Concat(Settings.ImpliedProperties).Concat(granted).Concat(kept).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// D1097: what of its materials a made thing has as a whole (material_transfer) - a material a step works on counts as
    /// what the step's rule makes of it; the others as they are.
    /// </summary>
    private IEnumerable<string> Transferred(Invention invention, Func<string, IEnumerable<string>>? truth)
    {
        if (truth is null || Settings.MaterialTransfer is not { } transfer)
        {
            return [];
        }

        FrozenSet<string> Of(string type) => truth(type).ToFrozenSet(StringComparer.Ordinal);
        var worked = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var tech = TechLevels.ToList().IndexOf(invention.TechLevel);
        foreach (var step in invention.Steps)
        {
            var materials = step.Materials.ToList();
            if (StepRules?.StepResult(step.Process, [.. materials.Select(Of)], tech < 0 ? null : tech) is { } result)
            {
                // Only the step's target and what it uses up are changed; its other materials count as they are (review GW2 #424).
                properties.UnionWith(result.Made);
                worked.UnionWith(result.Worked.Select(i => materials[i]));
            }
        }

        foreach (var material in MaterialsOf(invention).Where(m => !worked.Contains(m)))
        {
            properties.UnionWith(truth(material));
        }

        return properties.Where(transfer.Contains);
    }

    /// <summary>Every material the invention names (its own and its steps'), each once.</summary>
    internal static IEnumerable<string> MaterialsOf(Invention invention) =>
        invention.Materials.Concat(invention.Steps.SelectMany(s => s.Materials)).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The world's tech level as a number: the position of <c>Settings.TechLevel</c> in the vocabulary
    /// <c>tech_level</c> (first = 0; checked at load). Data that count tech levels (look epochs) compare with it.
    /// </summary>
    public int ReachedTechLevel
    {
        get
        {
            for (var i = 0; i < TechLevels.Count; i++)
            {
                if (string.Equals(TechLevels[i], Settings.TechLevel, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }
    }
}

/// <summary>
/// The core's final, deterministic check of an AI invention (ADR-0005: the AI proposes, the core decides).
/// Returns reason codes; an empty list means the invention may enter the world. Nothing here names an
/// invention, recipe or object; every limit comes from data.
/// </summary>
public static class InventionCheck
{
    /// <summary>The guard rejected it, or its last round did not approve.</summary>
    public const string NotApproved = "not_approved";

    /// <summary>B2531: only objects and buildings.</summary>
    public const string Category = "category_not_allowed";

    /// <summary>B2531: brings a property of living beings.</summary>
    public const string LivingProperty = "living_property";

    /// <summary>Brings a property outside the vocabulary.</summary>
    public const string UnknownProperty = "unknown_property";

    /// <summary>Uses a material the inhabitant did not perceive.</summary>
    public const string MaterialNotPerceived = "material_not_perceived";

    /// <summary>Purpose is no known action.</summary>
    public const string UnknownPurpose = "unknown_purpose";

    /// <summary>A step uses an unknown process.</summary>
    public const string UnknownProcess = "unknown_process";

    /// <summary>Tech level unknown or above what the world allows.</summary>
    public const string TechLevel = "tech_level_too_high";

    /// <summary>Larger than the limit.</summary>
    public const string TooLarge = "too_large";

    /// <summary>More guard rounds than allowed.</summary>
    public const string TooManyRounds = "too_many_rounds";

    /// <summary>A name did not pass the content guard.</summary>
    public const string NameNotAllowed = "name_not_allowed";

    /// <summary>A name names a weapon, a trap, hunting or fishing gear or poison (B4b R16, B2481; data forbidden_terms).</summary>
    public const string ForbiddenPurpose = "forbidden_purpose";

    /// <summary>Point 11 (D827 R31): the base function is not in the data's list.</summary>
    public const string UnknownBaseFunction = "unknown_base_function";

    /// <summary>Point 11 (D827 R31): no base function although the data require one (require_base_function).</summary>
    public const string MissingBaseFunction = "missing_base_function";

    /// <summary>The invention claims a property none of its materials has though only materials can bring it (no_new, D1096 #1).</summary>
    public const string PropertyWithoutSource = "property_without_source";

    /// <summary>A new need or ability names an action that is not in the vocabulary.</summary>
    public const string UnknownAction = "unknown_action";

    /// <summary>Checks a result against the rules and the materials sent with the job.</summary>
    /// <param name="result">The AI's result.</param>
    /// <param name="perceivedTypes">What the inventor saw.</param>
    /// <param name="rules">The rules of the data.</param>
    /// <param name="techLevel">The world's reached tech level (A1625); null = the start level of the data.</param>
    /// <param name="truth">The world's true properties of a type (D1096 #1); null = not checked against the materials.</param>
    public static IReadOnlyList<string> Check(InventionResult result, IReadOnlySet<string> perceivedTypes, InventionRules rules, string? techLevel = null, Func<string, IEnumerable<string>>? truth = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(perceivedTypes);
        ArgumentNullException.ThrowIfNull(rules);
        var lastRound = result.Rounds.OrderBy(r => r.Round).LastOrDefault();
        // "downsize" is an approval of a smaller version (the service ends the discussion with it, #68).
        if (result.Decision == InventionResultDecision.Reject || result.Invention is null
            || lastRound?.Decision is not (GuardVerdictDecision.Approve or GuardVerdictDecision.Downsize))
        {
            return [NotApproved];
        }

        var invention = result.Invention;
        var settings = rules.Settings;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (invention.Category is not (InventionCategory.Object or InventionCategory.Building))
        {
            reasons.Add(Category);
        }

        if (invention.Introduces.Any(p => settings.LivingProperties.Contains(p)))
        {
            reasons.Add(LivingProperty);
        }

        if (invention.Introduces.Any(p => !rules.Properties.Contains(p)))
        {
            reasons.Add(UnknownProperty);
        }

        if (invention.Materials.Concat(invention.Steps.SelectMany(s => s.Materials)).Any(m => !perceivedTypes.Contains(m)))
        {
            reasons.Add(MaterialNotPerceived);
        }

        // D1096 #1: what only materials can bring (no_new: food, drink) a made thing has only if a material has it.
        if (truth is not null && rules.NoNew.Count > 0)
        {
            var fromMaterials = InventionRules.MaterialsOf(invention).SelectMany(truth).ToHashSet(StringComparer.Ordinal);
            if (invention.Introduces.Any(p => rules.NoNew.Contains(p) && !fromMaterials.Contains(p)))
            {
                reasons.Add(PropertyWithoutSource);
            }
        }

        // Point 11 (D827 R31): the thing is mapped to a base function of the fixed list (data); while the guard does not map
        // yet, a missing one passes unless the data require it.
        if (invention.BaseFunction is { } baseFunction ? !rules.BaseFunctions.ContainsKey(baseFunction) : settings.RequireBaseFunction == true)
        {
            reasons.Add(invention.BaseFunction is null ? MissingBaseFunction : UnknownBaseFunction);
        }

        if (!rules.Actions.Contains(invention.Purpose))
        {
            reasons.Add(UnknownPurpose);
        }

        if (invention.Steps.Any(s => !rules.Processes.Contains(s.Process)))
        {
            reasons.Add(UnknownProcess);
        }

        var level = IndexOf(rules.TechLevels, invention.TechLevel);
        var allowed = IndexOf(rules.TechLevels, techLevel ?? settings.TechLevel);
        if (level < 0 || allowed < 0 || level > allowed)
        {
            reasons.Add(TechLevel);
        }

        var size = invention.SizeM;
        if (Math.Max(size.X, Math.Max(size.Y, size.Z)) > settings.Limits.MaxSizeM)
        {
            reasons.Add(TooLarge);
        }

        if (result.Rounds.Count > settings.Limits.MaxRounds)
        {
            reasons.Add(TooManyRounds);
        }

        if (!rules.Guard.Check(invention.Name.De).Allowed || !rules.Guard.Check(invention.Name.En).Allowed)
        {
            reasons.Add(NameNotAllowed);
        }

        // R16: the core refuses what is a weapon, a trap, hunting gear or poison, whatever the AI guard said.
        var named = new List<(string De, string En)> { (invention.Name.De, invention.Name.En) };
        named.AddRange((invention.IntroducesNeeds ?? []).Select(n => (n.NameDe, n.NameEn)));
        named.AddRange((invention.IntroducesAbilities ?? []).Select(a => (a.NameDe, a.NameEn)));
        if (settings.ForbiddenTerms is { } forbidden && named.Any(n => Holds(n.De, forbidden.De) || Holds(n.En, forbidden.En)))
        {
            reasons.Add(ForbiddenPurpose);
        }

        // New needs and abilities (B4b R11): stored now, created in the core later; their actions must be
        // vocabulary and their names pass the content guard like the invention's name.
        var additions = (invention.IntroducesNeeds ?? []).Select(n => (n.NameDe, n.NameEn, n.Actions))
            .Concat((invention.IntroducesAbilities ?? []).Select(a => (a.NameDe, a.NameEn, a.Actions)));
        foreach (var (nameDe, nameEn, actions) in additions)
        {
            if (actions.Any(a => !rules.Actions.Contains(a)))
            {
                reasons.Add(UnknownAction);
            }

            if (!rules.Guard.Check(nameDe).Allowed || !rules.Guard.Check(nameEn).Allowed)
            {
                reasons.Add(NameNotAllowed);
            }
        }

        return [.. reasons.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a name holds one of the terms: per word of the name (letters only, lower case), a term matches the
    /// whole word; a leading <c>*</c> lets the word begin otherwise, a trailing <c>*</c> lets it go on.
    /// </summary>
    public static bool Holds(string name, IEnumerable<string> terms)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(terms);
        var words = System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0).ToList();
        foreach (var term in terms)
        {
            var stem = term.Trim('*');
            var anyStart = term.StartsWith('*');
            var anyEnd = term.EndsWith('*');
            foreach (var word in words)
            {
                var hit = (anyStart, anyEnd) switch
                {
                    (true, true) => word.Contains(stem, StringComparison.Ordinal),
                    (true, false) => word.EndsWith(stem, StringComparison.Ordinal),
                    (false, true) => word.StartsWith(stem, StringComparison.Ordinal),
                    _ => word == stem,
                };
                if (hit)
                {
                    return true;
                }
            }
        }

        return false;
    }

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
}
