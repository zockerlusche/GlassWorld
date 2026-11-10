using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Weather;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Living nature of the island (B2-5 R21, R24; island scene and main game): the season of the world clock
/// as shader parameters (the seasonal colours of ground and plants, the snow cover), the wind of the core
/// as plant sway, and falling leaves near the camera. Nothing is rebuilt: the meshes carry the shares of
/// the seasonal colours (<see cref="SeasonSlots"/>) and the warm comic shader reads global parameters,
/// which this node sets only when they change. The season blends in real time (<see cref="SeasonBlender"/>),
/// the wind is smoothed in real time (<see cref="WindSway"/>), so fast forward stays calm; while the game
/// is paused everything stands still. Presentation only: it reads the snapshot, never writes core state.
/// </summary>
public partial class NatureLook : Node3D
{
    private static readonly string[] TintParameters = ["season_tint_0", "season_tint_1", "season_tint_2", "season_tint_3"];

    private LookPalette _palette = null!;
    private Dictionary<string, Rgb> _colors = [];
    private FallView _leaves = null!;
    private SeasonMix? _seasonShown;
    private Vector4 _windShown = new(float.NaN, 0, 0, 0);
    private Color _light = Colors.White;

    /// <summary>The nature data (content/look/nature.json).</summary>
    public LookNature Data { get; private set; } = null!;

    /// <summary>The season blend.</summary>
    public SeasonBlender Season { get; private set; } = null!;

    /// <summary>The shown wind.</summary>
    public WindSway Wind { get; private set; } = null!;

    /// <summary>Creates the node for a palette (the base palette the meshes were built with).</summary>
    public static NatureLook Create(string contentDirectory, LookPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var data = ContractSerializer.Deserialize<LookNature>(File.ReadAllText(Path.Combine(contentDirectory, "look", "nature.json")));
        var look = new NatureLook
        {
            Name = "Nature",
            Data = data,
            _palette = palette,
            _colors = palette.Colors.ToDictionary(c => c.Id, c => Rgb.Parse(c.Color), StringComparer.Ordinal),
            Season = new SeasonBlender(palette, SeasonSlots.From(palette)),
            Wind = new WindSway(data.Wind),
            _leaves = FallView.Create("Leaves", data.LeafFall),
        };
        _ = look.PaletteColor(data.Snow.Color);
        _ = look.PaletteColor(data.LeafFall.Color);
        look.AddChild(look._leaves);
        var snow = data.Snow;
        RenderingServer.GlobalShaderParameterSet("nature_snow_shape", new Vector4((float)snow.PatchM, (float)snow.Edge, (float)snow.MinUp, 0));
        RenderingServer.GlobalShaderParameterSet("nature_wind_shape", new Vector4((float)look.Wind.WaveNumber, (float)look.Wind.LeanShare, 0, 0));
        look.Apply(look.Season.Shown);
        look.ApplyWind();
        return look;
    }

    /// <summary>
    /// One frame of a running world: the season id and the weather of the snapshot, the real seconds since
    /// the last frame (0 while paused) and the camera target on the ground.
    /// </summary>
    public void Follow(string season, WeatherReading? weather, double realSeconds, Vector3 focus)
    {
        Apply(Season.Update(season, realSeconds));
        Wind.Update(weather?.Wind, realSeconds);
        ApplyWind();
        Fall(focus);
    }

    /// <summary>
    /// Screenshot tool: shows a season at once (null = the base colours), at the camera target
    /// <paramref name="focus"/>. Colours in <paramref name="keep"/> stay as the meshes have them (an epoch
    /// palette wins over the season).
    /// </summary>
    public void Show(string? season, Vector3 focus, IReadOnlySet<string>? keep = null)
    {
        Season = new SeasonBlender(_palette, Season.Slots, keep);
        Apply(Season.Jump(season));
        ApplyWind();
        Fall(focus);
        _leaves.Settle();
    }

    /// <summary>The day light that tints the unlit particles (the water light of the daylight data).</summary>
    public void Light(Rgb light)
    {
        _light = new Color(light.R, light.G, light.B);
        TintLeaves();
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
        var wind = new Vector4((float)Wind.DirectionX, (float)Wind.DirectionZ, (float)Wind.Strength, (float)Wind.Phase);
        if (wind != _windShown)
        {
            _windShown = wind;
            RenderingServer.GlobalShaderParameterSet("nature_wind", wind);
        }
    }

    private void Fall(Vector3 focus)
    {
        var direction = new Vector2((float)Wind.DirectionX, (float)Wind.DirectionZ);
        _leaves.Follow(focus, _seasonShown?.LeafFall ?? 0, direction, Wind.Strength);
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
