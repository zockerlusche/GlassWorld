using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;
using Godot;
using ObjectPlacement = GlassWorld.Presentation.World.ObjectPlacement;

namespace GlassWorld.Game.World;

/// <summary>
/// Grass and small plants of the island in detail levels (B2-5 R21): the decorative plants the nature data
/// names, in square chunks, one MultiMesh per look, chunk and level (<see cref="GrassPlan"/>). Godot shows
/// a chunk's levels by the camera distance to its centre (visibility ranges, hard switch with hysteresis as
/// the terrain chunks): near every plant of the quality level's density, farther the thinned ones, beyond
/// the grass distance none. Meshes and materials are those of the objects (warm comic, season colours,
/// sway); the plants cast no shadows. Built once: decorative plants never change. Presentation only.
/// </summary>
public partial class GrassView : Node3D
{
    /// <summary>Plants drawn at the near level (after the density of the quality level).</summary>
    public int Plants { get; private set; }

    /// <summary>MultiMeshes built (looks × chunks × levels with plants).</summary>
    public int Layers { get; private set; }

    /// <summary>Builds the view from the plants; meshes, materials and ground fit come from the objects view.</summary>
    public static GrassView Create(ObjectsView objects, IReadOnlyList<WorldObject> plants, Func<double, double, double> ground, NatureGrass data, QualityLevel level)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(plants);
        ArgumentNullException.ThrowIfNull(ground);
        var view = new GrassView { Name = "Grass" };
        var looks = objects.Looks;
        var layout = new Dictionary<string, List<ObjectPlacement>>(StringComparer.Ordinal);
        foreach (var plant in plants)
        {
            var look = looks.For(plant.TypeId);
            if (objects.LayerOf(look.VisualId) is null)
            {
                continue;
            }

            if (!layout.TryGetValue(look.VisualId, out var list))
            {
                layout[look.VisualId] = list = [];
            }

            list.Add(ObjectLooks.Place(plant, look, ground, looks.Fit));
        }

        var ranges = GrassPlan.Ranges(data, level);
        var margin = (float)ranges.MarginM;
        foreach (var chunk in GrassPlan.Chunks(layout, data, level))
        {
            var (mesh, material) = objects.LayerOf(chunk.VisualId)!.Value;
            view.Plants += chunk.Near.Count;

            // Near level: from the camera to the near end; far level: from there to the grass distance.
            view.Add(chunk, "near", chunk.Near, mesh, material, 0, (float)ranges.NearEndM, margin);
            if (ranges.HasFar)
            {
                view.Add(chunk, "far", chunk.Far, mesh, material, (float)ranges.NearEndM, (float)ranges.FarEndM, margin);
            }
        }

        return view;
    }

    private void Add(GrassChunk chunk, string level, IReadOnlyList<ObjectPlacement> plants, Mesh mesh, Material material, float begin, float end, float margin)
    {
        if (plants.Count == 0)
        {
            return;
        }

        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = plants.Count };
        for (var i = 0; i < plants.Count; i++)
        {
            var p = plants[i];
            var (ax, ay, az) = p.Axes();
            var basis = new Basis(new Vector3((float)ax.X, (float)ax.Y, (float)ax.Z), new Vector3((float)ay.X, (float)ay.Y, (float)ay.Z), new Vector3((float)az.X, (float)az.Y, (float)az.Z));
            multi.SetInstanceTransform(i, new Transform3D(basis, new Vector3((float)p.X, (float)p.Y, (float)p.Z)));
        }

        // The ranges are widened by the margin on their inner ends (hysteresis, see TerrainLevel.RangeBeginM).
        AddChild(new MultiMeshInstance3D
        {
            Name = $"{chunk.VisualId}_{chunk.ChunkX}_{chunk.ChunkZ}_{level}",
            Multimesh = multi,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeBegin = begin > 0 ? Math.Max(begin - margin, 0) : 0,
            VisibilityRangeBeginMargin = begin > 0 ? margin : 0,
            VisibilityRangeEnd = end + margin,
            VisibilityRangeEndMargin = margin,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
        });
        Layers++;
    }
}
