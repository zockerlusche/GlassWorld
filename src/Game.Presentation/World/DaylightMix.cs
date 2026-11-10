using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Light, sky and water tint of one moment (B2-5, A1367), resolved from <c>content/look/daylight.json</c>:
/// the phase's sunlight blended with the moonlight by the phase's moon value, the ambient light
/// never below <c>night_min_brightness</c>. <see cref="Lerp"/> blends two moments for calm
/// transitions at dusk and dawn. The main light is one directional light for sun and moon, so the
/// night costs no extra shadow pass. Its shadow fades (<see cref="ShadowOpacity"/>) instead of switching
/// when the sun hands over to a moon without shadows (GW3 #166). Presentation only.
/// </summary>
public sealed record DaylightMix(
    Rgb LightColor,
    double LightEnergy,
    double LightElevationDeg,
    double LightAzimuthDeg,
    double ShadowOpacity,
    Rgb SkyTop,
    Rgb SkyHorizon,
    Rgb Ambient,
    double AmbientEnergy,
    double Stars,
    double Moon,
    Rgb WaterLight)
{
    /// <summary>Whether the main light casts a shadow at all (no shadow pass when it is fully faded).</summary>
    public bool Shadows => ShadowOpacity > 0;

    /// <summary>The moment of a day phase; an unknown phase is a data error.</summary>
    public static DaylightMix Of(LookDaylight daylight, string phase)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        var data = daylight.Phases.FirstOrDefault(p => p.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Unknown day phase '{phase}' (content/look/daylight.json).");
        var moon = daylight.Moon;
        var w = data.Moon;
        var (elevation, azimuth) = Blend(data.SunElevationDeg, daylight.SunAzimuthDeg, moon.ElevationDeg, moon.AzimuthDeg, w);
        return new DaylightMix(
            Rgb.Parse(data.LightColor).Lerp(Rgb.Parse(moon.LightColor), (float)w),
            (data.LightEnergy * (1 - w)) + (moon.LightEnergy * w),
            elevation,
            azimuth,
            w < 0.5 || moon.Shadows ? 1 : 0,
            Rgb.Parse(data.SkyTop),
            Rgb.Parse(data.SkyHorizon),
            Rgb.Parse(data.AmbientColor),
            Math.Max(data.AmbientEnergy, daylight.NightMinBrightness),
            data.Stars,
            w,
            Rgb.Parse(data.WaterLight));
    }

    /// <summary>Blends towards <paramref name="other"/> (t = 0: this moment, 1: the other).</summary>
    public DaylightMix Lerp(DaylightMix other, double t)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (t <= 0 || t >= 1)
        {
            return t <= 0 ? this : other;
        }

        double Mix(double a, double b) => a + ((b - a) * t);
        var (elevation, azimuth) = Blend(LightElevationDeg, LightAzimuthDeg, other.LightElevationDeg, other.LightAzimuthDeg, t);
        return new DaylightMix(
            LightColor.Lerp(other.LightColor, (float)t),
            Mix(LightEnergy, other.LightEnergy),
            elevation,
            azimuth,
            Mix(ShadowOpacity, other.ShadowOpacity),
            SkyTop.Lerp(other.SkyTop, (float)t),
            SkyHorizon.Lerp(other.SkyHorizon, (float)t),
            Ambient.Lerp(other.Ambient, (float)t),
            Mix(AmbientEnergy, other.AmbientEnergy),
            Mix(Stars, other.Stars),
            Mix(Moon, other.Moon),
            WaterLight.Lerp(other.WaterLight, (float)t));
    }

    /// <summary>Direction between two light directions (normalised lerp of the unit vectors), as elevation and azimuth.</summary>
    private static (double Elevation, double Azimuth) Blend(double elevationA, double azimuthA, double elevationB, double azimuthB, double t)
    {
        if (t <= 0)
        {
            return (elevationA, azimuthA);
        }

        if (t >= 1)
        {
            return (elevationB, azimuthB);
        }

        static (double X, double Y, double Z) Unit(double elevation, double azimuth)
        {
            var (e, a) = (double.DegreesToRadians(elevation), double.DegreesToRadians(azimuth));
            return (Math.Cos(e) * Math.Sin(a), Math.Sin(e), Math.Cos(e) * Math.Cos(a));
        }

        var (u, v) = (Unit(elevationA, azimuthA), Unit(elevationB, azimuthB));
        var (x, y, z) = (u.X + ((v.X - u.X) * t), u.Y + ((v.Y - u.Y) * t), u.Z + ((v.Z - u.Z) * t));
        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (length < 1e-9)
        {
            return t < 0.5 ? (elevationA, azimuthA) : (elevationB, azimuthB);
        }

        var azimuth = double.RadiansToDegrees(Math.Atan2(x, z));
        return (double.RadiansToDegrees(Math.Asin(y / length)), azimuth < 0 ? azimuth + 360 : azimuth);
    }
}
