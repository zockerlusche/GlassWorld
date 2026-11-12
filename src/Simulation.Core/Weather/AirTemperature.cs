using GlassWorld.Contracts;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Weather;

/// <summary>
/// The air temperature of a climate in tenths of a degree (B2 R13, B5 R1/R28), integers only:
/// base + season offset + daily part + weather offset + biome offsets. The daily part is the season's
/// hourly curve (points by hour, linear by the minute, around midnight) or, without a curve, the offset of
/// the time of day. During the first <c>season_transition_hours</c> of a season its part (season offset,
/// daily part, biome offsets) blends linearly from the previous season's part. Without the optional data
/// (curves, biomes, transition) the result is exactly base + season + time of day + weather.
/// The season order of the temperature table is the calendar's (checked at load when it matters).
/// </summary>
public sealed class AirTemperature
{
    private readonly int _base;
    private readonly int _minutesPerDay;
    private readonly long _transitionMinutes;
    private readonly string[] _seasons;
    private readonly Dictionary<string, int> _seasonOffsets;
    private readonly Dictionary<string, int> _weatherOffsets;
    private readonly Dictionary<string, int> _timeOffsets;
    private readonly Dictionary<string, (int Minute, int Offset)[]> _curves;
    private readonly Dictionary<(string Biome, string? Season), int> _biomeOffsets;

    /// <summary>Builds the temperature of a climate for a day of <paramref name="hoursPerDay"/> hours.</summary>
    public AirTemperature(ClimateTemperature data, int hoursPerDay)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(hoursPerDay, 1);
        _base = data.BaseTenthC;
        _minutesPerDay = hoursPerDay * 60;
        _transitionMinutes = (long)(data.SeasonTransitionHours ?? 0) * 60;
        _seasons = [.. data.Seasons.Select(s => s.Season)];
        _seasonOffsets = data.Seasons.ToDictionary(s => s.Season, s => s.OffsetTenthC, StringComparer.Ordinal);
        _weatherOffsets = data.Weathers.ToDictionary(w => w.Weather, w => w.OffsetTenthC, StringComparer.Ordinal);
        _timeOffsets = data.TimesOfDay.ToDictionary(t => t.TimeOfDay, t => t.OffsetTenthC, StringComparer.Ordinal);
        _curves = (data.Hourly ?? []).ToDictionary(
            c => c.Season,
            c => c.Points.Select(p => (p.Hour * 60, p.OffsetTenthC)).ToArray(),
            StringComparer.Ordinal);
        _biomeOffsets = (data.Biomes ?? []).ToDictionary(b => (b.Biome, b.Season), b => b.OffsetTenthC);
    }

    /// <summary>True if any biome has an offset (callers may skip the per-cell lookup otherwise).</summary>
    public bool HasBiomeOffsets => _biomeOffsets.Count > 0;

    /// <summary>
    /// The air temperature at <paramref name="time"/> in <paramref name="weather"/> (null = no weather part)
    /// for a cell of <paramref name="biome"/> (null = no biome part), before any height drop.
    /// </summary>
    public int At(WorldTime time, string? weather, string? biome = null)
    {
        var minuteOfDay = (time.Hour * 60) + time.Minute;
        var part = SeasonPart(time.SeasonId, minuteOfDay, time.Phase, biome);
        var intoSeason = ((long)(time.DayOfSeason - 1) * _minutesPerDay) + minuteOfDay;
        if (_transitionMinutes > 0 && intoSeason < _transitionMinutes && _seasons.Length > 1
            && Array.IndexOf(_seasons, time.SeasonId) is var index and >= 0)
        {
            var previous = SeasonPart(_seasons[(index - 1 + _seasons.Length) % _seasons.Length], minuteOfDay, time.Phase, biome);
            part = previous + (int)((part - previous) * intoSeason / _transitionMinutes);
        }

        var sum = _base + part;
        return weather is null ? sum : sum + _weatherOffsets.GetValueOrDefault(weather);
    }

    private int SeasonPart(string season, int minuteOfDay, DayPhase phase, string? biome)
    {
        var daily = _curves.TryGetValue(season, out var curve)
            ? Interpolate(curve, minuteOfDay)
            : _timeOffsets.GetValueOrDefault(DayPhases.IdOf(phase));
        var biomePart = biome is null
            ? 0
            : _biomeOffsets.GetValueOrDefault((biome, null)) + _biomeOffsets.GetValueOrDefault((biome, season));
        return _seasonOffsets.GetValueOrDefault(season) + daily + biomePart;
    }

    /// <summary>Linear between the points (ascending minutes), wrapping from the last point to the first over midnight.</summary>
    private int Interpolate((int Minute, int Offset)[] points, int minute)
    {
        if (points.Length == 1)
        {
            return points[0].Offset;
        }

        // The last point at or before the minute; before the first point it is the last one of the day before.
        var i = points.Length - 1;
        while (i >= 0 && points[i].Minute > minute)
        {
            i--;
        }

        var (fromMinute, from) = i >= 0 ? points[i] : (points[^1].Minute - _minutesPerDay, points[^1].Offset);
        var (toMinute, to) = i + 1 < points.Length ? points[i + 1] : (points[0].Minute + _minutesPerDay, points[0].Offset);
        return from + (int)((long)(to - from) * (minute - fromMinute) / (toMinute - fromMinute));
    }
}
