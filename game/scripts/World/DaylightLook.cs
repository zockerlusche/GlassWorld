using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Light, sky and ambient of one day phase from <c>content/look/daylight.json</c> (island scene and the
/// main game's island mode; B2-5 blends the phases over time). Presentation only.
/// </summary>
public static class DaylightLook
{
    /// <summary>Applies the phase to the sun and the environment; an unknown phase is a data error.</summary>
    public static void Apply(LookDaylight daylight, string phase, DirectionalLight3D sun, Godot.Environment environment)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        ArgumentNullException.ThrowIfNull(sun);
        ArgumentNullException.ThrowIfNull(environment);
        var data = daylight.Phases.FirstOrDefault(p => p.Phase.ToString().Equals(phase, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Unknown day phase '{phase}' (content/look/daylight.json).");
        sun.LightColor = Color.FromHtml(data.LightColor);
        sun.LightEnergy = (float)data.LightEnergy;
        sun.RotationDegrees = new Vector3(-(float)data.SunElevationDeg, (float)daylight.SunAzimuthDeg, 0);
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = Color.FromHtml(data.SkyTop),
            SkyHorizonColor = Color.FromHtml(data.SkyHorizon),
            GroundHorizonColor = Color.FromHtml(data.SkyHorizon),
            GroundBottomColor = Color.FromHtml(data.SkyHorizon),
            SunAngleMax = 0,
        };
        environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        environment.Sky = new Sky { SkyMaterial = sky };
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightColor = Color.FromHtml(data.AmbientColor);
        environment.AmbientLightEnergy = (float)data.AmbientEnergy;
    }
}
