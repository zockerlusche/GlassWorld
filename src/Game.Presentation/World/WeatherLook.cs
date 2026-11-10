using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// What a weather shows (B2-5 R23), resolved from <c>content/look/weather.json</c>: clouds, fog, rain and
/// snow near the camera, how bright and in which tint the light is, the least snow cover, how strongly the
/// plants sway, the storm glow and the colours it lays over the season's (dry grass in heat and drought).
/// <see cref="Lerp"/> blends two weathers. Presentation only.
/// </summary>
/// <param name="Overcast">Share of the sky covered by clouds (0..1).</param>
/// <param name="CloudShade">Brightness of the clouds (1 = bright, lower = dark rain clouds).</param>
/// <param name="FogDensity">Density of the fog (0 = none).</param>
/// <param name="FogSky">How much the fog hides the sky (0..1).</param>
/// <param name="Rain">Amount of rain near the camera (0..1).</param>
/// <param name="Snow">Amount of falling snow near the camera (0..1).</param>
/// <param name="Light">Factor on the energy of the main light.</param>
/// <param name="Ambient">Factor on the ambient light and the unlit water.</param>
/// <param name="Tint">Tint of the light (multiplied).</param>
/// <param name="Shadow">Factor on the opacity of the shadows.</param>
/// <param name="SnowCover">Least share of the ground covered by snow while it holds (0..1).</param>
/// <param name="Sway">Factor on the sway of the plants.</param>
/// <param name="Glow">Amount of the soft storm glow (0..1).</param>
/// <param name="Colors">Per tint slot (<see cref="SeasonSlots"/>): the colour laid over the season's and how much of it (0..1).</param>
public sealed record WeatherMix(
    double Overcast,
    double CloudShade,
    double FogDensity,
    double FogSky,
    double Rain,
    double Snow,
    double Light,
    double Ambient,
    Rgb Tint,
    double Shadow,
    double SnowCover,
    double Sway,
    double Glow,
    IReadOnlyList<(Rgb Color, double Weight)> Colors)
{
    /// <summary>Waves on the sea and on lakes (W1724): 0 = none, about 0.3 = small, 1 = large.</summary>
    public double Waves { get; init; }

    /// <summary>No weather (a world without a weather system, an unknown weather id): the plain day.</summary>
    public static WeatherMix Neutral(SeasonSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return new WeatherMix(0, 1, 0, 0, 0, 0, 1, 1, new Rgb(1, 1, 1), 1, 0, 1, 0, [.. slots.Ids.Select(_ => (new Rgb(1, 1, 1), 0.0))]);
    }

    /// <summary>
    /// The look of a main weather with its conditions (<c>WeatherReading.Conditions</c>). A condition that
    /// is not the main weather (drought after the dry days) lays its colours and its tint over the main
    /// weather's look; everything else stays the main weather's. A weather without an entry looks neutral.
    /// </summary>
    public static WeatherMix Of(LookWeather data, SeasonSlots slots, string? weather, IEnumerable<string>? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(slots);
        var main = weather is null ? null : data.Looks.FirstOrDefault(l => l.Weather == weather);
        var mix = Neutral(slots);
        if (main is not null)
        {
            mix = mix with
            {
                Overcast = main.Overcast,
                CloudShade = main.CloudShade,
                FogDensity = main.FogDensity,
                FogSky = main.FogSky,
                Rain = main.Rain,
                Snow = main.Snow,
                Light = main.Light,
                Ambient = main.Ambient,
                Shadow = main.Shadow,
                SnowCover = main.SnowCover,
                Sway = main.Sway,
                Glow = main.Glow,
                Waves = main.Waves ?? 0,
            };
        }

        var colors = mix.Colors.ToArray();
        var tint = mix.Tint;
        var laid = new[] { main }.Concat((conditions ?? []).Where(c => c != weather).Order(StringComparer.Ordinal).Select(c => data.Looks.FirstOrDefault(l => l.Weather == c)));
        foreach (var look in laid.Where(l => l is not null))
        {
            var t = Rgb.Parse(look!.Tint);
            tint = new Rgb(tint.R * t.R, tint.G * t.G, tint.B * t.B);
            foreach (var c in look.Colors)
            {
                var slot = slots.IndexOf(c.Id);
                if (slot < 0)
                {
                    throw new InvalidDataException($"content/look/weather.json: weather '{look.Weather}' changes colour '{c.Id}', which no season changes (only seasonal colours can move without a rebuild).");
                }

                colors[slot] = (Rgb.Parse(c.Color), 1);
            }
        }

        return mix with { Tint = tint, Colors = colors };
    }

    /// <summary>Blends towards <paramref name="other"/> (0 = this, 1 = the other).</summary>
    public WeatherMix Lerp(WeatherMix other, double t)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (t <= 0 || t >= 1)
        {
            return t <= 0 ? this : other;
        }

        double Mix(double a, double b) => a + ((b - a) * t);
        (Rgb Color, double Weight) Color((Rgb Color, double Weight) a, (Rgb Color, double Weight) b) =>
            (a.Weight <= 0 ? b.Color : b.Weight <= 0 ? a.Color : a.Color.Lerp(b.Color, (float)t), Mix(a.Weight, b.Weight));
        return new WeatherMix(
            Mix(Overcast, other.Overcast),
            Mix(CloudShade, other.CloudShade),
            Mix(FogDensity, other.FogDensity),
            Mix(FogSky, other.FogSky),
            Mix(Rain, other.Rain),
            Mix(Snow, other.Snow),
            Mix(Light, other.Light),
            Mix(Ambient, other.Ambient),
            Tint.Lerp(other.Tint, (float)t),
            Mix(Shadow, other.Shadow),
            Mix(SnowCover, other.SnowCover),
            Mix(Sway, other.Sway),
            Mix(Glow, other.Glow),
            [.. Colors.Select((c, i) => Color(c, other.Colors[i]))])
        {
            Waves = Mix(Waves, other.Waves),
        };
    }

    /// <summary>
    /// The season's look under this weather: the weather's colours laid over the season's by their weight,
    /// and at least the weather's snow cover (snow that fell stays the season's business).
    /// </summary>
    public SeasonMix Over(SeasonMix season)
    {
        ArgumentNullException.ThrowIfNull(season);
        return new SeasonMix(
            [.. season.Colors.Select((c, i) => Colors[i].Weight <= 0 ? c : Colors[i].Weight >= 1 ? Colors[i].Color : c.Lerp(Colors[i].Color, (float)Colors[i].Weight))],
            Math.Max(season.SnowCover, SnowCover),
            season.LeafFall);
    }

    /// <summary>
    /// The day light under this weather: dimmer and tinted light, softer shadows, brighter or darker
    /// ambient light (never below <paramref name="minAmbient"/>, the night floor of the daylight data),
    /// plus the storm glow (<paramref name="glow"/> 0..1, already limited by <see cref="FlickerLimiter"/>)
    /// as a soft lift of the ambient light.
    /// </summary>
    public DaylightMix Shade(DaylightMix day, double minAmbient, double glow = 0, double glowAmbient = 0)
    {
        ArgumentNullException.ThrowIfNull(day);
        Rgb Tinted(Rgb c) => new(c.R * Tint.R, c.G * Tint.G, c.B * Tint.B);
        var water = Tinted(day.WaterLight).Scaled((float)Math.Clamp(Ambient, 0, 1));
        return day with
        {
            LightColor = Tinted(day.LightColor),
            LightEnergy = day.LightEnergy * Light,
            ShadowOpacity = day.ShadowOpacity * Math.Clamp(Shadow, 0, 1),
            Ambient = Tinted(day.Ambient),
            AmbientEnergy = Math.Max(day.AmbientEnergy * Ambient, Math.Min(day.AmbientEnergy, minAmbient)) + (Math.Clamp(glow, 0, 1) * glowAmbient),
            WaterLight = water,
        };
    }

    /// <summary>
    /// The sky under this weather: the cloud cover in the cloud colour of the data, darkened by the weather
    /// and lit by the day (the unlit tint of the day phase, so clouds are dark at night) and a little by the
    /// storm glow; and the fog in the colour of the horizon under those clouds.
    /// </summary>
    public WeatherSky Sky(DaylightMix day, Rgb cloud, double glow = 0, double glowSky = 0)
    {
        ArgumentNullException.ThrowIfNull(day);
        var lift = (float)(Math.Clamp(CloudShade, 0, 1) * (1 + (Math.Clamp(glow, 0, 1) * glowSky)));
        var lit = new Rgb(cloud.R * day.WaterLight.R, cloud.G * day.WaterLight.G, cloud.B * day.WaterLight.B).Scaled(lift);
        return new WeatherSky(Math.Clamp(Overcast, 0, 1), lit, Math.Max(FogDensity, 0), Math.Clamp(FogSky, 0, 1), day.SkyHorizon.Lerp(lit, (float)Math.Clamp(Overcast, 0, 1)));
    }

    /// <inheritdoc/>
    public bool Equals(WeatherMix? other) =>
        other is not null
        && (Overcast, CloudShade, FogDensity, FogSky, Rain, Snow, Light).Equals((other.Overcast, other.CloudShade, other.FogDensity, other.FogSky, other.Rain, other.Snow, other.Light))
        && (Ambient, Tint, Shadow, SnowCover, Sway, Glow, Waves).Equals((other.Ambient, other.Tint, other.Shadow, other.SnowCover, other.Sway, other.Glow, other.Waves))
        && Colors.SequenceEqual(other.Colors);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Overcast, FogDensity, Rain, Snow, Light, SnowCover, Glow);
}

