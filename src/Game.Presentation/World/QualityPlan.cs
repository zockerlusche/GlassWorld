using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// What a quality level of <c>content/look/quality.json</c> means for the views (B2-7 R30): the grass
/// (<see cref="Level"/>, see <see cref="GrassPlan"/>), the sun's shadow and the ink outline. The level is
/// picked by <see cref="GrassPlan.Level(LookQuality, string?)"/> only. Presentation only.
/// </summary>
/// <param name="Level">The quality level (grass density and distance).</param>
/// <param name="Shadows">How the sun's shadow is rendered, or null when the level draws no shadows.</param>
/// <param name="HullOutline">Whether objects and figures get the inverted-hull ink outline.</param>
/// <param name="OutlineFallback">
/// The level asks for an outline method that is not built yet (<c>post_process</c>): the inverted hull is
/// drawn instead, and the view says so once.
/// </param>
public sealed record QualityPlan(QualityLevel Level, ShadowRendering? Shadows, bool HullOutline, bool OutlineFallback)
{
    /// <summary>The plan of level <paramref name="id"/> (e.g. <c>--quality=ID</c>), or of the default level when null; an unknown level is a data error.</summary>
    public static QualityPlan From(LookQuality quality, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(quality);
        var level = GrassPlan.Level(quality, id);
        var shadows = level.Shadows switch
        {
            QualityLevelShadows.Off => null,
            QualityLevelShadows.Low => quality.ShadowSettings.Low,
            QualityLevelShadows.High => quality.ShadowSettings.High,
            _ => throw new InvalidDataException($"Quality level '{level.Id}': unknown shadow quality '{level.Shadows}' (content/look/quality.json)."),
        };
        var (hull, fallback) = level.Outline switch
        {
            QualityLevelOutline.Off => (false, false),
            QualityLevelOutline.Inverted_hull => (true, false),

            // No post-process outline exists yet: the hull keeps the comic look until it does.
            QualityLevelOutline.Post_process => (true, true),
            _ => throw new InvalidDataException($"Quality level '{level.Id}': unknown outline method '{level.Outline}' (content/look/quality.json)."),
        };
        return new QualityPlan(level, shadows, hull, fallback);
    }

    /// <summary>The level in one log line: what is drawn (shot tool, benchmark, smoke).</summary>
    public string Describe()
    {
        var shadows = Level.Shadows switch
        {
            QualityLevelShadows.Off => "off",
            QualityLevelShadows.Low => "low",
            _ => "high",
        };
        return $"level={Level.Id} shadows={shadows} outline={(HullOutline ? "inverted_hull" : "off")}";
    }

    /// <summary>
    /// The light of a moment at this level: without shadows the main light casts none in any day phase.
    /// The returned moment has <see cref="DaylightMix.Shadows"/> false (it is derived from the shadow
    /// opacity), so the view switches the light's shadow off and the renderer has no shadow pass to pay
    /// for. A moment that has no shadow anyway (daylight data, weather) stays as it is.
    /// </summary>
    public DaylightMix Light(DaylightMix mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        return Shadows is null && mix.Shadows ? mix with { ShadowOpacity = 0 } : mix;
    }
}
