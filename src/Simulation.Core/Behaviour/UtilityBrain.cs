using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>An option the routine layer can choose.</summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Target object id or null.</param>
/// <param name="Score">Utility score (higher is better).</param>
public sealed record ScoredOption(string Action, string? TargetId, double Score);

/// <summary>
/// The routine layer (utility AI): scores the options the core allows with weights and curves from data
/// (content/behaviour/utility.json). It sees only the inhabitant's BELIEFS, never the truth. Ties are
/// broken by option order (action, then target id), so the choice is deterministic.
/// </summary>
public sealed class UtilityBrain
{
    private readonly (Consideration Data, Curve Curve)[] _considerations;

    /// <summary>Creates the brain and checks ids against the action rules and needs.</summary>
    public UtilityBrain(UtilityWeights weights, ActionRules actions, IReadOnlySet<string> needs, IReadOnlySet<string> properties)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(actions);
        Weights = weights;
        _considerations = [.. weights.Considerations.Select(c => (c, new Curve(c.Curve)))];
        foreach (var (c, _) in _considerations)
        {
            _ = actions.Timing(c.Action);
            if (c.SearchAction is { } search)
            {
                _ = actions.Timing(search);
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

    /// <summary>All options with their scores, best first; ties in option order.</summary>
    public IReadOnlyList<ScoredOption> Score(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(perceived);
        var options = new List<ScoredOption>();
        foreach (var (c, curve) in _considerations)
        {
            var urgency = c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0));
            if (c.TargetProperty is null)
            {
                options.Add(new ScoredOption(c.Action, null, urgency));
                continue;
            }

            var found = false;
            foreach (var obj in perceived)
            {
                var belief = BeliefFactor(inhabitant, obj.TypeId, c.TargetProperty) * AvoidFactor(inhabitant, obj.TypeId, c.AvoidProperties);
                if (belief > 0)
                {
                    options.Add(new ScoredOption(c.Action, obj.Id, urgency * belief * DistanceFactor(inhabitant.Position, obj.Position)));
                    found = true;
                }
            }

            if (!found && c.SearchAction is { } search && urgency > 0 && KnowsSomethingThatHelps(inhabitant, c.TargetProperty, c.AvoidProperties))
            {
                // Nothing in sight seems to help, but the inhabitant knows of a kind of thing that would:
                // the need's urgency drives looking for it further away (the data names the action),
                // instead of waiting where nothing will turn up.
                options.Add(new ScoredOption(search, null, urgency));
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

    /// <summary>Whether the inhabitant believes some type has the property and none it would avoid (its own beliefs only).</summary>
    private static bool KnowsSomethingThatHelps(Inhabitant inhabitant, string property, IEnumerable<string> avoid) =>
        inhabitant.Beliefs.All.Any(b => b.PropertyId == property && b.Belief.Value && b.Belief.Confidence > 0 && AvoidFactor(inhabitant, b.TypeId, avoid) > 0);

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
