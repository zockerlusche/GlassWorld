using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Gift suggestions without the AI (F5238 fallback, B6 R16): for a need, the object types that truly
/// help by the data (a consideration with this need names the target property; types with a property
/// to avoid are left out, e.g. salt water for thirst). The core still checks every gift.
/// </summary>
public static class GiftFallback
{
    /// <summary>Up to <paramref name="max"/> type ids for a need, in catalog order.</summary>
    public static IReadOnlyList<string> For(string need, UtilityWeights weights, ObjectCatalog catalog, int max)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(catalog);
        var helps = weights.Considerations.Where(c => c.Need == need && c.TargetProperty is not null).ToList();
        return [.. catalog.TypeIds
            .Where(type => helps.Any(c => catalog.HasProperty(type, c.TargetProperty!) && !c.AvoidProperties.Any(p => catalog.HasProperty(type, p))))
            .Take(max)];
    }
}
