using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// How a figure moves between two core ticks, without Godot (B2 R17, R28): it goes in a straight line and
/// at an even pace from where it is to the position of the latest tick over the real time that tick lasts,
/// turns towards where it walks at a rate from the data, and knows its speed in game time (distance per
/// game second, for the gait) and on screen (distance per real second, for the playback of the gait).
/// Presentation only: it reads core positions and never writes them.
/// </summary>
public sealed class FigureMotion
{
    private readonly double _turnRateRad;
    private readonly int _turnBackAfterSteps;
    private int _backSteps;
    private Point3 _from;
    private Point3 _to;
    private double _elapsed;
    private double _duration;
    private double _targetYaw;

    /// <summary>Creates the motion of a figure standing at <paramref name="start"/>.</summary>
    /// <param name="start">Start position in metres.</param>
    /// <param name="turnRateDegS">How fast the figure turns, degrees per real second.</param>
    /// <param name="turnBackAfterSteps">How many backward steps in a row turn a walking figure round (data, at least 1).</param>
    public FigureMotion(Point3 start, double turnRateDegS, int turnBackAfterSteps = 1)
    {
        _turnRateRad = turnRateDegS * Math.PI / 180;
        _turnBackAfterSteps = Math.Max(1, turnBackAfterSteps);
        Position = _from = _to = start;
    }

    /// <summary>Where the figure is now (interpolated).</summary>
    public Point3 Position { get; private set; }

    /// <summary>Where the latest tick put the inhabitant.</summary>
    public Point3 Target => _to;

    /// <summary>Facing in radians around the up axis (atan2 of the walk direction x, z), eased.</summary>
    public double Yaw { get; private set; }

    /// <summary>Ground distance in metres the core moved the inhabitant in the latest tick(s).</summary>
    public double StepM { get; private set; }

    /// <summary>Speed in game time of the latest tick(s), cm per game second (chooses the gait).</summary>
    public double GameSpeedCmS { get; private set; }

    /// <summary>Speed on screen of the current stretch, cm per real second (scales the gait playback).</summary>
    public double ScreenSpeedCmS { get; private set; }

    /// <summary>Game seconds one tick lasts (calendar settings: an hour has 3600 game seconds).</summary>
    public static double GameSecondsPerTick(WorldTimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return 3600.0 / time.TicksPerHour;
    }

    /// <summary>Real seconds one tick lasts at a time factor (1 = normal speed).</summary>
    public static double RealSecondsPerTick(WorldTimeSettings time, double speed)
    {
        ArgumentNullException.ThrowIfNull(time);
        return speed > 0 ? time.NormalRealSecondsPerDay / ((double)time.TicksPerHour * time.HoursPerDay * speed) : double.PositiveInfinity;
    }

    /// <summary>Speed in cm/s for a distance in metres over some seconds (0 without time).</summary>
    public static double SpeedCmS(double distanceM, double seconds) =>
        seconds > 0 && !double.IsInfinity(seconds) ? distanceM * 100 / seconds : 0;

    /// <summary>How far along the stretch the figure is after <paramref name="elapsed"/> of <paramref name="duration"/> seconds (0..1).</summary>
    public static double Fraction(double elapsed, double duration) =>
        duration > 0 ? Math.Clamp(elapsed / duration, 0, 1) : 1;

    /// <summary>
    /// The playback factor of the gait so its feet roughly match the on-screen speed: on-screen speed over
    /// the clip's ground speed at the figure's scale, kept within the data range (calm world).
    /// </summary>
    public static double GaitTimeScale(double screenSpeedCmS, double groundSpeedCmS, double figureScale, double min, double max)
    {
        var ground = groundSpeedCmS * figureScale;
        return ground > 0 ? Math.Clamp(screenSpeedCmS / ground, min, max) : Math.Clamp(1, min, max);
    }

    /// <summary>Turns from <paramref name="yaw"/> towards <paramref name="target"/> by at most <paramref name="maxStep"/> (radians, shortest way).</summary>
    public static double TurnTowards(double yaw, double target, double maxStep)
    {
        var diff = Math.IEEERemainder(target - yaw, 2 * Math.PI);
        return Math.IEEERemainder(Math.Abs(diff) <= maxStep ? yaw + diff : yaw + (Math.Sign(diff) * maxStep), 2 * Math.PI);
    }

    /// <summary>
    /// Takes the position of a new tick: the figure goes from where it is now to <paramref name="to"/>
    /// over <paramref name="ticks"/> × the real tick time. A step of at least <paramref name="minStepM"/>
    /// turns the figure towards it; while it walks, a step backwards (more than 90° from its facing, e.g. giving
    /// way on a narrow path, W1726) turns it only from the data's number of such steps in a row, so a figure
    /// that steps back and forth keeps facing the way it goes.
    /// </summary>
    public void Retarget(Point3 to, int ticks, double gameSecondsPerTick, double realSecondsPerTick, double minStepM)
    {
        var walking = StepM >= minStepM;
        StepM = Flat(_to, to);
        GameSpeedCmS = SpeedCmS(StepM, ticks * gameSecondsPerTick);
        _from = Position;
        _to = to;
        _elapsed = 0;
        _duration = ticks * realSecondsPerTick;
        ScreenSpeedCmS = SpeedCmS(Flat(_from, _to), _duration);
        if (StepM >= minStepM && Flat(_from, _to) > 1e-6)
        {
            var yaw = Math.Atan2(_to.X - _from.X, _to.Z - _from.Z);
            var back = walking && Math.Abs(Math.IEEERemainder(yaw - _targetYaw, 2 * Math.PI)) > Math.PI / 2;
            _backSteps = back ? _backSteps + 1 : 0;
            if (!back || _backSteps >= _turnBackAfterSteps)
            {
                _targetYaw = yaw;
                _backSteps = 0;
            }
        }
    }

    /// <summary>
    /// Turns the standing figure's whole body towards a point (B2528, W1691: two inhabitants face each other
    /// while one shows, warns or teaches the other); ignored while it walks or when the point is where it stands.
    /// </summary>
    public void Face(double x, double z, double minStepM)
    {
        if (StepM < minStepM && ((x - _to.X) * (x - _to.X)) + ((z - _to.Z) * (z - _to.Z)) > 1e-12)
        {
            _targetYaw = Math.Atan2(x - _to.X, z - _to.Z);
        }
    }

    /// <summary>Turns the standing figure towards a facing (radians, like <see cref="Yaw"/>), e.g. to a voice; the next step turns it to where it walks.</summary>
    public void Face(double yaw) => _targetYaw = yaw;

    /// <summary>Puts the figure at <paramref name="at"/> at once (after a warm-up, a load): no stretch to walk.</summary>
    public void Place(Point3 at)
    {
        Position = _from = _to = at;
        _elapsed = _duration = 0;
        StepM = GameSpeedCmS = ScreenSpeedCmS = 0;
    }

    /// <summary>Advances the figure by real <paramref name="seconds"/>: along the stretch and round towards its facing.</summary>
    public void Advance(double seconds)
    {
        _elapsed += Math.Max(0, seconds);
        var f = Fraction(_elapsed, _duration);
        Position = new Point3(_from.X + ((_to.X - _from.X) * f), _from.Y + ((_to.Y - _from.Y) * f), _from.Z + ((_to.Z - _from.Z) * f));
        Yaw = TurnTowards(Yaw, _targetYaw, _turnRateRad * Math.Max(0, seconds));
    }

    private static double Flat(Point3 a, Point3 b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
}
