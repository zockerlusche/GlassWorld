using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Lying down to sleep (W1695) without Godot: whether a clip is played lying, where the hips of a figure lying
/// on its back are (behind its feet, the figure faces +Z at yaw 0) and the height of its feet so the body rests
/// on the ground under the hips. Data: <c>content/look/figures.json</c> (lying).
/// </summary>
public static class LyingPose
{
    /// <summary>Whether the library clip <paramref name="clipId"/> is played lying.</summary>
    public static bool IsLying(FigureLying lying, string clipId)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return lying.Clips.Contains(clipId, StringComparer.Ordinal);
    }

    /// <summary>Where the hips lie on the ground: <c>hip_m</c> × scale behind the feet at (x, z) for the figure's yaw.</summary>
    public static (double X, double Z) Hips(FigureLying lying, double x, double z, double yaw, double scale)
    {
        ArgumentNullException.ThrowIfNull(lying);
        var d = lying.HipM * scale;
        return (x - (Math.Sin(yaw) * d), z - (Math.Cos(yaw) * d));
    }

    /// <summary>
    /// Height of the figure's origin (its feet) while it lies down by <paramref name="share"/> (0 standing, 1
    /// lying): from the ground under the feet to the ground under the hips plus <c>lift_m</c> × scale.
    /// </summary>
    public static double Height(FigureLying lying, double groundAtFeet, double groundAtHips, double scale, double share)
    {
        ArgumentNullException.ThrowIfNull(lying);
        var s = Math.Clamp(share, 0, 1);
        return groundAtFeet + (((groundAtHips + (lying.LiftM * scale)) - groundAtFeet) * s);
    }

    /// <summary>The tilt about the figure's left-right axis in radians at <paramref name="share"/>.</summary>
    public static double Pitch(FigureLying lying, double share)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return lying.PitchDeg * Math.PI / 180 * Math.Clamp(share, 0, 1);
    }
}
