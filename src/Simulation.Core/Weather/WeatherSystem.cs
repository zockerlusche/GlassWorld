using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Weather;

/// <summary>The weather now, for the presentation and the decider (all values from the climate data).</summary>
/// <param name="Weather">Main weather id.</param>
/// <param name="Conditions">All current conditions (main weather, plus drought after dry days), sorted by id.</param>
/// <param name="WindPermille">Wind 0..1000 (plants sway with it, B2 R21).</param>
/// <param name="PrecipitationPermille">Precipitation 0..1000.</param>
/// <param name="CloudPermille">Cloud cover 0..1000.</param>
/// <param name="TemperatureTenthC">Air temperature in tenths of a degree (climate table, see <see cref="WeatherSystem.TemperatureAt"/>).</param>
/// <param name="SinceTick">Tick at which the main weather began.</param>
/// <param name="NextChangeTick">Tick of the next possible change.</param>
/// <param name="TransitionMinutes">How long the presentation blends a change.</param>
public sealed record WeatherReading(
    string Weather,
    IReadOnlyList<string> Conditions,
    int WindPermille,
    int PrecipitationPermille,
    int CloudPermille,
    int TemperatureTenthC,
    long SinceTick,
    long NextChangeTick,
    int TransitionMinutes);

/// <summary>Saved state of the weather (save games, meta JSON).</summary>
/// <param name="Weather">Main weather id.</param>
/// <param name="SinceTick">Tick at which it began.</param>
/// <param name="DryTicks">Ticks in a row without precipitation.</param>
/// <param name="History">Recent phases (start tick, weather), oldest first, the current one last.</param>
public sealed record WeatherState(string Weather, long SinceTick, long DryTicks, IReadOnlyList<KeyValuePair<long, string>> History);

/// <summary>
/// The weather as a world system (B2 R13, name "weather", stream "weather/change"): at every change step
/// (change_every_hours of the climate) the next main weather is drawn from the transition table of the
/// current season; precipitation below the snow temperature turns into the snow weather; after the dry
/// days of the data the drought weather joins the conditions. Integer state only, deterministic, part of
/// the hash and the save game. The weather is island-wide for now.
/// </summary>
public sealed class WeatherSystem : IWorldSystem, IWeather
{
    /// <summary>Recent phases kept for <see cref="WeatherAt"/> (a technical buffer, not a world value).</summary>
    public const int HistoryLength = 32;

    private readonly ClimateWeather _data;
    private readonly Dictionary<string, ClimateWeatherState> _states;
    private readonly int _heightOffsetTenthC;
    private readonly Dictionary<string, int> _seasonOffsets;
    private readonly Dictionary<string, int> _weatherOffsets;
    private readonly Dictionary<string, int> _timeOffsets;
    private readonly long _changeTicks;
    private readonly long _droughtTicks;
    private readonly List<KeyValuePair<long, string>> _history = [];
    private long _dryTicks;

    // The world tick up to which the weather is known: systems advance at tick t, then the world moves to t + 1.
    private long _knownUntil;

