using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// A1823 (B5-8, B2736 'Aussehen folgt der Staerke'): how big a glowing thing is drawn and how strong its light shines, from
/// the core's strength (permille) and whether it lies as embers. Presentation only.
/// </summary>
public static class FireLook
{
    /// <summary>The drawn size factor and the light's share for this strength; embers by their own data.</summary>
    public static (double Scale, double Light, bool Embers) Shown(int strengthPermille, bool embers, ObjectGlow glow)
    {
        ArgumentNullException.ThrowIfNull(glow);
        var share = Math.Clamp(strengthPermille, 0, 1000) / 1000.0;
        if (glow.MinScale is not { } least)
        {
            return (1.0, embers ? glow.EmbersEnergy ?? share : 1.0, embers);
        }

        if (embers)
        {
            return (glow.EmbersScale ?? least, glow.EmbersEnergy ?? share, true);
        }

        return (least + ((1 - least) * share), share, false);
    }
}
