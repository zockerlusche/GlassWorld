using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>Timing of an action in ticks.</summary>
/// <param name="DurationTicks">How long the action takes once started.</param>
/// <param name="ReachM">Distance to the target at which it can start.</param>
/// <param name="RandomNearby">True if an action without a target happens at a random spot nearby.</param>
/// <param name="Offered">False for actions the decider may not offer (the core plans them itself).</param>
/// <param name="Frontier">True if an action without a target happens where the land the inhabitant has not seen yet begins (B2-6 R14).</param>
/// <param name="Gradual">True if the effects work while the action lasts, a share every tick (B2681), instead of at its end.</param>
/// <param name="WakeAfterTicks">Ticks the action has run before a pressing need can cut it short (B2681).</param>
/// <param name="PartnerReachM">Greatest distance of a partner thing from the target (combine, B2687); 0 = no partners.</param>
/// <param name="PartnerOptions">At most this many options with a partner per question; 0 = the action uses its target alone.</param>
public readonly record struct ActionTimingInfo(long DurationTicks, double ReachM, bool RandomNearby, bool Offered = true, bool Frontier = false, bool Gradual = false, long WakeAfterTicks = 0, double PartnerReachM = 0, int PartnerOptions = 0);

/// <summary>
/// What actions do (content/effects/*.json). The effect of an action on a target is decided only by the
/// target's TRUE properties in the <see cref="ObjectCatalog"/> (the world knows the truth); the code knows
/// no action, food or poison by name.
/// </summary>
public sealed class ActionRules
{
    private readonly FrozenDictionary<string, ActionTimingInfo> _timing;
    private readonly FrozenDictionary<string, EffectRule[]> _rules;
    private readonly FrozenDictionary<string, EventEffect[]> _eventEffects;
    private readonly ITypeTruth _catalog;

    private ActionRules(FrozenDictionary<string, ActionTimingInfo> timing, FrozenDictionary<string, EffectRule[]> rules, FrozenDictionary<string, EventEffect[]> eventEffects, ITypeTruth catalog)
    {
        _timing = timing;
        _rules = rules;
        _eventEffects = eventEffects;
        _catalog = catalog;
    }

    /// <summary>Actions that have timing data.</summary>
    public IEnumerable<string> Actions => _timing.Keys.Order(StringComparer.Ordinal);

    /// <summary>Actions the decider may offer as options (all with timing except those the core plans itself).</summary>
    public IEnumerable<string> OfferedActions => _timing.Where(t => t.Value.Offered).Select(t => t.Key).Order(StringComparer.Ordinal);

