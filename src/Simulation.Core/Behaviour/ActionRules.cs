using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>Timing of an action in ticks.</summary>
/// <param name="DurationTicks">How long the action takes once started.</param>
/// <param name="ReachM">Distance to the target at which it can start.</param>
/// <param name="RandomNearby">True if an action without a target happens at a random spot nearby.</param>
public readonly record struct ActionTimingInfo(long DurationTicks, double ReachM, bool RandomNearby);

/// <summary>
/// What actions do (content/effects/*.json). The effect of an action on a target is decided only by the
/// target's TRUE properties in the <see cref="ObjectCatalog"/> (the world knows the truth); the code knows
/// no action, food or poison by name.
/// </summary>
public sealed class ActionRules
{
    private readonly FrozenDictionary<string, ActionTimingInfo> _timing;
    private readonly FrozenDictionary<string, EffectRule[]> _rules;
    private readonly ObjectCatalog _catalog;

    private ActionRules(FrozenDictionary<string, ActionTimingInfo> timing, FrozenDictionary<string, EffectRule[]> rules, ObjectCatalog catalog)
    {
        _timing = timing;
        _rules = rules;
        _catalog = catalog;
    }

    /// <summary>Actions that have timing data.</summary>
    public IEnumerable<string> Actions => _timing.Keys.Order(StringComparer.Ordinal);

    /// <summary>Builds the rules and checks every id against the vocabularies.</summary>
    public static ActionRules Create(IEnumerable<ActionEffects> files, ObjectCatalog catalog, Vocabulary actions, IReadOnlySet<string> needs, IReadOnlySet<string> moods, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(actions);
        var actionIds = actions.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var all = files.ToList();
        var timing = new Dictionary<string, ActionTimingInfo>(StringComparer.Ordinal);
        foreach (var t in all.SelectMany(f => f.Actions))
        {
            Require(actionIds.Contains(t.Action), $"timing for unknown action '{t.Action}'");
            Require(timing.TryAdd(t.Action, new ActionTimingInfo(Math.Max(1, (long)t.DurationMinutes * ticksPerHour / 60), t.ReachM, t.Destination == ActionTimingDestination.Random_nearby)), $"timing for '{t.Action}' defined twice");
        }

        foreach (var rule in all.SelectMany(f => f.Rules))
        {
            Require(timing.ContainsKey(rule.Action), $"effect rule for action '{rule.Action}' without timing");
            Require(rule.RequiresProperty is null || catalog.PropertyVocabulary.Contains(rule.RequiresProperty), $"effect rule uses unknown property '{rule.RequiresProperty}'");
            foreach (var effect in rule.Effects)
            {
                Require(effect.Kind switch
                {
                    EffectKind.Need => needs.Contains(effect.Id),
                    EffectKind.Mood => moods.Contains(effect.Id),
                    _ => true,
                }, $"effect on unknown {effect.Kind} '{effect.Id}'");
            }
        }

        var rules = all.SelectMany(f => f.Rules)
            .GroupBy(r => r.Action, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(r => r.RequiresProperty ?? string.Empty, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return new ActionRules(timing.ToFrozenDictionary(StringComparer.Ordinal), rules, catalog);
    }

    /// <summary>Timing of an action.</summary>
    public ActionTimingInfo Timing(string action) =>
        _timing.TryGetValue(action, out var timing) ? timing : throw new KeyNotFoundException($"No timing for action '{action}'.");

    /// <summary>True if the action has rules that need a target.</summary>
    public bool NeedsTarget(string action) => Rules(action).Any(r => r.RequiresProperty is not null);

    /// <summary>All rules of an action (target property rules in property order).</summary>
    public IReadOnlyList<EffectRule> Rules(string action) => _rules.TryGetValue(action, out var rules) ? rules : [];

    /// <summary>
    /// The rules that apply to <paramref name="action"/> on an object of <paramref name="targetTypeId"/>:
    /// rules without a required property always, the others only if the target truly has the property.
    /// </summary>
    public IReadOnlyList<EffectRule> Applying(string action, string? targetTypeId) =>
        [.. Rules(action).Where(r => r.RequiresProperty is null
            || (targetTypeId is not null && _catalog.HasProperty(targetTypeId, r.RequiresProperty)))];

    private static void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw new InvalidDataException("Action effects: " + problem + ".");
        }
    }
}
