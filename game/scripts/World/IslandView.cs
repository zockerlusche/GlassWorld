using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Map;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Draws the generated island (B2 R18–R20): a ground mesh per chunk and detail level and the water per chunk from
/// <see cref="TerrainMesher"/>, the ground in the warm comic shader with palette colours, the sea with
/// depth bands and shore foam, fresh water with the core's flow direction (streams as bands with flow
/// lines, W1724), wave crests on sea and lakes by the weather (<c>waves</c> of the terrain look; the amount
/// is a global shader parameter <see cref="NatureLook"/> sets), and an open sea around the
/// map. The sea follows the tide (B2-5 R15): all salt water hangs under one node that moves up and down
/// with the level of the snapshot, glided in real time (<see cref="TideFollower"/>); the sea shader reads
/// the water depth from the picture, so depth bands and shore foam follow by themselves, and the open sea
/// plane under the island wets the beach at high water. Fresh water does not move. Chunks are built once (the base grid never changes; only a palette change rebuilds them); Godot
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
    private LookEpochs _epochs = null!;
    private Node3D _sea = null!;
    private Node3D? _salt;

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

    /// <summary>The shown tide level (height units against the mean sea level).</summary>
    public TideFollower Tide { get; private set; } = null!;

    /// <summary>Builds the view for an island; content from <paramref name="contentDirectory"/>.</summary>
    public static IslandView Create(WorldMapSystem island, WorldPreset preset, string contentDirectory)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(preset);
        T Read<T>(string file)
            where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(contentDirectory, "look", file)));
        var palette = Read<LookPalette>("palette.json");
        var data = Read<LookTerrain>("terrain.json");
        var view = new IslandView
        {
            Name = "Island",
            _map = island.Map,
            _preset = preset,
            _data = data,
            _epochs = Read<LookEpochs>("epochs.json"),
            _sea = new Node3D { Name = "Sea" },
            Tide = new TideFollower(data.TideFollowRealSeconds ?? 0),
        };
        view.AddChild(view._sea);

        view._groundMaterial = new ShaderMaterial { Shader = Comic };
        view._groundMaterial.SetShaderParameter("use_vertex_color", true);
        view._groundMaterial.SetShaderParameter("use_regions", false);
        view._groundMaterial.SetShaderParameter("use_nature", true);
        view._seaMaterial = new ShaderMaterial { Shader = Sea };
        view._seaMaterial.SetShaderParameter("use_shore_line", false);
        var fresh = view._freshMaterial = new ShaderMaterial { Shader = Fresh };
        var shader = data.WaterShader;
        fresh.SetShaderParameter("flow_lanes", shader.FlowLanes);
        fresh.SetShaderParameter("flow_line_length", shader.FlowLineLengthM);
        fresh.SetShaderParameter("flow_speed", shader.FlowSpeedMPerS);
        fresh.SetShaderParameter("flow_speed_spread", shader.FlowSpeedSpread);
        fresh.SetShaderParameter("flow_bend", shader.FlowBend);
        fresh.SetShaderParameter("flow_strength", shader.FlowStrength);
        fresh.SetShaderParameter("shimmer_speed", shader.ShimmerSpeed);
        fresh.SetShaderParameter("shimmer_strength", shader.ShimmerStrength);
        ApplyWaves(data.Waves, view._seaMaterial, fresh);
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
        view._sea.AddChild(new MeshInstance3D
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
    /// The wave crests of the terrain look (W1724) as parameters of the sea and the fresh water shader;
    /// without the data no crest is ever drawn.
    /// </summary>
    private static void ApplyWaves(TerrainWaves? waves, ShaderMaterial sea, ShaderMaterial fresh)
    {
        foreach (var material in new[] { sea, fresh })
        {
            material.SetShaderParameter("wave_strength", waves?.Strength ?? 0);
            if (waves is null)
            {
                continue;
            }

            foreach (var (name, layer) in new[] { ("wave_small", waves.Small), ("wave_large", waves.Large) })
            {
                if (layer.FullAt <= layer.StartsAt)
                {
                    throw new InvalidDataException($"content/look/terrain.json waves: full_at ({layer.FullAt}) must lie above starts_at ({layer.StartsAt}).");
                }

                // The speed is not the shader's business: the game sums the drift up (WaveDrift, NatureLook).
                material.SetShaderParameter(name, new Vector3((float)layer.SpacingM, (float)layer.LengthM, (float)layer.ThicknessM));
                material.SetShaderParameter(name + "_range", new Vector3((float)layer.Density, (float)layer.StartsAt, (float)layer.FullAt));
            }
        }

        if (waves is not null)
        {
            sea.SetShaderParameter("wave_shore_foam", waves.ShoreFoamGain);
            fresh.SetShaderParameter("wave_share", waves.LakeShare);
        }
    }

    /// <summary>
    /// Colours the island with a palette (B2 R19): resolves the look and builds the chunk meshes of every
    /// detail level and the water, replacing earlier ones (about 160k triangles on the start island).
    /// Models and shaders stay; the grids of the levels are made once. Meant for building the view.
    /// Seasons and look epochs never rebuild (B2-5 R24, R16): the ground carries per vertex the shares of
    /// the palette colours they change (<see cref="SeasonSlots"/>, CUSTOM0 and CUSTOM1), and the shader
    /// moves them to the colour shown now from global parameters (<see cref="NatureLook"/>).
    /// </summary>
    public void ApplyPalette(LookPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var look = TerrainLook.From(_data, palette, _map, _preset, _epochs);
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

        if (_salt is not null)
        {
            _sea.RemoveChild(_salt);
            _salt.QueueFree();
        }

        _chunks = new Node3D { Name = "Chunks" };
        AddChild(_chunks);

        // The salt water hangs under the sea node, which the tide moves; fresh water stays with the ground.
        _salt = new Node3D { Name = "Salt" };
        _sea.AddChild(_salt);
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
                    _chunks.AddChild(Ranged(new MeshInstance3D { Name = $"Ground_{level.CellStep}_{cx}_{cz}", Mesh = ToMesh(meshes[i], tints: true), MaterialOverride = _groundMaterial, CustomAabb = bounds }, level));
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

                    (kind == WaterKind.Salt ? _salt : _chunks).AddChild(new MeshInstance3D
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
    /// One frame of a running world (B2-5 R15): the sea surface glides towards the tide of the snapshot in
    /// real time (0 seconds while the game is paused; null without tides), so it is smooth at x16 too. Only
    /// one node moves; no mesh is rebuilt. Without tides the sea stays exactly at the mean sea level.
    /// </summary>
    public void FollowTide(TideReading? reading, double realSeconds) => PlaceSea(Tide.Update(reading, realSeconds));

    /// <summary>Shows a tide level at once, in height units against the mean sea level (the screenshot tool).</summary>
    public void ShowTide(double units) => PlaceSea(Tide.Jump(units));

    private void PlaceSea(double units)
    {
        var y = (float)(units * Look!.UnitM);
        if (!_sea.Position.Y.Equals(y))
        {
            _sea.Position = new Vector3(0, y, 0);
        }
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
    /// triangle is reversed; vertex colours go from sRGB palette values to linear for the shader. With
    /// <paramref name="tints"/> the shares of the movable colours go into CUSTOM0 and CUSTOM1 (four bytes
    /// per vertex each: tint slots 0..3 and 4..7). A stream band's places (<see cref="MeshData.Band"/>:
    /// metres along, -1..1 across) go into UV2 for the flow lines of the fresh water shader.
    /// </summary>
    private static ArrayMesh ToMesh(MeshData data, bool tints = false)
    {
        var count = data.VertexCount;
        var weights = new byte[tints ? count * 4 : 0];
        var more = new byte[tints ? count * 4 : 0];
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var colors = new Color[count];
        var flow = new Vector2[count];
        var band = data.Band.Count == count * 2 ? new Vector2[count] : null;
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
                if (band is not null)
                {
                    band[o] = new Vector2(data.Band[i * 2], data.Band[(i * 2) + 1]);
                }

                for (var s = 0; tints && s < SeasonSlots.Max; s++)
                {
                    (s < 4 ? weights : more)[(o * 4) + (s % 4)] = (byte)Math.Clamp((int)MathF.Round(data.Tints[(i * SeasonSlots.Max) + s] * 255), 0, 255);
                }
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = flow;
        if (band is not null)
        {
            arrays[(int)Mesh.ArrayType.TexUV2] = band;
        }

        if (tints)
        {
            // The default custom format is four unsigned bytes (RGBA8), read as 0..1 in the shader.
            arrays[(int)Mesh.ArrayType.Custom0] = weights;
            arrays[(int)Mesh.ArrayType.Custom1] = more;
        }

        var mesh = new ArrayMesh();
        if (count > 0)
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        return mesh;
    }
}
