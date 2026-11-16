namespace GlassWorld.Presentation.Audio;

/// <summary>
/// A1543 (D1237): how loud a burning thing crackles - the distance gain of its bus times its strength (permille);
/// embers by the data's share. Presentation only.
/// </summary>
public static class FireSound
{
    /// <summary>The gain (0..1) of a burning thing's loop.</summary>
    public static double Gain(double distanceGain, int strengthPermille, bool embers, double embersGain) =>
        Math.Clamp(distanceGain, 0, 1) * (embers ? Math.Clamp(embersGain, 0, 1) : Math.Clamp(strengthPermille, 0, 1000) / 1000.0);
}