/// <summary>Clouds and fog of one moment, for the sky shader and the environment.</summary>
/// <param name="Clouds">Share of the sky covered by clouds (0..1).</param>
/// <param name="CloudColor">Colour of the clouds, lit by the day.</param>
/// <param name="FogDensity">Density of the fog (0 = none).</param>
/// <param name="FogSky">How much the fog hides the sky (0..1).</param>
/// <param name="FogColor">Colour of the fog.</param>
public sealed record WeatherSky(double Clouds, Rgb CloudColor, double FogDensity, double FogSky, Rgb FogColor);

/// <summary>
/// The weather look follows the snapshot (B2-5 R23): when the main weather or its conditions change, the
/// look blends from what is shown to the new weather over the weather's <c>transition_minutes</c> of game
/// time (the climate data, in the reading), but never faster than <c>min_transition_real_seconds</c> of
/// <c>content/look/weather.json</c> - at x16 an hour of game time is only a few real seconds, and the
/// sky must still change softly. A second change during a blend starts from what is shown, so nothing
/// jumps. The first weather shows at once. Presentation only: it reads the reading, never core state.
/// </summary>
public sealed class WeatherBlender
{
    private readonly LookWeather _data;
    private WeatherMix _from;
    private WeatherMix _to;
    private string? _key;
    private double _progress = 1;
    private bool _started;

