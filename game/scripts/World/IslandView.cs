using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Map;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Draws the generated island (B2 R18–R20): a ground mesh per chunk and detail level and the water per chunk from
/// <see cref="TerrainMesher"/>, the ground in the warm comic shader with palette colours, the sea with
/// depth bands and shore foam, fresh water with the core's flow direction, and an open sea around the
/// map. Chunks are built once (the base grid never changes; only a palette change rebuilds them); Godot
/// shows each level by the camera distance to the chunk (visibility ranges, hard switch, distances from
/// <c>content/look/terrain.json</c>) and culls chunks outside the camera frustum. Presentation only.
/// </summary>
public partial class IslandView : Node3D
{
    private static readonly Shader Comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
    private static readonly Shader Sea = GD.Load<Shader>("res://shaders/sea.gdshader");
    private static readonly Shader Fresh = GD.Load<Shader>("res://shaders/island_water.gdshader");

    private WorldMap _map = null!;
    private WorldPreset _preset = null!;
    private LookTerrain _data = null!;
    private IReadOnlyList<TerrainLevel> _levels = [];
    private ShaderMaterial _groundMaterial = null!;
    private ShaderMaterial _seaMaterial = null!;
    private ShaderMaterial _freshMaterial = null!;
    private Node3D? _chunks;

    /// <summary>Number of ground chunks built.</summary>
    public int GroundChunks { get; private set; }

    /// <summary>Triangles of all ground chunks at the finest level.</summary>
    public int GroundTriangles { get; private set; }

    /// <summary>Triangles of all water surfaces (the same at every level).</summary>
    public int WaterTriangles { get; private set; }

    /// <summary>Per detail level: cell step and ground triangles of all chunks.</summary>
    public IReadOnlyList<(int CellStep, int Ground)> LevelTriangles { get; private set; } = [];

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
        var data = Read<LookTerrain>("terrain.json");
        var view = new IslandView { Name = "Island", _map = island.Map, _preset = preset, _data = data };

        view._groundMaterial = new ShaderMaterial { Shader = Comic };
        view._groundMaterial.SetShaderParameter("use_vertex_color", true);
        view._groundMaterial.SetShaderParameter("use_regions", false);
        view._seaMaterial = new ShaderMaterial { Shader = Sea };
        view._seaMaterial.SetShaderParameter("use_shore_line", false);
        var fresh = view._freshMaterial = new ShaderMaterial { Shader = Fresh };
        var shader = data.WaterShader;
        fresh.SetShaderParameter("stripe_scale", shader.StripeScale);
        fresh.SetShaderParameter("stripe_speed", shader.StripeSpeed);
        fresh.SetShaderParameter("stripe_strength", shader.StripeStrength);
        fresh.SetShaderParameter("shimmer_speed", shader.ShimmerSpeed);
        fresh.SetShaderParameter("shimmer_strength", shader.ShimmerStrength);
        view.ApplyPalette(palette);
        var look = view.Look!;
        var map = island.Map;

