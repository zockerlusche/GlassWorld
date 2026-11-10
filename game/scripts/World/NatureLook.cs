using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Weather;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Living nature and weather of the island (B2-5 R16, R21, R23, R24; island scene and main game): the
/// look epoch of the snapshot as the base palette (its colours blend in softly when the epoch changes,
/// <see cref="EpochBlender"/>; the season works on that base), the season of
/// the world clock as shader parameters (the seasonal colours of ground and plants, the snow cover), the
/// wind of the core as plant sway, falling leaves near the camera, and the weather of the snapshot: rain
/// and snow near the camera, the colours and snow cover it lays over the season's, stronger sway in a
/// storm, the waves on sea and lakes (W1724: <c>waves</c> of the weather look as the global parameter
/// <c>water_waves</c>; the crests drift with the wind by a shift summed up here, <see cref="WaveDrift"/>,
/// global <c>water_wave_shift</c>), the clock all water moves by (<see cref="WaterClock"/>, global
/// <c>water_time</c>: it stands still in a pause and wraps without a jump), and through <see cref="Shade"/> the light, clouds and fog of the day (blended softly by
/// <see cref="WeatherBlender"/>; the storm glows softly instead of flashing, <see cref="StormGlow"/>).
/// Nothing is rebuilt: the meshes carry the shares of
/// the seasonal colours (<see cref="SeasonSlots"/>) and the warm comic shader reads global parameters,
/// which this node sets only when they change. The season blends in real time (<see cref="SeasonBlender"/>),
/// the wind is smoothed in real time (<see cref="WindSway"/>), so fast forward stays calm; while the game
/// is paused everything stands still. Presentation only: it reads the snapshot, never writes core state.
/// </summary>
public partial class NatureLook : Node3D
{
    private static readonly string[] TintParameters =
        ["season_tint_0", "season_tint_1", "season_tint_2", "season_tint_3", "season_tint_4", "season_tint_5", "season_tint_6", "season_tint_7"];

    private Dictionary<string, Rgb> _colors = [];
    private FallView _leaves = null!;
    private FallView _rain = null!;
    private FallView _snow = null!;
    private StormGlow _glow = null!;
    private SeasonMix? _seasonShown;
    private (SeasonMix? Season, WeatherMix? Weather, EpochMix? Epoch) _laid;
    private Vector4 _windShown = new(float.NaN, 0, 0, 0);
    private Vector4 _wavesShown = new(float.NaN, 0, 0, 0);
    private Vector4 _shiftShown = new(float.NaN, 0, 0, 0);
    private Vector2 _timeShown = new(float.NaN, 0);
    private Color _light = Colors.White;

    /// <summary>The nature data (content/look/nature.json).</summary>
    public LookNature Data { get; private set; } = null!;

    /// <summary>The season blend.</summary>
    public SeasonBlender Season { get; private set; } = null!;

    /// <summary>The look epoch blend (palette and details, B2-5 R16).</summary>
    public EpochBlender Epoch { get; private set; } = null!;

    /// <summary>The shown wind.</summary>
    public WindSway Wind { get; private set; } = null!;

    /// <summary>The clock the water shaders move by (real time, held in a pause).</summary>
    public WaterClock Water { get; private set; } = null!;

    /// <summary>Where the wave crests have drifted to with the wind.</summary>
    public WaveDrift Waves { get; private set; } = null!;

    /// <summary>The weather look data (content/look/weather.json).</summary>
    public LookWeather WeatherData { get; private set; } = null!;

    /// <summary>The weather blend.</summary>
    public WeatherBlender Weather { get; private set; } = null!;

    /// <summary>The weather ids the data has a look for (the screenshot tool notes the others).</summary>
    public IReadOnlySet<string> WeatherWithLook => WeatherData.Looks.Select(l => l.Weather).ToHashSet(StringComparer.Ordinal);

