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
public sealed record InventionRules(
    InventionSettings Settings,
    FrozenSet<string> Properties,
    FrozenSet<string> Actions,
    FrozenSet<string> Processes,
    IReadOnlyList<string> TechLevels,
    TextGuard Guard);

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

    /// <summary>Checks a result against the rules and the materials sent with the job.</summary>
    public static IReadOnlyList<string> Check(InventionResult result, IReadOnlySet<string> perceivedTypes, InventionRules rules)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(perceivedTypes);
        ArgumentNullException.ThrowIfNull(rules);
        var lastRound = result.Rounds.OrderBy(r => r.Round).LastOrDefault();
        if (result.Decision == InventionResultDecision.Reject || result.Invention is null || lastRound?.Decision != GuardVerdictDecision.Approve)
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

        if (!rules.Actions.Contains(invention.Purpose))
        {
            reasons.Add(UnknownPurpose);
        }

        if (invention.Steps.Any(s => !rules.Processes.Contains(s.Process)))
        {
            reasons.Add(UnknownProcess);
        }

        var level = IndexOf(rules.TechLevels, invention.TechLevel);
        var allowed = IndexOf(rules.TechLevels, settings.TechLevel);
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

        return [.. reasons];
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
