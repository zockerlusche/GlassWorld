using System.Runtime.CompilerServices;
using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>
/// D1086 (B2728): a trait scales how often or how readily something happens - never what (PR2). The factor is the
/// data's point table over the inhabitant's trait value; no curve or no such trait = factor 1.
/// </summary>
public static class TraitFactor
{
    private static readonly ConditionalWeakTable<TraitCurve, Curve> Curves = [];

    /// <summary>The factor for traits <paramref name="traits"/> (an inhabitant's), or 1.</summary>
    public static double Of(TraitCurve? curve, IReadOnlyDictionary<string, double> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        return curve is not null && traits.TryGetValue(curve.Trait, out var value)
            ? Curves.GetValue(curve, c => new Curve(c.Curve)).At(value)
            : 1.0;
    }
}
