using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Light, sky and water tint of one moment (B2-5, A1367), resolved from <c>content/look/daylight.json</c>:
/// the phase's sunlight blended with the moonlight by the phase's moon value, the ambient light
/// never below <c>night_min_brightness</c>. <see cref="Lerp"/> blends two moments for calm
/// transitions at dusk and dawn. The main light is one directional light for sun and moon, so the
/// night costs no extra shadow pass. Sun and moon stand far apart in the sky, so the light never swings
/// across from one to the other (that would pass the zenith): at the hand-over (moon value 0.5) the light
/// and its shadow fade to 0, the direction switches there and fades back in (GW3 #166, #173). The ambient
/// light keeps its floor, so the world never turns black. Presentation only.
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
        var (elevation, azimuth) = w < 0.5 ? (data.SunElevationDeg, daylight.SunAzimuthDeg) : (moon.ElevationDeg, moon.AzimuthDeg);
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

    /// <summary>Whether the moon gives the main light (else the sun).</summary>
    public bool MoonLit => Moon >= 0.5;

    /// <summary>
    /// Blends towards <paramref name="other"/> (t = 0: this moment, 1: the other). Between sun and moon the
    /// main light fades out to the hand-over, jumps to the other direction there and fades in again.
    /// </summary>
    public DaylightMix Lerp(DaylightMix other, double t)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (t <= 0 || t >= 1)
        {
            return t <= 0 ? this : other;
        }

        double Mix(double a, double b) => a + ((b - a) * t);
        double energy, shadow, elevation, azimuth;
        if (MoonLit == other.MoonLit)
        {
            (energy, shadow) = (Mix(LightEnergy, other.LightEnergy), Mix(ShadowOpacity, other.ShadowOpacity));
            (elevation, azimuth) = Blend(LightElevationDeg, LightAzimuthDeg, other.LightElevationDeg, other.LightAzimuthDeg, t);
        }
        else
        {
            // The hand-over where the moon value passes 0.5; the light is 0 there.
            var handOver = Math.Clamp((0.5 - Moon) / (other.Moon - Moon), 1e-3, 1 - 1e-3);
            var (light, fade) = t < handOver ? (this, 1 - (t / handOver)) : (other, (t - handOver) / (1 - handOver));
            (energy, shadow, elevation, azimuth) = (light.LightEnergy * fade, light.ShadowOpacity * fade, light.LightElevationDeg, light.LightAzimuthDeg);
        }

        return new DaylightMix(
            LightColor.Lerp(other.LightColor, (float)t),
            energy,
            elevation,
            azimuth,
            shadow,
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