    /// <summary>Creates the blender for the tint slots of the palette.</summary>
    public WeatherBlender(LookWeather data, SeasonSlots slots)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(slots);
        (_data, Slots) = (data, slots);
        Shown = _from = _to = WeatherMix.Neutral(slots);
    }

    /// <summary>The tint slots the mixes are for.</summary>
    public SeasonSlots Slots { get; }

    /// <summary>What is shown now.</summary>
    public WeatherMix Shown { get; private set; }

    /// <summary>How far the running blend is (0..1, eased; 1 outside a blend).</summary>
    public double T => _progress * _progress * (3 - (2 * _progress));

    /// <summary>
    /// Real seconds a change blends: the transition minutes of game time at the given speed, at least the
    /// minimum of the look data.
    /// </summary>
    public double DurationSeconds(int transitionMinutes, double realSecondsPerGameMinute, double speed) =>
        Math.Max(_data.MinTransitionRealSeconds, speed > 0 ? transitionMinutes * realSecondsPerGameMinute / speed : double.PositiveInfinity);

    /// <summary>Shows a weather at once (start of a world, a load, the screenshot tool); null = no weather.</summary>
    public WeatherMix Jump(string? weather, IEnumerable<string>? conditions = null)
    {
        var all = (conditions ?? []).ToList();
        (_started, _progress, _key) = (true, 1, Key(weather, all));
        Shown = _from = _to = WeatherMix.Of(_data, Slots, weather, all);
        return Shown;
    }

    /// <summary>
    /// One frame: the weather and its conditions from the snapshot (null without a weather system), how
    /// long the core says a change blends, the real seconds one game minute lasts at normal speed, the
    /// speed factor and the real seconds since the last frame (0 while paused). Returns what to show.
    /// </summary>
    public WeatherMix Update(string? weather, IReadOnlyList<string>? conditions, int transitionMinutes, double realSecondsPerGameMinute, double speed, double realSeconds)
    {
        if (!_started)
        {
            return Jump(weather, conditions);
        }

        var key = Key(weather, conditions ?? []);
        if (key != _key)
        {
            // Blend from what is shown now (also in the middle of a blend), so the look is continuous.
            (_from, _to, _key, _progress) = (Shown, WeatherMix.Of(_data, Slots, weather, conditions), key, 0);
        }

        if (_progress < 1)
        {
            var duration = DurationSeconds(transitionMinutes, realSecondsPerGameMinute, speed);
            _progress = Math.Min(1, _progress + (Math.Max(realSeconds, 0) / duration));
            if (_progress >= 1)
            {
                _from = _to;
            }
        }

        Shown = _from.Lerp(_to, T);
        return Shown;
    }

    private static string Key(string? weather, IEnumerable<string> conditions) => $"{weather}|{string.Join(',', conditions.Order(StringComparer.Ordinal))}";
}

/// <summary>
/// Keeps any changing brightness calm (B2466 accessibility: no flashing, at most three light-dark changes
/// per second): the output follows the wanted value with a limited rise and fall per second, and after it
/// turned (from brighter to darker or back) it may turn again only after the minimum time. Whatever is
/// asked of it - a flickering value, a square wave - the output never turns more often than
/// <see cref="MaxChangesPerSecond"/> in any second and never jumps. Presentation only.
/// </summary>
public sealed class FlickerLimiter
{
    /// <summary>The most light-dark changes per second the accessibility rule allows (B2466).</summary>
    public const double AllowedChangesPerSecond = 3;

