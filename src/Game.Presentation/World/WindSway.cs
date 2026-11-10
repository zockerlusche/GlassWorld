using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The wind the plants show (B2-5 R21): the wind strength of the core (0..1, <c>WeatherReading.Wind</c>)
/// smoothed over <c>smooth_real_seconds</c> of <c>content/look/nature.json</c> in real time, and the phase
/// of the swing, which runs faster in stronger wind but never jumps. Both are handed to the shaders as one
/// global parameter per frame. Real time, not game time: at x16 the core's wind changes sixteen times as
/// fast, the shown wind follows as calmly as at x1. Presentation only, never in the core hash.
/// </summary>
public sealed class WindSway
{
    private readonly NatureWind _data;
    private bool _started;

    /// <summary>Creates the wind from the nature data.</summary>
    public WindSway(NatureWind data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        var angle = double.DegreesToRadians(data.DirectionDeg);
        (DirectionX, DirectionZ) = (Math.Sin(angle), Math.Cos(angle));
    }

    /// <summary>Shown wind strength 0..1.</summary>
    public double Strength { get; private set; }

    /// <summary>Phase of the swing in radians, 0..2π (the shader uses whole multiples, so the wrap does not show).</summary>
    public double Phase { get; private set; }

    /// <summary>x of the unit direction the wind blows to on the ground.</summary>
    public double DirectionX { get; }

    /// <summary>z of the unit direction the wind blows to on the ground.</summary>
    public double DirectionZ { get; }

    /// <summary>Radians of phase per metre along the wind (the wave running over the land).</summary>
    public double WaveNumber => Math.Tau / _data.WaveLengthM;

    /// <summary>Share of the bend that is a steady lean (the rest swings).</summary>
    public double LeanShare => _data.LeanShare;

    /// <summary>
    /// One frame: <paramref name="wind"/> is the core's wind now (null without weather: still air),
    /// <paramref name="realSeconds"/> the real time since the last frame (0 while the game is paused, so
    /// the plants stand still with the figures). The first value is taken at once.
    /// </summary>
    public void Update(double? wind, double realSeconds)
    {
        var target = Math.Clamp(wind ?? 0, 0, 1);
        if (!_started)
        {
            (_started, Strength) = (true, target);
            return;
        }

        var dt = Math.Max(realSeconds, 0);
        Strength += (target - Strength) * (1 - Math.Exp(-dt / _data.SmoothRealSeconds));
        var swings = _data.SwingsPerSecondCalm + ((_data.SwingsPerSecondFull - _data.SwingsPerSecondCalm) * Strength);
        Phase = (Phase + (dt * swings * Math.Tau)) % Math.Tau;
    }

    /// <summary>
    /// How far the top of a plant with sway 1 m is moved along the wind at a place (metres), as the shader
    /// computes it: a steady lean plus a swing of two whole harmonics, both scaled by the strength.
    /// </summary>
    public double OffsetAt(double x, double z)
    {
        var phase = Phase - (((x * DirectionX) + (z * DirectionZ)) * WaveNumber);
        var swing = (0.7 * Math.Sin(phase)) + (0.3 * Math.Sin((2 * phase) + 1.3));
        return Strength * (LeanShare + ((1 - LeanShare) * swing));
    }
}
