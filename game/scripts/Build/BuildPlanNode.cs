using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.Build;

/// <summary>
/// An invented object or building drawn from its building plan (D471 D6, B4b R14): one mesh per part,
/// shape from the construction kit, colour from the kit palette, the warm comic shader (D355). Parts
/// appear step by step while the inhabitant builds (<see cref="ShowUpToStep"/>). The plan comes from the
/// core, which has already checked it; this node only draws, it decides nothing.
/// </summary>
public partial class BuildPlanNode : Node3D
{
    private static readonly Shader ComicShader = GD.Load<Shader>("res://shaders/warm_comic.gdshader");

    private readonly List<(int Step, MeshInstance3D Mesh)> _parts = [];

    /// <summary>Number of part meshes (equals the parts of the plan).</summary>
    public int PartCount => _parts.Count;

    /// <summary>Highest building step of the plan.</summary>
    public int LastStep => _parts.Count == 0 ? 0 : _parts.Max(p => p.Step);

    /// <summary>Builds the node from a plan; unknown shapes or colours are an error (the core checks plans first).</summary>
    public static BuildPlanNode Create(BuildPlan plan, BuildKit kit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(kit);
        var colors = kit.Colors.ToDictionary(c => c.Id, c => Color.FromHtml(c.Color), StringComparer.Ordinal);
        var node = new BuildPlanNode { Name = "BuildPlan" };
        var index = 0;
        foreach (var part in plan.Parts.OrderBy(p => p.Step))
        {
            if (!kit.Shapes.Contains(part.Shape))
            {
                throw new InvalidDataException($"Part {index}: shape '{part.Shape}' is not in the construction kit.");
            }

            if (!colors.TryGetValue(part.Color, out var color))
            {
                throw new InvalidDataException($"Part {index}: colour '{part.Color}' is not in the construction kit.");
            }

            var size = new Vector3((float)part.SizeM.X, (float)part.SizeM.Y, (float)part.SizeM.Z);
            var (mesh, turn) = MeshFor(part.Shape, size);
            var material = new ShaderMaterial { Shader = ComicShader };
            material.SetShaderParameter("albedo", color);
            material.SetShaderParameter("use_vertex_color", false);
            var instance = new MeshInstance3D
            {
                Name = $"Part{index:00}_{part.Shape}",
                Mesh = mesh,
                MaterialOverride = material,
                Position = new Vector3((float)part.PositionM.X, (float)part.PositionM.Y, (float)part.PositionM.Z),
                Rotation = new Vector3(turn, Mathf.DegToRad((float)part.RotationYDeg), 0),
            };
            node.AddChild(instance);
            node._parts.Add((part.Step, instance));
            index++;
        }

        return node;
    }

    /// <summary>Shows the parts of all steps up to <paramref name="step"/> (0 = nothing yet).</summary>
    public void ShowUpToStep(int step)
    {
        foreach (var (partStep, mesh) in _parts)
        {
            mesh.Visible = partStep <= step;
        }
    }

    /// <summary>Number of visible parts.</summary>
    public int VisibleParts => _parts.Count(p => p.Mesh.Visible);

    /// <summary>Bounding box of the visible parts in the node's space.</summary>
    public Aabb VisibleBounds()
    {
        Aabb? box = null;
        foreach (var (_, mesh) in _parts.Where(p => p.Mesh.Visible))
        {
            var local = mesh.Transform * mesh.GetAabb();
            box = box is { } b ? b.Merge(local) : local;
        }

        return box ?? new Aabb();
    }

    /// <summary>
    /// Mesh for a kit shape scaled to the part size (x = width, y = height, z = depth), centred on the
    /// part position; the extra rotation about x turns shapes that Godot builds upside down.
    /// </summary>
    private static (Mesh Mesh, float TurnX) MeshFor(string shape, Vector3 size)
    {
        var radius = Mathf.Max(size.X, size.Z) / 2f;
        return shape switch
        {
            "box" or "plank" => (new BoxMesh { Size = size }, 0f),
            "cylinder" => (new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = size.Y, RadialSegments = 16 }, 0f),
            "cone" => (new CylinderMesh { TopRadius = 0f, BottomRadius = radius, Height = size.Y, RadialSegments = 16 }, 0f),
            "sphere" => (Scaled(new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 16, Rings = 8 }, size), 0f),
            "wedge" => (new PrismMesh { Size = size, LeftToRight = 0f }, 0f),
            "ring" => (new TorusMesh { InnerRadius = Mathf.Max(0.001f, radius - size.Y), OuterRadius = radius, Rings = 24, RingSegments = 8 }, 0f),
            "bowl" => (Scaled(new SphereMesh { Radius = 0.5f, Height = 1f, IsHemisphere = true, RadialSegments = 16, Rings = 6 }, size, doubleSided: true, shiftY: -0.5f), Mathf.Pi),
            _ => throw new InvalidDataException($"No mesh for kit shape '{shape}'."),
        };
    }

    /// <summary>A unit mesh baked to the given size (keeps normals usable for the comic light).</summary>
    private static ArrayMesh Scaled(PrimitiveMesh unit, Vector3 size, bool doubleSided = false, float shiftY = 0f)
    {
        var arrays = unit.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        for (var i = 0; i < vertices.Length; i++)
        {
            // A hemisphere spans 0..1 in y; shifting it centres the part on its position.
            vertices[i] = (vertices[i] + new Vector3(0, shiftY, 0)) * size;
            normals[i] = (normals[i] / size).Normalized();
        }

        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        if (doubleSided)
        {
            // Open shapes (bowl): the inside is a second surface with reversed winding and normals,
            // because the comic shader culls back faces.
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var count = vertices.Length;
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.Concat(vertices).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.Concat(normals.Select(n => -n)).ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.Concat(indices.Chunk(3).SelectMany(t => new[] { t[0] + count, t[2] + count, t[1] + count })).ToArray();
            foreach (var kind in new[] { Mesh.ArrayType.Tangent, Mesh.ArrayType.TexUV })
            {
                if (arrays[(int)kind].VariantType != Variant.Type.Nil)
                {
                    arrays[(int)kind] = kind == Mesh.ArrayType.TexUV
                        ? arrays[(int)kind].AsVector2Array().Concat(arrays[(int)kind].AsVector2Array()).ToArray()
                        : arrays[(int)kind].AsFloat32Array().Concat(arrays[(int)kind].AsFloat32Array()).ToArray();
                }
            }
        }

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
