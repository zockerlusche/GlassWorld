using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Map;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Draws the generated island (B2 R18–R20): one ground and one water mesh per chunk from
/// <see cref="TerrainMesher"/>, the ground in the warm comic shader with palette colours, the sea with
/// depth bands and shore foam, fresh water with the core's flow direction, and an open sea around the
/// map. Chunks are built once (the base grid never changes); Godot culls them by the camera frustum.
/// Presentation only.
/// </summary>
public partial class IslandView : Node3D
{
    private static readonly Shader Comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
    private static readonly Shader Sea = GD.Load<Shader>("res://shaders/sea.gdshader");
    private static readonly Shader Fresh = GD.Load<Shader>("res://shaders/island_water.gdshader");

    /// <summary>Number of ground chunks built.</summary>
    public int GroundChunks { get; private set; }

    /// <summary>Triangles of all ground chunks.</summary>
    public int GroundTriangles { get; private set; }

    /// <summary>Triangles of all water surfaces.</summary>
    public int WaterTriangles { get; private set; }

    /// <summary>Look resolved for this island.</summary>
    public TerrainLook? Look { get; private set; }

    /// <summary>Builds the view for an island; content from <paramref name="contentDirectory"/>.</summary>
    public static IslandView Create(WorldMapSystem island, WorldPreset preset, string contentDirectory)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(preset);
        T Read<T>(string file)
            where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(contentDirectory, "look", file)));
        var palette = Read<LookPalette>("palette.json");
        var look = TerrainLook.From(Read<LookTerrain>("terrain.json"), palette, island.Map, preset);
        var view = new IslandView { Name = "Island", Look = look };

        var ground = new ShaderMaterial { Shader = Comic };
        ground.SetShaderParameter("use_vertex_color", true);
        ground.SetShaderParameter("use_regions", false);
        var sea = SeaMaterial(look);
        var fresh = new ShaderMaterial { Shader = Fresh };
        var map = island.Map;
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var terrain = TerrainMesher.Ground(map, cx, cz, look);
                view.AddChild(new MeshInstance3D { Name = $"Ground_{cx}_{cz}", Mesh = ToMesh(terrain), MaterialOverride = ground });
                view.GroundChunks++;
                view.GroundTriangles += terrain.TriangleCount;
                foreach (var (kind, material) in new[] { (WaterKind.Salt, (Material)sea), (WaterKind.Fresh, fresh) })
                {
                    var water = TerrainMesher.Water(map, cx, cz, look, kind);
                    if (water.TriangleCount == 0)
                    {
                        continue;
                    }

                    view.AddChild(new MeshInstance3D
                    {
                        Name = $"{kind}_{cx}_{cz}",
                        Mesh = ToMesh(water),
                        MaterialOverride = material,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
                    view.WaterTriangles += water.TriangleCount;
                }
            }
        }

        // Open sea around the map, at sea level, a little below the edge so it never shows through.
        var width = map.WidthCells * look.CellM;
        var depth = map.DepthCells * look.CellM;
        var seaLevel = (preset.Terrain.SeaLevel * look.UnitM) - 0.01f;
        view.AddChild(new MeshInstance3D
        {
            Name = "OpenSea",
            Mesh = new PlaneMesh { Size = new Vector2(width * 8, depth * 8) },
            Position = new Vector3(width / 2, seaLevel, depth / 2),
            MaterialOverride = sea,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return view;
    }

    /// <summary>Ground height in metres at a world position (for placing figures and objects).</summary>
    public static float GroundHeight(WorldMap map, TerrainLook look, double x, double z)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var cx = Math.Clamp((int)Math.Floor(x / look.CellM), 0, map.WidthCells - 1);
        var cz = Math.Clamp((int)Math.Floor(z / look.CellM), 0, map.DepthCells - 1);
        var cell = map.Cell(cx, cz);
        return (cell.Height + cell.WaterDepth) * look.UnitM;
    }

    private static ShaderMaterial SeaMaterial(TerrainLook look)
    {
        var material = new ShaderMaterial { Shader = Sea };
        material.SetShaderParameter("use_shore_line", false);
        material.SetShaderParameter("shallow_color", ToColor(look.SeaShallow));
        material.SetShaderParameter("middle_color", ToColor(look.SeaShallow.Lerp(look.SeaDeep, 0.5f)));
        material.SetShaderParameter("deep_color", ToColor(look.SeaDeep));
        return material;
    }

    private static Color ToColor(Rgb c) => new(c.R, c.G, c.B);

    /// <summary>
    /// Converts mesher output to an ArrayMesh. Godot treats clockwise triangles as front faces, so each
    /// triangle is reversed; vertex colours go from sRGB palette values to linear for the shader.
    /// </summary>
    private static ArrayMesh ToMesh(MeshData data)
    {
        var count = data.VertexCount;
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var colors = new Color[count];
        var flow = new Vector2[count];
        for (var t = 0; t < count; t += 3)
        {
            // a, b, c → a, c, b
            int[] order = [t, t + 2, t + 1];
            for (var k = 0; k < 3; k++)
            {
                var i = order[k];
                var o = t + k;
                vertices[o] = new Vector3(data.Positions[i * 3], data.Positions[(i * 3) + 1], data.Positions[(i * 3) + 2]);
                normals[o] = new Vector3(data.Normals[i * 3], data.Normals[(i * 3) + 1], data.Normals[(i * 3) + 2]);
                colors[o] = new Color(data.Colors[i * 3], data.Colors[(i * 3) + 1], data.Colors[(i * 3) + 2]).SrgbToLinear();
                flow[o] = new Vector2(data.Flow[i * 2], data.Flow[(i * 2) + 1]);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = flow;
        var mesh = new ArrayMesh();
        if (count > 0)
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        return mesh;
    }
}
