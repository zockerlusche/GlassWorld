using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The shown sea level follows the tide of the snapshot (B2-5 R15): the core gives the level per tick, in
/// steps; the presentation moves the sea surface towards it in real time (an exponential approach with
/// the time constant <c>tide_follow_real_seconds</c> of <c>content/look/terrain.json</c>), so the water
/// glides at every game speed, also when several ticks fall into one frame (x16) or a frame brings none.
/// The level is in height units against the mean sea level: 0 without tides (amplitude 0) and without an
/// island, so the sea then lies exactly where it always did. The first reading (start of a world, a load)
/// is shown at once; while the game is paused (0 real seconds) the water stands still; once the level is
/// within <see cref="ArriveUnits"/> of a reading it equals it exactly and rests. Presentation only:
/// it reads the tide reading, never core state.
/// </summary>
public sealed class TideFollower
{
    /// <summary>
    /// Distance to the reading, in height units, below which the level counts as arrived: far below
    /// anything visible (a height unit is a fraction of a metre), a numeric tolerance and not a look value.
    /// </summary>
    public const double ArriveUnits = 1e-4;

    private readonly double _followSeconds;
    private bool _started;

    /// <summary>Creates the follower; <paramref name="followRealSeconds"/> 0 shows every reading at once.</summary>
    public TideFollower(double followRealSeconds) => _followSeconds = Math.Max(followRealSeconds, 0);

    /// <summary>The shown water level against the mean sea level, in height units.</summary>
    public double Units { get; private set; }

    /// <summary>
    /// The level of a reading in height units, from its position on the curve (finer than the reading's
    /// tenths, so the water has no visible steps); 0 without a reading or without tides.
    /// </summary>
    public static double Level(TideReading? reading) => reading is null ? 0 : AtPermille(reading.Amplitude, reading.LevelPermille);

    /// <summary>The level at a position on the curve (−1000 lowest … 1000 highest) for an amplitude in height units.</summary>
    public static double AtPermille(int amplitude, int levelPermille) => amplitude * Math.Clamp(levelPermille, -1000, 1000) / 1000.0;

    /// <summary>Shows a level at once (start of a world, a load, the screenshot tool).</summary>
    public double Jump(double units)
    {
        _started = true;
        Units = units;
        return Units;
    }

    /// <summary>
    /// One frame: <paramref name="reading"/> is the tide of the snapshot (null without an island),
    /// <paramref name="realSeconds"/> the real time since the last frame (0 while the game is paused).
    /// Returns the level to show.
    /// </summary>
    public double Update(TideReading? reading, double realSeconds)
    {
        var target = Level(reading);
        if (!_started)
        {
            return Jump(target);
        }

        if (realSeconds <= 0 || Units.Equals(target))
        {
            return Units;
        }

        // The share of the way covered in this frame; it depends on the real time alone, so the result is
        // the same whether the time comes in many short frames or a few long ones.
        Units = _followSeconds <= 0 ? target : Units + ((target - Units) * (1 - Math.Exp(-realSeconds / _followSeconds)));
        if (Math.Abs(target - Units) < ArriveUnits)
        {
            // An exponential approach never arrives by itself: the last invisible rest is taken at once, so
            // the level then equals the reading exactly and nothing moves any more until the reading changes.
            Units = target;
        }

        return Units;
    }
}
