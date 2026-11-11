using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The renderer side of a quality level (B2-7 R30, see <see cref="QualityPlan"/>): how the sun's shadow is
/// rendered. Whether the sun casts a shadow at all is part of the light (<see cref="QualityPlan.Light"/>),
/// whether the ink outline is drawn is decided where the materials are built (ObjectsView, FigureView).
/// The shadow map size and the filter quality are global renderer settings, not per viewport or light.
/// Presentation only.
/// </summary>
public static class QualityLook
{
    private const string AtlasSizeSetting = "rendering/lights_and_shadows/directional_shadow/size";
    private const string Atlas16BitsSetting = "rendering/lights_and_shadows/directional_shadow/16_bits";
    private const string SoftFilterSetting = "rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality";

    private static bool _fallbackReported;

    /// <summary>
    /// Applies the level's shadow rendering to the sun and the renderer (nothing when the level has no
    /// shadows) and reports an outline method that is not built yet, once per run.
    /// </summary>
    public static void Apply(QualityPlan plan, DirectionalLight3D sun)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sun);
        if (plan.Shadows is { } shadows)
        {
            sun.DirectionalShadowMode = shadows.Mode switch
            {
                ShadowRenderingMode.Orthogonal => DirectionalLight3D.ShadowMode.Orthogonal,
                ShadowRenderingMode.Parallel_2_splits => DirectionalLight3D.ShadowMode.Parallel2Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel4Splits,
            };
            sun.DirectionalShadowMaxDistance = (float)shadows.MaxDistanceM;
            RenderingServer.DirectionalShadowAtlasSetSize((int)shadows.AtlasSize, ProjectSettings.GetSetting(Atlas16BitsSetting).AsBool());
            RenderingServer.DirectionalSoftShadowFilterSetQuality(shadows.SoftFilter switch
            {
                ShadowRenderingSoftFilter.Hard => RenderingServer.ShadowQuality.Hard,
                ShadowRenderingSoftFilter.Soft_very_low => RenderingServer.ShadowQuality.SoftVeryLow,
                ShadowRenderingSoftFilter.Soft_medium => RenderingServer.ShadowQuality.SoftMedium,
                ShadowRenderingSoftFilter.Soft_high => RenderingServer.ShadowQuality.SoftHigh,
                ShadowRenderingSoftFilter.Soft_ultra => RenderingServer.ShadowQuality.SoftUltra,
                _ => RenderingServer.ShadowQuality.SoftLow,
            });
        }

        if (plan.OutlineFallback && !_fallbackReported)
        {
            _fallbackReported = true;
            GD.Print($"QUALITY_NOTE level={plan.Level.Id} outline=post_process is not available yet, drawing inverted_hull");
        }
    }

    /// <summary>Puts the global shadow settings back to the project's (leaving the world for the menu).</summary>
    public static void RestoreRenderer()
    {
        RenderingServer.DirectionalShadowAtlasSetSize(ProjectSettings.GetSetting(AtlasSizeSetting).AsInt32(), ProjectSettings.GetSetting(Atlas16BitsSetting).AsBool());
        RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)ProjectSettings.GetSetting(SoftFilterSetting).AsInt32());
    }
}
