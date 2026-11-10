namespace GlassWorld.Presentation.World;

/// <summary>
/// The clock of the water (W1724): the time the water shaders move by. It runs in real time and stands
/// still while the game is paused (so flow lines, shimmer and waves stand still with the figures), and it
/// wraps at its period. The shaders never use the seconds raw: every cycle is rounded to a whole number of
/// rounds per period (<see cref="Phase"/>, <c>water_cycle</c> in <c>water_time.gdshaderinc</c>), and every
/// scrolling pattern repeats after the steps it scrolls per period, so the wrap does not show. Handed to the shaders as the global parameter <c>water_time</c>. Presentation only.
/// </summary>
public sealed class WaterClock
{
    /// <summary>Period for callers without look data (the look prototype): one hour.</summary>
    public const double DefaultPeriodSeconds = 3600;

    /// <summary>Creates the clock at 0 with a period in seconds (at least one second).</summary>
    public WaterClock(double periodSeconds = DefaultPeriodSeconds)
    {
        if (!(periodSeconds >= 1) || double.IsInfinity(periodSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(periodSeconds), periodSeconds, "The period of the water clock must be at least one second.");
        }

        PeriodSeconds = periodSeconds;
    }

    /// <summary>Seconds after which the clock starts again at 0.</summary>
    public double PeriodSeconds { get; }

    /// <summary>The time now, in [0, <see cref="PeriodSeconds"/>).</summary>
    public double Seconds { get; private set; }

    /// <summary>One frame: <paramref name="realSeconds"/> since the last one, 0 while the game is paused.</summary>
    public void Update(double realSeconds)
    {
        if (realSeconds > 0 && !double.IsInfinity(realSeconds))
        {
            Seconds = (Seconds + realSeconds) % PeriodSeconds;
        }
    }

    /// <summary>Share of the period that has passed, 0..1 (<c>water_progress</c> of the shaders).</summary>
    public double Progress => Seconds / PeriodSeconds;

    /// <summary>
    /// Whole rounds per period of a cycle with about <paramref name="hz"/> rounds per second, as the
    /// shaders round it (<c>water_rounds</c>): at least one for any rate above 0, so nothing stands still
    /// under a short period; none without a rate.
    /// </summary>
    public double Rounds(double hz) => hz > 0 ? Math.Max(Math.Floor((hz * PeriodSeconds) + 0.5), 1) : 0;

    /// <summary>
    /// Phase 0..1 of a cycle with about <paramref name="hz"/> rounds per second, exactly as the shaders
    /// compute it: it runs on without a jump when the clock wraps.
    /// </summary>
    public double Phase(double hz)
    {
        var turns = Rounds(hz) * Progress;
        return turns - Math.Floor(turns);
    }
}

/// <summary>
/// Where the wave crests are (W1724, review GW3 #199). The crests lie on grids that are fixed in the world
/// and never turn; the wind only pushes them along. Per layer this keeps the shift of its grid: direction
/// times speed times real time, summed up frame by frame, so a turning wind bends the path of the crests
/// but never slides or reshuffles them. The direction is eased (it turns the short way over
/// <c>turn_real_seconds</c>), a wind without a direction keeps the last one, and a pause (0 seconds) holds
/// everything. A shift wraps after <see cref="PeriodCells"/> places of its grid, and the shader repeats
/// its pattern after as many, so the wrap does not show either. Handed to the shaders as the global
/// parameters <c>water_wave_shift</c> and <c>water_waves</c>. Presentation only.
/// </summary>
public sealed class WaveDrift
{
    /// <summary>Places after which the crest pattern of the shader repeats (<c>WAVE_PERIOD</c> in <c>water_waves.gdshaderinc</c>).</summary>
    public const int PeriodCells = 64;

    private readonly (double SpacingM, double SpeedMPerS)[] _layers;
    private readonly (double X, double Z)[] _shifts;
    private readonly double _turnSeconds;
    private bool _started;

    /// <summary>Creates the drift for layers given by the size of a place of their grid and their speed.</summary>
    /// <param name="turnRealSeconds">Real seconds over which the direction follows a turned wind (0 = at once).</param>
    /// <param name="layers">Per layer: metres per place of its grid (above 0) and metres per second with the wind.</param>
    public WaveDrift(double turnRealSeconds, params (double SpacingM, double SpeedMPerS)[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        foreach (var (spacing, speed) in layers)
        {
            if (!(spacing > 0) || double.IsInfinity(spacing) || !double.IsFinite(speed))
            {
                throw new ArgumentOutOfRangeException(nameof(layers), $"A wave layer needs a spacing above 0 and a finite speed (got {spacing} m, {speed} m/s).");
            }
        }

        _layers = [.. layers];
        _shifts = new (double X, double Z)[_layers.Length];
        _turnSeconds = Math.Max(turnRealSeconds, 0);
    }

    /// <summary>x of the eased unit direction the crests travel in; never zero.</summary>
    public double DirectionX { get; private set; }

    /// <summary>z of the eased unit direction the crests travel in; never zero.</summary>
    public double DirectionZ { get; private set; } = 1;

    /// <summary>Per layer the shift of its grid in metres, each coordinate in [0, spacing times <see cref="PeriodCells"/>).</summary>
    public IReadOnlyList<(double X, double Z)> Shifts => _shifts;

    /// <summary>
    /// One frame: the direction the wind blows to on the ground (any length; no length = keep the last
    /// one) and the real seconds since the last frame (0 while the game is paused). The first direction
    /// is taken at once.
    /// </summary>
    public void Update(double windX, double windZ, double realSeconds)
    {
        var dt = realSeconds > 0 && double.IsFinite(realSeconds) ? realSeconds : 0;
        var length = Math.Sqrt((windX * windX) + (windZ * windZ));
        if (length > 1e-9 && double.IsFinite(length))
        {
            var (tx, tz) = (windX / length, windZ / length);
            if (!_started)
            {
                (_started, DirectionX, DirectionZ) = (true, tx, tz);
            }
            else if (dt > 0)
            {
                // Turn the short way by the share of the angle this frame covers; a unit vector stays one.
                var angle = Math.Atan2((DirectionX * tz) - (DirectionZ * tx), (DirectionX * tx) + (DirectionZ * tz));
                var step = angle * (_turnSeconds > 0 ? 1 - Math.Exp(-dt / _turnSeconds) : 1);
                var (cos, sin) = (Math.Cos(step), Math.Sin(step));
                (DirectionX, DirectionZ) = ((DirectionX * cos) - (DirectionZ * sin), (DirectionX * sin) + (DirectionZ * cos));
                var norm = Math.Sqrt((DirectionX * DirectionX) + (DirectionZ * DirectionZ));
                (DirectionX, DirectionZ) = (DirectionX / norm, DirectionZ / norm);
            }
        }

        if (dt <= 0)
        {
            return;
        }

        for (var i = 0; i < _layers.Length; i++)
        {
            var period = _layers[i].SpacingM * PeriodCells;
            var way = _layers[i].SpeedMPerS * dt;
            _shifts[i] = (Wrap(_shifts[i].X + (DirectionX * way), period), Wrap(_shifts[i].Z + (DirectionZ * way), period));
        }
    }

    private static double Wrap(double value, double period)
    {
        var wrapped = value % period;
        wrapped = wrapped < 0 ? wrapped + period : wrapped;

        // A tiny negative rest plus the period rounds to the period itself: that is the start again.
        return wrapped >= period ? 0 : wrapped;
    }
}
