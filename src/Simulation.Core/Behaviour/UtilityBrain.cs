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
    private readonly (Consideration Data, Curve Curve)[] _considerations;

    /// <summary>
    /// Creates the brain and checks ids against the action rules and needs. A search action must be an
    /// action without a target (<paramref name="targetlessActions"/>, the vocabulary's target none; without
    /// it: an action no rule needs a target for) on a consideration with a target property.
    /// </summary>
    public UtilityBrain(UtilityWeights weights, ActionRules actions, IReadOnlySet<string> needs, IReadOnlySet<string> properties, IReadOnlySet<string>? targetlessActions = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(actions);
        Weights = weights;
        if (weights.SearchWeight > weights.UnknownTargetBelief)
        {
            // Trying something unknown close by is how inhabitants learn; searching far away must not outbid it.
            throw new InvalidDataException("Utility weights: search_weight must not exceed unknown_target_belief.");
        }

        _considerations = [.. weights.Considerations.Select(c => (c, new Curve(c.Curve)))];
        foreach (var (c, _) in _considerations)
        {
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
        foreach (var (c, curve) in _considerations)
        {
            var urgency = c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0));
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
