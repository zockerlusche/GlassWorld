using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>An option the routine layer can choose.</summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Target object id or null.</param>
/// <param name="Score">Utility score (higher is better).</param>
public sealed record ScoredOption(string Action, string? TargetId, double Score)
{
    /// <summary>For a search (looking further away): the search key (need, else action) it serves; null otherwise.</summary>
    public string? SearchFor { get; init; }
}

/// <summary>
/// The routine layer (utility AI): scores the options the core allows with weights and curves from data
/// (content/behaviour/utility.json). It sees only the inhabitant's BELIEFS, never the truth. Ties are
/// broken by option order (action, then target id), so the choice is deterministic.
/// </summary>
public sealed class UtilityBrain
{
    private readonly (Consideration Data, Curve Curve, Curve? Novelty)[] _considerations;
    private readonly IReadOnlyDictionary<string, double> _neglectBelow;

    /// <summary>
    /// Creates the brain and checks ids against the action rules and needs. A search action must be an
    /// action without a target (<paramref name="targetlessActions"/>, the vocabulary's target none; without
    /// it: an action no rule needs a target for) on a consideration with a target property. A trait must be
    /// one of <paramref name="traits"/> (when given); exploring (novelty_curve) yields to the needs in
    /// <paramref name="neglectBelow"/> (need id to neglect threshold; without it: to every need).
    /// </summary>
    public UtilityBrain(UtilityWeights weights, ActionRules actions, IReadOnlySet<string> needs, IReadOnlySet<string> properties, IReadOnlySet<string>? targetlessActions = null, IReadOnlySet<string>? traits = null, IReadOnlyDictionary<string, double>? neglectBelow = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(actions);
        Weights = weights;
        if (weights.SearchWeight > weights.UnknownTargetBelief)
        {
            // Trying something unknown close by is how inhabitants learn; searching far away must not outbid it.
            throw new InvalidDataException("Utility weights: search_weight must not exceed unknown_target_belief.");
        }

        _neglectBelow = neglectBelow ?? needs.ToDictionary(n => n, _ => 0.0, StringComparer.Ordinal);
        _considerations = [.. weights.Considerations.Select(c => (c, new Curve(c.Curve), c.NoveltyCurve is { } novelty ? new Curve(novelty) : null))];
        foreach (var (c, _, novelty) in _considerations)
        {
            if (novelty is not null && (c.TargetProperty is not null || c.SearchAction is not null))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}': novelty_curve needs target_property and search_action null.");
            }

