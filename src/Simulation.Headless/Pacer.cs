namespace GlassWorld.Simulation.Headless;

/// <summary>
/// Real-time pacing of the headless loop. The core never waits for the AI (B2487): like in the game, the
/// world runs on and answers take effect at the tick they are received. What differs from the game is
/// only how fast real time passes per tick:
/// <list type="bullet">
/// <item><c>--tick-ms N</c> (N &gt; 0): every tick takes at least N ms (833 ≈ the game at normal speed).</item>
/// <item><c>--tick-ms 0</c> (default): as fast as possible while no AI call is running. While a question
/// or job is out (live AI), ticks pass at the game's fastest speed (normal speed × the largest
/// fast-forward factor of content/world/time.json, x16 = 52 ms per tick), so the run does not race
/// through days while answers are on their way: the answer takes effect after the same number of ticks
/// as in the game at that speed. With the mock nothing is ever running, so the run is as fast as before.</item>
/// </list>
/// </summary>
internal sealed class Pacer(TimeSpan fixedInterval, TimeSpan busyInterval, Func<TimeSpan> clock, Action<TimeSpan> sleep)
{
    private TimeSpan? _last;

    /// <summary>Real time per tick for the given state.</summary>
    public TimeSpan IntervalFor(bool busy) =>
        fixedInterval > TimeSpan.Zero ? fixedInterval : busy ? busyInterval : TimeSpan.Zero;

    /// <summary>Waits until the next tick is due.</summary>
    public void BeforeTick(bool busy)
    {
        var interval = IntervalFor(busy);
        var now = clock();
        if (interval > TimeSpan.Zero && _last is { } last && last + interval > now)
        {
            sleep(last + interval - now);
            now = last + interval;
        }

        _last = now;
    }
}