    /// <summary>
    /// Creates the weather of a climate; it starts with the first state of the data at tick 0.
    /// <paramref name="heightOffsetTenthC"/> is the preset's height drop for the place the island-wide
    /// weather stands for (the mean height of the start cells, see SimulationHost).
    /// </summary>
    public WeatherSystem(ClimateSettings climate, int heightOffsetTenthC, int ticksPerHour, int hoursPerDay)
    {
        ArgumentNullException.ThrowIfNull(climate);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerHour, 1);
        _data = climate.Weather;
        _states = _data.States.ToDictionary(s => s.Weather, StringComparer.Ordinal);
        _heightOffsetTenthC = heightOffsetTenthC;
        var temperature = _data.Temperature;
        _seasonOffsets = temperature.Seasons.ToDictionary(s => s.Season, s => s.OffsetTenthC, StringComparer.Ordinal);
        _weatherOffsets = temperature.Weathers.ToDictionary(w => w.Weather, w => w.OffsetTenthC, StringComparer.Ordinal);
        _timeOffsets = temperature.TimesOfDay.ToDictionary(t => t.TimeOfDay, t => t.OffsetTenthC, StringComparer.Ordinal);
        _changeTicks = Math.Max(1L, (long)_data.ChangeEveryHours * ticksPerHour);
        _droughtTicks = (long)_data.DroughtAfterDryDays * hoursPerDay * ticksPerHour;
        Current = _data.States.First().Weather;
        _history.Add(new KeyValuePair<long, string>(0, Current));
    }

    /// <inheritdoc/>
    public string Name => "weather";

    /// <inheritdoc/>
    public string Current { get; private set; }

    /// <summary>Tick at which the current main weather began.</summary>
    public long SinceTick => _history[^1].Key;

    /// <summary>Raised when the main weather changes (old, new, tick); for chronicle and B5 signals.</summary>
    public event Action<string, string, long>? Changed;

    /// <summary>All current conditions, sorted by id (main weather, plus drought after the dry days).</summary>
    public IReadOnlyList<string> Conditions =>
        _droughtTicks > 0 && _dryTicks >= _droughtTicks && Current != _data.DroughtWeather
            ? [.. new[] { Current, _data.DroughtWeather }.Order(StringComparer.Ordinal)]
            : [Current];

    /// <summary>
    /// The main weather at a place and tick. The weather is island-wide for now (<paramref name="at"/> is
    /// ignored). Allowed ticks: the current tick, any tick of the current phase and of the recent phases
    /// kept in a history of <see cref="HistoryLength"/> changes; a tick before that history or after the
    /// current tick throws <see cref="ArgumentOutOfRangeException"/>, so a caller never gets a guess.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tick lies in the future or before the kept history.</exception>
    public string WeatherAt(Position at, long tick)
    {
        if (tick < _history[0].Key || tick > _knownUntil)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"weather is known for ticks {_history[0].Key}..{_knownUntil}");
        }

        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Key <= tick)
            {
                return _history[i].Value;
            }
        }

        return _history[0].Value;
    }

    /// <summary>The weather now with its numbers from the data.</summary>
    public WeatherReading Read(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var state = _states[Current];
        var next = ((world.Tick / _changeTicks) + 1) * _changeTicks;
        return new WeatherReading(Current, Conditions, state.WindPermille, state.PrecipitationPermille, state.CloudPermille, TemperatureAt(world.Now, Current), SinceTick, next, _data.TransitionMinutes);
    }

    /// <summary>
    /// Air temperature in tenths of a degree: climate base + offset of the season + offset of the weather
    /// (none when <paramref name="weather"/> is null) + offset of the time of day + the height drop.
    /// Integers only; every season, weather and time of day has an entry (checked at load).
    /// </summary>
    public int TemperatureAt(Time.WorldTime time, string? weather)
    {
        var sum = _data.Temperature.BaseTenthC + _heightOffsetTenthC
            + _seasonOffsets.GetValueOrDefault(time.SeasonId)
            + _timeOffsets.GetValueOrDefault(time.Phase.ToString().ToLowerInvariant());
        return weather is null ? sum : sum + _weatherOffsets.GetValueOrDefault(weather);
    }

    /// <summary>Sets the main weather now (player intervention, B6; debug jumps, R23). Unknown ids are refused.</summary>
    public void Force(string weather, long tick)
    {
        if (!_states.ContainsKey(weather))
        {
            throw new ArgumentException($"Unknown weather '{weather}'.", nameof(weather));
        }

        Change(weather, tick);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var tick = world.Tick;
        _knownUntil = tick + 1;
        if (tick > 0 && tick % _changeTicks == 0)
        {
            var next = Draw(world.Now.SeasonId, world.Random.Get("weather/change"));
            // Snow or rain is decided by the temperature without the new weather's own offset.
            if (_states[next].PrecipitationPermille > 0 && TemperatureAt(world.Now, null) < _data.SnowBelowTenthC && _states.ContainsKey(_data.SnowWeather))
            {
                next = _data.SnowWeather;
            }

            if (next != Current)
            {
                Change(next, tick);
            }
        }

        _dryTicks = _states[Current].PrecipitationPermille == 0 ? _dryTicks + 1 : 0;
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Current).Write(_dryTicks).Write(_history.Count);
        foreach (var (start, weather) in _history)
        {
            hasher.Write(start).Write(weather);
        }
    }

    /// <summary>The saved state.</summary>
    public WeatherState Capture() => new(Current, SinceTick, _dryTicks, [.. _history]);

    /// <summary>Restores a saved state (unknown ids are refused).</summary>
    public void Restore(WeatherState state, long tick)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_states.ContainsKey(state.Weather) || state.History.Count == 0 || state.History.Any(h => !_states.ContainsKey(h.Value)))
        {
            throw new InvalidDataException($"Save game weather '{state.Weather}' is not in this climate.");
        }

        Current = state.Weather;
        _dryTicks = state.DryTicks;
        _history.Clear();
        _history.AddRange(state.History);
        _knownUntil = tick;
    }

    private void Change(string weather, long tick)
    {
        var old = Current;
        Current = weather;
        _history.Add(new KeyValuePair<long, string>(tick, weather));
        if (_history.Count > HistoryLength)
        {
            _history.RemoveAt(0);
        }

        Changed?.Invoke(old, weather, tick);
    }

    /// <summary>
    /// Draws the next weather from the season's row for the current weather; a weather without a row in this
    /// season (e.g. heat in winter) uses the season's first row.
    /// </summary>
    private string Draw(string season, Random.Pcg32 rng)
    {
        var table = _data.Seasons.FirstOrDefault(s => s.Season == season) ?? _data.Seasons.First();
        var row = table.Transitions.FirstOrDefault(t => t.FromWeather == Current) ?? table.Transitions.First();
        var total = row.To.Sum(t => t.Weight);
        if (total <= 0)
        {
            return Current;
        }

        var pick = rng.NextInt(0, total);
        foreach (var target in row.To)
        {
            pick -= target.Weight;
            if (pick < 0)
            {
                return target.Weather;
            }
        }

        return row.To.Last().Weather;
    }
}