            if (c.Trait is { } trait && traits is not null && !traits.Contains(trait))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}' uses an unknown trait '{trait}'.");
            }

            _ = actions.Timing(c.Action);
            if (c.SearchAction is { } search)
            {
                _ = actions.Timing(search);
                if (c.TargetProperty is null || !(targetlessActions?.Contains(search) ?? !actions.NeedsTarget(search)))
                {
                    throw new InvalidDataException($"Utility consideration for '{c.Action}': search_action '{search}' needs a target_property and must be an action without a target.");
                }
            }

            if ((c.Need is not null && !needs.Contains(c.Need))
                || (c.TargetProperty is not null && !properties.Contains(c.TargetProperty))
                || c.AvoidProperties.Any(p => !properties.Contains(p)))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}' uses an unknown need or property.");
            }
        }
    }

    /// <summary>The data in use.</summary>
    public UtilityWeights Weights { get; }

    /// <summary>
    /// Whether the inhabitant's needs suggest an action (F5275 A): an action with need-based considerations
    /// is suggested only while one of them gives it some urgency (weight x the need's curve above 0); an
    /// action without any is always suggested. An exploring consideration (novelty_curve) counts the same
    /// way: it suggests its action on <paramref name="targetTypeId"/> while its score there is above 0
    /// (curiosity, novelty, no pressing need; TL #19921). Data only - no action names in code.
    /// </summary>
    public bool NeedSuggests(Inhabitant inhabitant, string action, string? targetTypeId = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var needBased = false;
        foreach (var (c, curve, novelty) in _considerations)
        {
            if (!string.Equals(c.Action, action, StringComparison.Ordinal))
            {
                continue;
            }

            if (novelty is not null)
            {
                needBased = true;
                if (targetTypeId is not null && Exploring(inhabitant, c, curve, novelty, targetTypeId) > 0)
                {
                    return true;
                }

                continue;
            }

            // A consideration that yields to the needs (yields_to_needs, B2-6 R14) counts like a need-based
            // one: it suggests its action only while the needs leave room for it.
            var yields = c.YieldsToNeeds ?? false;
            if (c.Need is null && !yields)
            {
                continue;
            }

            needBased = true;
            if (c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c) * (yields ? Calm(inhabitant) : 1.0) > 0)
            {
                return true;
            }
        }

        return !needBased;
    }

    /// <summary>Whether an action serves a need: one of its considerations names that need (data).</summary>
    public bool Serves(string action, string need) =>
        _considerations.Any(c => string.Equals(c.Data.Action, action, StringComparison.Ordinal) && string.Equals(c.Data.Need, need, StringComparison.Ordinal));

    /// <summary>The key a search is kept under (its need, else its action).</summary>
    public static string SearchKey(Consideration consideration)
    {
        ArgumentNullException.ThrowIfNull(consideration);
        return consideration.Need ?? consideration.Action;
    }

    /// <summary>All options with their scores, best first; ties in option order.</summary>
    public IReadOnlyList<ScoredOption> Score(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived) =>
        Score(inhabitant, perceived, new HashSet<string>(StringComparer.Ordinal), null);

    /// <summary>
    /// All options with their scores, best first. A consideration with a search action also offers looking
    /// further away while the inhabitant believes some kind of thing helps, scored urgency x surest such
    /// belief x search_weight, so a weak target in sight competes fairly. Searches in
    /// <paramref name="givenUp"/> are not offered; <paramref name="searching"/> receives the keys whose
    /// search is still called for (need not met, nothing in sight that seems as good as searching).
    /// </summary>
    public IReadOnlyList<ScoredOption> Score(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived, IReadOnlySet<string> givenUp, ISet<string>? searching)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(perceived);
        ArgumentNullException.ThrowIfNull(givenUp);
        var options = new List<ScoredOption>();
        foreach (var (c, curve, novelty) in _considerations)
        {
            if (novelty is not null)
            {
                // Exploring (TL #19921): every thing in sight, by how little the inhabitant knows of its kind.
                foreach (var obj in perceived)
                {
                    var score = Exploring(inhabitant, c, curve, novelty, obj.TypeId) * DistanceFactor(inhabitant.Position, obj.Position);
                    if (score > 0)
                    {
                        options.Add(new ScoredOption(c.Action, obj.Id, score));
                    }
                }

                continue;
            }

            var urgency = c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c);
            if (c.YieldsToNeeds ?? false)
            {
                // B2-6 R14: gives way to pressing needs, like exploring by novelty.
                urgency *= Calm(inhabitant);
            }

            if (c.TargetProperty is null)
            {
                options.Add(new ScoredOption(c.Action, null, urgency));
                continue;
            }

            var bestInSight = 0.0;
            foreach (var obj in perceived)
            {
                var belief = BeliefFactor(inhabitant, obj.TypeId, c.TargetProperty) * AvoidFactor(inhabitant, obj.TypeId, c.AvoidProperties);
                if (belief > 0)
                {
                    var score = urgency * belief * DistanceFactor(inhabitant.Position, obj.Position);
                    options.Add(new ScoredOption(c.Action, obj.Id, score));
                    bestInSight = Math.Max(bestInSight, score);
                }
            }

            var known = SurestHelpfulBelief(inhabitant, c.TargetProperty, c.AvoidProperties);
            if (c.SearchAction is { } search && urgency > 0 && known > 0)
            {
                // The inhabitant knows of a kind of thing that would help: the need's urgency drives looking
                // for it further away (the data names the action), instead of waiting where nothing turns up.
                var key = SearchKey(c);
                var score = urgency * known * Weights.SearchWeight;
                if (score > bestInSight)
                {
                    searching?.Add(key);
                }

                if (!givenUp.Contains(key))
                {
                    options.Add(new ScoredOption(search, null, score) { SearchFor = key });
                }
            }
        }

        return [.. options
            .Select((o, index) => (o, index))
            .OrderByDescending(x => x.o.Score)
            .ThenBy(x => x.o.Action, StringComparer.Ordinal)
            .ThenBy(x => x.o.TargetId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.index)
            .Select(x => x.o)];
    }

    /// <summary>
    /// Belief that an object of <paramref name="typeId"/> has <paramref name="property"/>: the confidence
    /// of a positive belief, 0 for a negative belief, and the data value for "not known yet" (trying
    /// unknown things is how inhabitants learn without prior knowledge).
    /// </summary>
    public double BeliefFactor(Inhabitant inhabitant, string typeId, string property)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.Beliefs.TryGet(typeId, property, out var belief)
            ? (belief.Value ? belief.Confidence : 0.0)
            : Weights.UnknownTargetBelief;
    }

    /// <summary>
    /// How new a type is to the inhabitant: 1 - its surest belief about the type (any property, any
    /// source); 1 = it knows nothing about it, 0 = it is sure of something.
    /// </summary>
    public static double Novelty(Inhabitant inhabitant, string typeId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var surest = 0.0;
        foreach (var (_, belief) in inhabitant.Beliefs.About(typeId))
        {
            surest = Math.Max(surest, belief.Confidence);
        }

        return 1.0 - surest;
    }

    /// <summary>
    /// How calm the inhabitant's needs are for exploring: 0 while a need is below its neglect threshold,
    /// else 1 - the highest urgency (weight x curve) of a consideration for a need that can be neglected.
    /// </summary>
    public double Calm(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var pressing = 0.0;
        foreach (var (c, curve, _) in _considerations)
        {
            if (c.Need is not { } need || !_neglectBelow.TryGetValue(need, out var below))
            {
                continue;
            }

            var value = inhabitant.Needs.GetValueOrDefault(need, 1.0);
            if (value < below)
            {
                return 0.0;
            }

            pressing = Math.Max(pressing, c.Weight * curve.At(value));
        }

        return Math.Clamp(1.0 - pressing, 0.0, 1.0);
    }

    /// <summary>Score of an exploring consideration on a type (without distance).</summary>
    private double Exploring(Inhabitant inhabitant, Consideration c, Curve curve, Curve novelty, string typeId) =>
        c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c)
            * novelty.At(Novelty(inhabitant, typeId)) * Calm(inhabitant);

    /// <summary>The trait's value (0 if the inhabitant lacks it), or 1 without a trait.</summary>
    private static double TraitFactor(Inhabitant inhabitant, Consideration c) =>
        c.Trait is { } trait ? inhabitant.Traits.GetValueOrDefault(trait, 0.0) : 1.0;

    /// <summary>
    /// The surest belief that some type has the property, times what the inhabitant would avoid in it
    /// (its own beliefs only; 0 = it knows of nothing that helps).
    /// </summary>
    private static double SurestHelpfulBelief(Inhabitant inhabitant, string property, IEnumerable<string> avoid) =>
        inhabitant.Beliefs.All
            .Where(b => b.PropertyId == property && b.Belief.Value)
            .Select(b => b.Belief.Confidence * AvoidFactor(inhabitant, b.TypeId, avoid))
            .DefaultIfEmpty(0.0)
            .Max();

    /// <summary>For each avoided property the inhabitant believes the type has: factor 1 - confidence.</summary>
    private static double AvoidFactor(Inhabitant inhabitant, string typeId, IEnumerable<string> avoid)
    {
        var factor = 1.0;
        foreach (var property in avoid)
        {
            if (inhabitant.Beliefs.TryGet(typeId, property, out var belief) && belief.Value)
            {
                factor *= 1.0 - belief.Confidence;
            }
        }

        return factor;
    }

    private double DistanceFactor(Position from, Position to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        return 1.0 - (Weights.DistanceFalloff * Math.Min(1.0, distance / Weights.PerceptionRadiusM));
    }
}