        // Streams as smooth bands along their lines (A1456), one mesh for the whole island.
        var streams = TerrainMesher.Streams(map, look);
        if (streams.TriangleCount > 0)
        {
            view.AddChild(new MeshInstance3D
            {
                Name = "Streams",
                Mesh = ToMesh(streams),
                MaterialOverride = fresh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            view.WaterTriangles += streams.TriangleCount;
        }

        // Open sea around the map, at sea level, a little below the edge so it never shows through.
        var width = map.WidthCells * look.CellM;
        var depth = map.DepthCells * look.CellM;
        var seaLevel = (preset.Terrain.SeaLevel * look.UnitM) - 0.01f;
        view.AddChild(new MeshInstance3D
        {
            Name = "OpenSea",
            Mesh = new PlaneMesh { Size = new Vector2(width * (float)data.OpenSeaSizeFactor, depth * (float)data.OpenSeaSizeFactor) },
            Position = new Vector3(width / 2, seaLevel, depth / 2),
            MaterialOverride = view._seaMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return view;
    }

    /// <summary>
    /// Colours the island with a palette (base, or with season and epoch overrides, B2 R19): resolves the
    /// look and builds the chunk meshes of every detail level and the water, replacing earlier ones (about
    /// 160k triangles on the start island). Models and shaders stay; the grids of the levels are made once.
    /// Meant for building the view and for the screenshot tool, which switches palettes between pictures;
    /// a run-time season or epoch blend (B2-5) should recolour the vertex colours (or move the colours into
    /// the shader) instead of rebuilding.
    /// </summary>
    public void ApplyPalette(LookPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var look = TerrainLook.From(_data, palette, _map, _preset);
        Look = look;
        _seaMaterial.SetShaderParameter("shallow_color", ToColor(look.SeaShallow));
        _seaMaterial.SetShaderParameter("middle_color", ToColor(look.SeaShallow.Lerp(look.SeaDeep, 0.5f)));
        _seaMaterial.SetShaderParameter("deep_color", ToColor(look.SeaDeep));
        _levels = _levels.Count == 0 ? TerrainLevel.From(_data.Detail, _map, look) : [.. _levels.Select(l => l.Recolored(look))];

        if (_chunks is not null)
        {
            RemoveChild(_chunks);
            _chunks.QueueFree();
        }

        _chunks = new Node3D { Name = "Chunks" };
        AddChild(_chunks);
        GroundChunks = 0;
        WaterTriangles = 0;
        var ground = new int[_levels.Count];
        for (var cz = 0; cz < _map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < _map.ChunksX; cx++)
            {
                // All levels of a chunk share one bounding box (the union of theirs), so Godot measures the
                // switch distance to the same centre for each and the levels hand over at the same place.
                var meshes = _levels.Select(l => TerrainMesher.Ground(l, cx, cz)).ToList();
                var bounds = Bounds(meshes);
                for (var i = 0; i < _levels.Count; i++)
                {
                    var level = _levels[i];
                    _chunks.AddChild(Ranged(new MeshInstance3D { Name = $"Ground_{level.CellStep}_{cx}_{cz}", Mesh = ToMesh(meshes[i]), MaterialOverride = _groundMaterial, CustomAabb = bounds }, level));
                    ground[i] += meshes[i].TriangleCount;
                }

                GroundChunks++;
            }
        }

        var counts = _levels.Select((l, i) => (l.CellStep, Ground: ground[i])).ToList();

        // The water is the same at every level (drawn from the base grid), so it fits every level's ground.
        for (var cz = 0; cz < _map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < _map.ChunksX; cx++)
            {
                foreach (var (kind, material) in new[] { (WaterKind.Salt, _seaMaterial), (WaterKind.Fresh, _freshMaterial) })
                {
                    var surface = TerrainMesher.Water(_levels[0], cx, cz, kind);
                    if (surface.TriangleCount == 0)
                    {
                        continue;
                    }

                    _chunks.AddChild(new MeshInstance3D
                    {
                        Name = $"{kind}_{cx}_{cz}",
                        Mesh = ToMesh(surface),
                        MaterialOverride = material,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
                    WaterTriangles += surface.TriangleCount;
                }
            }
        }

        LevelTriangles = counts;
        GroundTriangles = counts[0].Ground;
    }

    /// <summary>
    /// Ground height in metres at a world position: the drawn ground mesh, interpolated over its triangles
    /// (for placing figures and objects and for the camera target).
    /// </summary>
    public static float GroundHeight(WorldMap map, TerrainLook look, double x, double z) => TerrainMesher.GroundHeight(map, look, x, z);

    /// <summary>
    /// Shows a chunk mesh only within its level's distances (Godot measures from the camera to the
    /// chunk's centre), without fade but with the switch hysteresis (see <see cref="TerrainLevel"/>): the
    /// fade dithers and moves the chunk into the transparent pass, where it was drawn over the sea.
    /// </summary>
    private static MeshInstance3D Ranged(MeshInstance3D instance, TerrainLevel level)
    {
        instance.VisibilityRangeBegin = level.RangeBeginM;
        instance.VisibilityRangeEnd = level.RangeEndM;
        instance.VisibilityRangeBeginMargin = level.RangeBeginM > 0 ? level.SwitchMarginM : 0;
        instance.VisibilityRangeEndMargin = level.RangeEndM > 0 ? level.SwitchMarginM : 0;
        instance.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
        return instance;
    }

    /// <summary>The bounding box around all vertices of the meshes.</summary>
    private static Aabb Bounds(IEnumerable<MeshData> meshes)
    {
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        foreach (var mesh in meshes)
        {
            for (var i = 0; i < mesh.Positions.Count; i += 3)
            {
                var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
                (min, max) = (min.Min(p), max.Max(p));
            }
        }

        return new Aabb(min, max - min);
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