    /// <summary>Creates the node for a palette (the base palette the meshes were built with).</summary>
    public static NatureLook Create(string contentDirectory, LookPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var data = ContractSerializer.Deserialize<LookNature>(File.ReadAllText(Path.Combine(contentDirectory, "look", "nature.json")));
        var weather = ContractSerializer.Deserialize<LookWeather>(File.ReadAllText(Path.Combine(contentDirectory, "look", "weather.json")));
        var epochs = ContractSerializer.Deserialize<LookEpochs>(File.ReadAllText(Path.Combine(contentDirectory, "look", "epochs.json")));
        var terrain = ContractSerializer.Deserialize<LookTerrain>(File.ReadAllText(Path.Combine(contentDirectory, "look", "terrain.json")));

        // One tint slot per colour a season or an epoch changes; the meshes of ground and objects are built with the same slots.
        var slots = SeasonSlots.From(palette, epochs);
        var look = new NatureLook
        {
            Name = "Nature",
            Data = data,
            _colors = palette.Colors.ToDictionary(c => c.Id, c => Rgb.Parse(c.Color), StringComparer.Ordinal),
            Season = new SeasonBlender(palette, slots),
            Epoch = new EpochBlender(epochs, slots),
            Wind = new WindSway(data.Wind),
            Water = new WaterClock(terrain.WaterTimeWrapSeconds ?? WaterClock.DefaultPeriodSeconds),

            // The two crest layers in the order of the shader's shift parameter: small, large.
            Waves = terrain.Waves is { } crests
                ? new WaveDrift(crests.TurnRealSeconds ?? 4, (crests.Small.SpacingM, crests.Small.SpeedMPerS), (crests.Large.SpacingM, crests.Large.SpeedMPerS))
                : new WaveDrift(0),
            WeatherData = weather,
            Weather = new WeatherBlender(weather, slots),
            _glow = new StormGlow(weather.Glow),
            _leaves = FallView.Create("Leaves", data.LeafFall),
            _rain = FallView.Create("Rain", weather.Rain),
            _snow = FallView.Create("Snow", weather.Snow),
        };
        foreach (var id in new[] { data.Snow.Color, data.LeafFall.Color, weather.Clouds.Color, weather.Rain.Color, weather.Snow.Color })
        {
            _ = look.PaletteColor(id);
        }

        // Every look is resolved once, so a colour the shader cannot move is a data error at the start.
        foreach (var entry in weather.Looks)
        {
            _ = WeatherMix.Of(weather, slots, entry.Weather);
        }

        look.AddChild(look._leaves);
        look.AddChild(look._rain);
        look.AddChild(look._snow);
        var snow = data.Snow;
        RenderingServer.GlobalShaderParameterSet("nature_snow_shape", new Vector4((float)snow.PatchM, (float)snow.Edge, (float)snow.MinUp, 0));
        RenderingServer.GlobalShaderParameterSet("nature_wind_shape", new Vector4((float)look.Wind.WaveNumber, (float)look.Wind.LeanShare, 0, 0));
        look.Apply(look.Weather.Shown.Over(look.Epoch.Shown.Over(look.Season.Shown)));
        look.ApplyWind();
        return look;
    }

    /// <summary>
    /// One frame of a running world: the season id, the look epoch and the weather of the snapshot, the
    /// real seconds since the last frame (0 while paused), the camera target on the ground, the speed
    /// factor and the real seconds one game minute lasts at normal speed (for the weather's transition time).
    /// </summary>
    public void Follow(string season, string? epoch, WeatherReading? weather, double realSeconds, Vector3 focus, double speed, double realSecondsPerGameMinute)
    {
        var mix = Weather.Update(weather?.Weather, weather?.Conditions, weather?.TransitionMinutes ?? 0, realSecondsPerGameMinute, speed, realSeconds);
        _glow.Update(mix.Glow, realSeconds);

        // Outside a blend the blenders hand out the same mix every frame: nothing is composed or written then.
        // The epoch palette is the base the season works on (where both name a colour the epoch's wins);
        // the weather lies over both.
        var seasonMix = Season.Update(season, realSeconds);
        var epochMix = Epoch.Update(epoch, realSeconds);
        if (!ReferenceEquals(seasonMix, _laid.Season) || !ReferenceEquals(mix, _laid.Weather) || !ReferenceEquals(epochMix, _laid.Epoch))
        {
            _laid = (seasonMix, mix, epochMix);
            Apply(mix.Over(epochMix.Over(seasonMix)));
        }

        Wind.Update(weather?.Wind, realSeconds);
        Water.Update(realSeconds);
        Waves.Update(Wind.DirectionX, Wind.DirectionZ, realSeconds);
        ApplyWind();
        Fall(focus);
    }

    /// <summary>
    /// Screenshot tool: shows a season, a weather and a look epoch at once (null = the base colours, no
    /// weather, the base palette), at the camera target <paramref name="focus"/>; the same shader
    /// parameters as in the running game.
    /// </summary>
    public void Show(string? season, string? weather, Vector3 focus, string? epoch = null)
    {
        var mix = Weather.Jump(weather, weather is null ? null : [weather]);
        _laid = (null, null, null);
        Apply(mix.Over(Epoch.Jump(epoch).Over(Season.Jump(season))));
        ApplyWind();
        Fall(focus);
        _leaves.Settle();
        _rain.Settle();
        _snow.Settle();
    }

    /// <summary>
    /// The day light, the clouds and the fog under the shown weather (and the storm glow of this moment):
    /// what <see cref="DaylightLook.Apply(LookDaylight, DaylightMix, DirectionalLight3D, Godot.Environment, WeatherSky?, WeatherClouds?)"/> shows.
    /// </summary>
    public (DaylightMix Light, WeatherSky Sky) Shade(DaylightMix day, double minAmbient)
    {
        var (mix, glow) = (Weather.Shown, WeatherData.Glow);
        return (mix.Shade(day, minAmbient, _glow.Value, glow.AmbientBoost), mix.Sky(day, PaletteColor(WeatherData.Clouds.Color), _glow.Value, glow.SkyBoost));
    }

