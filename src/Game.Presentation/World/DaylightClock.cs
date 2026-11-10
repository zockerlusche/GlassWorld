using GlassWorld.Contracts;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The light follows the world clock (B2-5, A1367): the day phases of the core (dawn from sunrise for the
/// twilight, day, dusk from sunset for the twilight, night; times per season from <c>content/world/time.json</c>)
/// are blended by the continuous time of day, <c>blend_minutes</c> of <c>content/look/daylight.json</c> on each
/// side of a phase change. The time includes the fraction of the running tick, so fast forward stays smooth.
/// Presentation only: reads the calendar, never core state.
/// </summary>
public static class DaylightClock
{
    /// <summary>Value of <c>day_phase</c> in <c>content/look/world.json</c> that follows the clock (any other value is a fixed phase).</summary>
    public const string FollowClock = "clock";

    /// <summary>The blend at a continuous time of day: <paramref name="T"/> = 0 shows <paramref name="From"/>, 1 <paramref name="To"/>.</summary>
    /// <param name="From">Phase blended from (the one before the change).</param>
    /// <param name="To">Phase blended to (equal to <paramref name="From"/> outside a blend).</param>
    /// <param name="T">Blend weight 0..1.</param>
    public readonly record struct Blend(string From, string To, double T);

    /// <summary>The light at <paramref name="tick"/> plus <paramref name="tickFraction"/> (0..1) of the next tick.</summary>
    public static DaylightMix At(LookDaylight daylight, WorldCalendar calendar, long tick, double tickFraction)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        var b = BlendAt(daylight, calendar, tick, tickFraction);
        var from = DaylightMix.Of(daylight, b.From);
        return b.From == b.To ? from : from.Lerp(DaylightMix.Of(daylight, b.To), b.T);
    }

    /// <summary>Which phases blend at <paramref name="tick"/> plus <paramref name="tickFraction"/>, with the season's sunrise and sunset.</summary>
    public static Blend BlendAt(LookDaylight daylight, WorldCalendar calendar, long tick, double tickFraction)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        ArgumentNullException.ThrowIfNull(calendar);
        var settings = calendar.Settings;
        var season = settings.Seasons.ElementAt(calendar.At(tick).SeasonIndex);
        var minute = ((tick % calendar.TicksPerDay) + Math.Clamp(tickFraction, 0, 1)) * 60.0 / settings.TicksPerHour;
        return BlendAt(minute, settings.HoursPerDay * 60, season.SunriseMinute, season.SunsetMinute, settings.TwilightMinutes, daylight.BlendMinutes);
    }

    /// <summary>
    /// Which phases blend at <paramref name="minuteOfDay"/> (continuous). Each phase change blends over
    /// <paramref name="blendMinutes"/> before and after it, at most half of the phase on that side, so the
    /// blend is continuous over the whole day and every phase is reached at least at its middle.
    /// </summary>
    public static Blend BlendAt(double minuteOfDay, int minutesPerDay, int sunriseMinute, int sunsetMinute, int twilightMinutes, double blendMinutes)
    {
        // The core's phases in the order of the day, starting at sunrise (WorldCalendar.PhaseOf).
        var phases = new List<(string Phase, double Start, double Length)>();
        void Add(DayPhase phase, double start, double end)
        {
            if (end > start)
            {
                phases.Add((WorldFeed.Phase(phase), start, end - start));
            }
        }

        Add(DayPhase.Dawn, sunriseMinute, sunriseMinute + twilightMinutes);
        Add(DayPhase.Day, sunriseMinute + twilightMinutes, sunsetMinute);
        Add(DayPhase.Dusk, sunsetMinute, sunsetMinute + twilightMinutes);
        Add(DayPhase.Night, sunsetMinute + twilightMinutes, sunriseMinute + minutesPerDay);

        var x = sunriseMinute + Modulo(minuteOfDay - sunriseMinute, minutesPerDay);
        var i = phases.FindLastIndex(p => p.Start <= x);
        var (phase, start, length) = phases[Math.Max(i, 0)];
        var previous = phases[(Math.Max(i, 0) + phases.Count - 1) % phases.Count];
        var next = phases[(Math.Max(i, 0) + 1) % phases.Count];
        double Half((string Phase, double Start, double Length) p) => Math.Min(Math.Max(blendMinutes, 0), p.Length / 2);

        var (half, before, after) = (Half((phase, start, length)), Half(previous), Half(next));
        if (x < start + half)
        {
            return new Blend(previous.Phase, phase, (x - (start - before)) / (before + half));
        }

        var end = start + length;
        return x > end - half
            ? new Blend(phase, next.Phase, (x - (end - half)) / (half + after))
            : new Blend(phase, phase, 0);
    }

    private static double Modulo(double value, double divisor) => ((value % divisor) + divisor) % divisor;
}