    /// <summary>Builds the rules and checks every id against the vocabularies.</summary>
    /// <param name="files">The effect files (content/effects).</param>
    /// <param name="catalog">Object types and the property vocabulary.</param>
    /// <param name="actions">Action vocabulary.</param>
    /// <param name="needs">Need ids.</param>
    /// <param name="moods">Mood ids.</param>
    /// <param name="ticksPerHour">Ticks per game hour.</param>
    /// <param name="events">Event kinds for <c>event_effects</c>; null = the kinds are not checked (rules built without the event vocabulary).</param>
    public static ActionRules Create(IEnumerable<ActionEffects> files, ObjectCatalog catalog, Vocabulary actions, IReadOnlySet<string> needs, IReadOnlySet<string> moods, int ticksPerHour, IReadOnlySet<string>? events = null)
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
            Require(
                t.Destination != ActionTimingDestination.Frontier || actions.Entries.First(e => e.Id == t.Action).Target is null or VocabularyEntryTarget.None,
                $"action '{t.Action}' has a target, so it cannot happen at the frontier");
            Require(
                t.Partners is null || actions.Entries.First(e => e.Id == t.Action).Target == VocabularyEntryTarget.Object,
                $"action '{t.Action}' has partners, so it needs an object target");
            Require(timing.TryAdd(t.Action, new ActionTimingInfo(Math.Max(1, (long)t.DurationMinutes * ticksPerHour / 60), t.ReachM, t.Destination == ActionTimingDestination.Random_nearby, t.DeciderOption ?? true, t.Destination == ActionTimingDestination.Frontier, t.Gradual ?? false, (long)(t.WakeAfterMinutes ?? 0) * ticksPerHour / 60, t.Partners?.ReachM ?? 0, t.Partners?.MaxOptions ?? 0)), $"timing for '{t.Action}' defined twice");
        }

        foreach (var rule in all.SelectMany(f => f.Rules))
        {
            Require(timing.ContainsKey(rule.Action), $"effect rule for action '{rule.Action}' without timing");
            Require(rule.RequiresProperty is null || catalog.PropertyVocabulary.Contains(rule.RequiresProperty), $"effect rule uses unknown property '{rule.RequiresProperty}'");
            Require(rule.RequiresAlso is null || (rule.RequiresProperty is not null && catalog.PropertyVocabulary.Contains(rule.RequiresAlso)), $"effect rule for '{rule.Action}': requires_also '{rule.RequiresAlso}' needs a requires_property and a known property");
            foreach (var effect in rule.Effects)
            {
                Require(Known(effect), $"effect on unknown {effect.Kind} '{effect.Id}'");
            }
        }

        foreach (var onEvent in all.SelectMany(f => f.EventEffects ?? []))
        {
            Require(events is null || events.Contains(onEvent.Event), $"event effect for unknown event '{onEvent.Event}'");
            foreach (var effect in onEvent.Effects)
            {
                Require(Known(effect), $"event effect on unknown {effect.Kind} '{effect.Id}'");
                Require(effect.ListenerShare is null, $"event effect of '{onEvent.Event}' has a listener_share (an event has no listener)");
            }
        }

        bool Known(Effect effect) => effect.Kind switch
        {
            EffectKind.Need => needs.Contains(effect.Id),
            EffectKind.Mood => moods.Contains(effect.Id),
            _ => true,
        };

        var rules = all.SelectMany(f => f.Rules)
            .GroupBy(r => r.Action, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(r => r.RequiresProperty ?? string.Empty, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var eventEffects = all.SelectMany(f => f.EventEffects ?? [])
            .GroupBy(e => e.Event, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return new ActionRules(timing.ToFrozenDictionary(StringComparer.Ordinal), rules, eventEffects, catalog);
    }

    /// <summary>
    /// The same rules judged against another type truth (the world's catalog with runtime types). Cheap:
    /// the rule tables are shared.
    /// </summary>
    public ActionRules WithTruth(ITypeTruth truth)
    {
        ArgumentNullException.ThrowIfNull(truth);
        return new ActionRules(_timing, _rules, _eventEffects, truth);
    }

    /// <summary>Timing of an action.</summary>
    public ActionTimingInfo Timing(string action) =>
        _timing.TryGetValue(action, out var timing) ? timing : throw new KeyNotFoundException($"No timing for action '{action}'.");

    /// <summary>True if the action has timing data (it can be done).</summary>
    public bool HasTiming(string action) => _timing.ContainsKey(action);

    /// <summary>True if the action has rules that need a target.</summary>
    public bool NeedsTarget(string action) => Rules(action).Any(r => r.RequiresProperty is not null);

    /// <summary>All rules of an action (target property rules in property order).</summary>
    public IReadOnlyList<EffectRule> Rules(string action) => _rules.TryGetValue(action, out var rules) ? rules : [];

    /// <summary>
    /// The rules that apply to <paramref name="action"/> on an object of <paramref name="targetTypeId"/>:
    /// rules without a required property always, the others only if the target truly has the property (and the
    /// second one, requires_also, B2668: e.g. only a made thing).
    /// </summary>
    public IReadOnlyList<EffectRule> Applying(string action, string? targetTypeId) =>
        [.. Rules(action).Where(r => (r.RequiresProperty is null
            || (targetTypeId is not null && _catalog.HasProperty(targetTypeId, r.RequiresProperty)))
            && (r.RequiresAlso is null || (targetTypeId is not null && _catalog.HasProperty(targetTypeId, r.RequiresAlso))))];

    /// <summary>
    /// The effects a reported core event has on the inhabitant it is about (data: <c>event_effects</c>, W1744),
    /// in data order: every entry of the kind whose <c>detail_ends_with</c> is absent or ends the event's detail.
    /// </summary>
    public IEnumerable<Effect> EventEffects(string kind, string? detail)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return _eventEffects.TryGetValue(kind, out var entries)
            ? entries.Where(e => e.DetailEndsWith is null || (detail is not null && detail.EndsWith(e.DetailEndsWith, StringComparison.Ordinal))).SelectMany(e => e.Effects)
            : [];
    }

    private static void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw new InvalidDataException("Action effects: " + problem + ".");
        }
    }
}
