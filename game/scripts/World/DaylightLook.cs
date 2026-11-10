using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Light, sky and ambient of one day phase from <c>content/look/daylight.json</c> (island scene and the
/// main game, which blends the phases by the world clock, see DaylightClock). Presentation only.
/// </summary>
public static class DaylightLook
{
    /// <summary>Shadow softness and tone mapping from the daylight data (the same in every phase).</summary>
    public static void ApplyRendering(LookDaylight daylight, DirectionalLight3D sun, Godot.Environment environment)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        ArgumentNullException.ThrowIfNull(sun);
        ArgumentNullException.ThrowIfNull(environment);
        sun.ShadowEnabled = true;
        sun.ShadowBlur = (float)daylight.ShadowBlur;
        environment.TonemapMode = daylight.Tonemap switch
        {
            LookDaylightTonemap.Reinhard => Godot.Environment.ToneMapper.Reinhardt,
            LookDaylightTonemap.Filmic => Godot.Environment.ToneMapper.Filmic,
            LookDaylightTonemap.Aces => Godot.Environment.ToneMapper.Aces,
            LookDaylightTonemap.Agx => Godot.Environment.ToneMapper.Agx,
            _ => Godot.Environment.ToneMapper.Linear,
        };
    }

    private static readonly Shader SkyShader = GD.Load<Shader>("res://shaders/island_sky.gdshader");

    /// <summary>Applies the phase to the sun and the environment; an unknown phase is a data error.</summary>
    public static void Apply(LookDaylight daylight, string phase, DirectionalLight3D sun, Godot.Environment environment) =>
        Apply(daylight, DaylightMix.Of(daylight, phase), sun, environment);

    /// <summary>
    /// Applies one moment (a phase or a blend of two, see <see cref="DaylightMix.Lerp"/>): one directional
    /// light for sun and moon, the sky shader with horizon, stars and moon disc, the ambient light and the
    /// tint of the unlit water shaders (global <c>water_light</c>).
    /// </summary>
    public static void Apply(LookDaylight daylight, DaylightMix mix, DirectionalLight3D sun, Godot.Environment environment)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        ArgumentNullException.ThrowIfNull(mix);
        ArgumentNullException.ThrowIfNull(sun);
        ArgumentNullException.ThrowIfNull(environment);
        sun.LightColor = ToColor(mix.LightColor);
        sun.LightEnergy = (float)mix.LightEnergy;
        sun.RotationDegrees = new Vector3(-(float)mix.LightElevationDeg, (float)mix.LightAzimuthDeg, 0);
        sun.ShadowEnabled = mix.Shadows;
        sun.ShadowOpacity = (float)mix.ShadowOpacity;

        if (environment.Sky?.SkyMaterial is not ShaderMaterial sky || sky.Shader != SkyShader)
        {
            sky = new ShaderMaterial { Shader = SkyShader };
            environment.Sky = new Sky { SkyMaterial = sky };
        }

        var (look, moon) = (daylight.Sky, daylight.Moon);
        var towardsMoon = Basis.FromEuler(new Vector3(Mathf.DegToRad(-(float)moon.ElevationDeg), Mathf.DegToRad((float)moon.AzimuthDeg), 0)).Z;
        sky.SetShaderParameter("top_color", ToColor(mix.SkyTop));
        sky.SetShaderParameter("horizon_color", ToColor(mix.SkyHorizon));
        sky.SetShaderParameter("horizon_falloff", (float)look.HorizonFalloff);
        sky.SetShaderParameter("below_horizon", (float)look.BelowHorizon);
        sky.SetShaderParameter("stars", (float)mix.Stars);
        sky.SetShaderParameter("star_density", (float)look.StarDensity);
        sky.SetShaderParameter("star_brightness", (float)look.StarBrightness);
        sky.SetShaderParameter("star_horizon_fade", (float)look.StarHorizonFade);
        sky.SetShaderParameter("moon_direction", towardsMoon);
        sky.SetShaderParameter("moon_color", Color.FromHtml(moon.DiscColor));
        sky.SetShaderParameter("moon_size_deg", (float)moon.DiscSizeDeg);
        sky.SetShaderParameter("moon_glow", (float)moon.Glow);
        sky.SetShaderParameter("moon", (float)mix.Moon);
        environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightColor = ToColor(mix.Ambient);
        environment.AmbientLightEnergy = (float)mix.AmbientEnergy;
        RenderingServer.GlobalShaderParameterSet("water_light", ToColor(mix.WaterLight));
    }

    private static Color ToColor(Rgb rgb) => new((float)rgb.R, (float)rgb.G, (float)rgb.B);
}