    private readonly double _risePerSecond;
    private readonly double _fallPerSecond;
    private readonly double _minInterval;
    private int _direction;
    private double _sinceTurn = double.PositiveInfinity;

    /// <summary>
    /// Creates the limiter: at most <paramref name="maxChangesPerSecond"/> turns per second (capped at
    /// <see cref="AllowedChangesPerSecond"/> whatever the data says) and a full rise over
    /// <paramref name="riseSeconds"/>, a full fall over <paramref name="fallSeconds"/>.
    /// </summary>
    public FlickerLimiter(double maxChangesPerSecond, double riseSeconds, double fallSeconds)
    {
        MaxChangesPerSecond = Math.Clamp(maxChangesPerSecond, 0.01, AllowedChangesPerSecond);

        // A little more than the even spacing, so the count holds for every window of one second.
        _minInterval = 1.0001 / MaxChangesPerSecond;
        _risePerSecond = 1 / Math.Max(riseSeconds, 0.05);
        _fallPerSecond = 1 / Math.Max(fallSeconds, 0.05);
    }

    /// <summary>The most turns per second this limiter lets through.</summary>
    public double MaxChangesPerSecond { get; }

    /// <summary>The value shown now (0..1).</summary>
    public double Value { get; private set; }

    /// <summary>One frame: moves towards <paramref name="wanted"/> (0..1) within the limits; returns the value to show.</summary>
    public double Update(double wanted, double realSeconds)
    {
        var dt = Math.Max(realSeconds, 0);
        _sinceTurn += dt;
        var delta = Math.Clamp(wanted, 0, 1) - Value;
        var direction = Math.Sign(delta);
        if (direction == 0 || dt <= 0)
        {
            return Value;
        }

        if (direction != _direction)
        {
            if (_sinceTurn < _minInterval)
            {
                // Too soon after the last turn: hold the value until the minimum time has passed.
                return Value;
            }

            (_direction, _sinceTurn) = (direction, 0);
        }

        var step = (direction > 0 ? _risePerSecond : _fallPerSecond) * dt;
        Value += Math.Clamp(delta, -step, step);
        return Value;
    }
}

/// <summary>
/// The storm glow (B2-5 R23): instead of lightning flashes a soft, slow brightening of the clouds every
/// few seconds - no hard flash, no flicker, never a camera shake (B2466). The moments come from a fixed
/// sequence in real time (presentation only: no core random stream, not in the hash); the brightness goes
/// through the <see cref="FlickerLimiter"/> of the data, which holds the accessibility limit whatever the
/// data asks for.
/// </summary>
public sealed class StormGlow
{
    private readonly WeatherGlow _data;
    private readonly FlickerLimiter _limiter;
    private double _untilNext;
    private double _hold;
    private uint _index;

    /// <summary>Creates the glow from the look data.</summary>
    public StormGlow(WeatherGlow data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _limiter = new FlickerLimiter(data.MaxChangesPerSecond, data.RiseS, data.FallS);
        _untilNext = Pause();
    }

    /// <summary>The limiter the glow goes through.</summary>
    public FlickerLimiter Limiter => _limiter;

    /// <summary>The glow shown now (0..1).</summary>
    public double Value => _limiter.Value;

    /// <summary>
    /// One frame: <paramref name="amount"/> is the glow of the shown weather (0 = no storm: the glow fades
    /// out and no new one begins), <paramref name="realSeconds"/> the real time since the last frame (0
    /// while paused).
    /// </summary>
    public double Update(double amount, double realSeconds)
    {
        var dt = Math.Max(realSeconds, 0);
        var wanted = 0.0;
        if (amount > 0)
        {
            if (_hold > 0)
            {
                _hold -= dt;
                wanted = Math.Clamp(amount, 0, 1);
            }
            else if ((_untilNext -= dt) <= 0)
            {
                (_hold, _untilNext) = (_data.RiseS + _data.HoldS, Pause());
            }
        }
        else
        {
            _hold = 0;
        }

        return _limiter.Update(wanted, dt);
    }

    /// <summary>The pause before the next glow: between the data's interval bounds, from a fixed sequence.</summary>
    private double Pause()
    {
        var h = unchecked(++_index * 2654435761u);
        h ^= h >> 15;
        var (min, max) = (_data.IntervalS.Min, Math.Max(_data.IntervalS.Min, _data.IntervalS.Max));
        return min + ((max - min) * ((h & 0xFFFF) / 65535.0));
    }
}