    /// <summary>The day light that tints the unlit particles (the water light of the shaded day).</summary>
    public void Light(Rgb light)
    {
        _light = new Color(light.R, light.G, light.B);
        TintLeaves();
        var (rain, snow) = (PaletteColor(WeatherData.Rain.Color), PaletteColor(WeatherData.Snow.Color));
        _rain.Tint(new Color(rain.R, rain.G, rain.B), _light);
        _snow.Tint(new Color(snow.R, snow.G, snow.B), _light);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        // Back to the neutral values, so nothing of this world shows in the next one.
        foreach (var name in TintParameters)
        {
            RenderingServer.GlobalShaderParameterSet(name, Vector4.Zero);
        }

        RenderingServer.GlobalShaderParameterSet("nature_snow", new Vector4(1, 1, 1, 0));
        RenderingServer.GlobalShaderParameterSet("nature_wind", new Vector4(0, 1, 0, 0));
        RenderingServer.GlobalShaderParameterSet("water_waves", new Vector4(0, 0, 1, 0));
        RenderingServer.GlobalShaderParameterSet("water_wave_shift", Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet("water_time", new Vector2(0, (float)WaterClock.DefaultPeriodSeconds));
    }

    private Rgb PaletteColor(string id) =>
        _colors.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"content/look/nature.json names unknown palette colour '{id}'.");

    /// <summary>The colour of a palette id now: its seasonal colour if a season changes it, else the palette colour.</summary>
    private Rgb ColorNow(string id, SeasonMix mix) => Season.Slots.IndexOf(id) is >= 0 and var slot ? mix.Colors[slot] : PaletteColor(id);

    private static Color Linear(Rgb c) => new Color(c.R, c.G, c.B).SrgbToLinear();

    private void Apply(SeasonMix mix)
    {
        if (mix.Equals(_seasonShown))
        {
            return;
        }

        _seasonShown = mix;
        var slots = Season.Slots;
        for (var i = 0; i < TintParameters.Length; i++)
        {
            // The way from the colour the meshes carry to the colour shown now, in the shader's linear space.
            var delta = i < slots.Ids.Count ? Linear(mix.Colors[i]) - Linear(slots.BaseColors[i]) : new Color(0, 0, 0, 0);
            RenderingServer.GlobalShaderParameterSet(TintParameters[i], new Vector4(delta.R, delta.G, delta.B, 0));
        }

        var snow = Linear(PaletteColor(Data.Snow.Color));
        RenderingServer.GlobalShaderParameterSet("nature_snow", new Vector4(snow.R, snow.G, snow.B, (float)Math.Clamp(mix.SnowCover, 0, 1)));
        TintLeaves();
    }

    private void ApplyWind()
    {
        // The weather's sway factor makes a storm pull harder at the plants than its wind alone.
        var strength = Math.Clamp(Wind.Strength * Weather.Shown.Sway, 0, 1);
        var wind = new Vector4((float)Wind.DirectionX, (float)Wind.DirectionZ, (float)strength, (float)Wind.Phase);
        if (wind != _windShown)
        {
            _windShown = wind;
            RenderingServer.GlobalShaderParameterSet("nature_wind", wind);
        }

        // Waves on sea and lakes (W1724): how many by the weather shown now (it blends softly with the
        // weather); the strokes lie across the eased wind direction (never without a length), and the
        // crests are where the wind has pushed their grids so far.
        Waves.Update(Wind.DirectionX, Wind.DirectionZ, 0);
        var waves = new Vector4((float)Math.Clamp(Weather.Shown.Waves, 0, 1), (float)Waves.DirectionX, (float)Waves.DirectionZ, 0);
        if (waves != _wavesShown)
        {
            _wavesShown = waves;
            RenderingServer.GlobalShaderParameterSet("water_waves", waves);
        }

        var (small, large) = Waves.Shifts.Count >= 2 ? (Waves.Shifts[0], Waves.Shifts[1]) : default;
        var shift = new Vector4((float)small.X, (float)small.Z, (float)large.X, (float)large.Z);
        if (shift != _shiftShown)
        {
            _shiftShown = shift;
            RenderingServer.GlobalShaderParameterSet("water_wave_shift", shift);
        }

        // The clock of all water: real time, held in a pause, wrapped at its period.
        var time = new Vector2((float)Water.Seconds, (float)Water.PeriodSeconds);
        if (time != _timeShown)
        {
            _timeShown = time;
            RenderingServer.GlobalShaderParameterSet("water_time", time);
        }
    }

    private void Fall(Vector3 focus)
    {
        var direction = new Vector2((float)Wind.DirectionX, (float)Wind.DirectionZ);
        _leaves.Follow(focus, _seasonShown?.LeafFall ?? 0, direction, Wind.Strength);
        _rain.Follow(focus, Weather.Shown.Rain, direction, Wind.Strength);
        _snow.Follow(focus, Weather.Shown.Snow, direction, Wind.Strength);
    }

    private void TintLeaves()
    {
        if (_seasonShown is { } mix)
        {
            var c = ColorNow(Data.LeafFall.Color, mix);
            _leaves.Tint(new Color(c.R, c.G, c.B), _light);
        }
    }
}
