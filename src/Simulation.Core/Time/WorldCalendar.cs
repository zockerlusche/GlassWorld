using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Simulation.Time;

/// <summary>Phase of the day (same vocabulary as <c>time_of_day</c> in the decision contract).</summary>
public enum DayPhase
{
    /// <summary>From sunrise for the twilight duration.</summary>
    Dawn,

    /// <summary>Between dawn and sunset.</summary>
    Day,

    /// <summary>From sunset for the twilight duration.</summary>
    Dusk,

    /// <summary>The rest.</summary>
    Night,
}

/// <summary>The ids of the day phases as the vocabulary <c>time_of_day</c> spells them.</summary>
public static class DayPhases
{
    private static readonly string[] Ids = [.. Enum.GetValues<DayPhase>().Select(p => p.ToString().ToLowerInvariant())];

    /// <summary>The vocabulary id of a phase: its name in lower case (the same string every time, nothing is built).</summary>
    public static string IdOf(DayPhase phase) => Ids[(int)phase];
}

/// <summary>A point in world time, fully derived from the tick counter with integer arithmetic.</summary>
/// <param name="Tick">Ticks since the founding of the world.</param>
/// <param name="Year">World year, starting at 1.</param>
/// <param name="SeasonIndex">Index into the season cycle.</param>
/// <param name="SeasonId">Season id (vocabulary).</param>
/// <param name="DayOfSeason">Day within the season, starting at 1.</param>
/// <param name="Day">Absolute game day since founding, starting at 1.</param>
/// <param name="Hour">Hour of the day, 0-based.</param>
/// <param name="Minute">Minute of the hour, 0-based.</param>
/// <param name="Phase">Phase of the day for the current season.</param>
public readonly record struct WorldTime(
    long Tick, long Year, int SeasonIndex, string SeasonId, int DayOfSeason, long Day, int Hour, int Minute, DayPhase Phase);

/// <summary>
/// Converts ticks into world time without an earth calendar (counted from the founding). Integer
/// arithmetic only (determinism, ADR-0002). Settings come from <c>content/world/time.json</c>.
/// </summary>
public sealed class WorldCalendar
{
    private readonly SeasonSettings[] _seasons;

    /// <summary>Creates a calendar; checks rules the schema cannot express.</summary>
    public WorldCalendar(WorldTimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        _seasons = [.. settings.Seasons];
        TicksPerDay = (long)settings.TicksPerHour * settings.HoursPerDay;
        TicksPerSeason = TicksPerDay * settings.DaysPerSeason;
        TicksPerYear = TicksPerSeason * _seasons.Length;
        Check();
    }

    /// <summary>The settings in use.</summary>
    public WorldTimeSettings Settings { get; }

    /// <summary>Ticks per game day.</summary>
    public long TicksPerDay { get; }

    /// <summary>Ticks per season.</summary>
    public long TicksPerSeason { get; }

    /// <summary>Ticks per world year.</summary>
    public long TicksPerYear { get; }

    /// <summary>Loads the settings from JSON, validated against the <c>world-time-settings</c> schema.</summary>
    public static WorldCalendar FromJson(string json) => new(ContractSerializer.Deserialize<WorldTimeSettings>(json));

    /// <summary>World time at <paramref name="tick"/>.</summary>
    public WorldTime At(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        var dayIndex = tick / TicksPerDay;
        var minuteOfDay = (int)(tick % TicksPerDay * 60 / Settings.TicksPerHour);
        var seasonIndex = (int)(tick % TicksPerYear / TicksPerSeason);
        var season = _seasons[seasonIndex];
        return new WorldTime(
            tick,
            (tick / TicksPerYear) + 1,
            seasonIndex,
            season.Id,
            (int)(dayIndex % Settings.DaysPerSeason) + 1,
            dayIndex + 1,
            minuteOfDay / 60,
            minuteOfDay % 60,
            PhaseOf(season, minuteOfDay));
    }

    /// <summary>
    /// B2680: game minutes since the night phase began (sunset plus twilight of the season), or null outside the night.
    /// After midnight it counts on from the evening before.
    /// </summary>
    public int? MinutesSinceNightfall(long tick)
    {
        var time = At(tick);
        if (time.Phase != DayPhase.Night)
        {
            return null;
        }

        var nightfall = _seasons[time.SeasonIndex].SunsetMinute + Settings.TwilightMinutes;
        var minute = (time.Hour * 60) + time.Minute;
        return minute >= nightfall ? minute - nightfall : minute + (Settings.HoursPerDay * 60) - nightfall;
    }

    /// <summary>Real seconds per tick at normal speed (for the game clock only, never for simulation state).</summary>
    public double RealSecondsPerTickAtNormalSpeed => (double)Settings.NormalRealSecondsPerDay / TicksPerDay;

    private void Check()
    {
        var s = Settings;
        if (_seasons.Length == 0 || (60 % s.TicksPerHour != 0 && s.TicksPerHour % 60 != 0))
        {
            throw new InvalidDataException("World time: needs seasons, and ticks_per_hour must divide or be a multiple of 60.");
        }

        var minutesPerDay = s.HoursPerDay * 60;
        foreach (var season in _seasons)
        {
            if (season.SunriseMinute + s.TwilightMinutes > season.SunsetMinute
                || season.SunsetMinute + s.TwilightMinutes > minutesPerDay)
            {
                throw new InvalidDataException($"World time: season '{season.Id}' has inconsistent sunrise, sunset and twilight.");
            }
        }
    }

    private DayPhase PhaseOf(SeasonSettings season, int minuteOfDay)
    {
        var twilight = Settings.TwilightMinutes;
        if (minuteOfDay >= season.SunriseMinute && minuteOfDay < season.SunriseMinute + twilight)
        {
            return DayPhase.Dawn;
        }

        if (minuteOfDay >= season.SunriseMinute + twilight && minuteOfDay < season.SunsetMinute)
        {
            return DayPhase.Day;
        }

        return minuteOfDay >= season.SunsetMinute && minuteOfDay < season.SunsetMinute + twilight
            ? DayPhase.Dusk
            : DayPhase.Night;
    }
}
