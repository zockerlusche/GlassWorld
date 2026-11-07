using System.Globalization;
using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Look prototype A1435 (Marcel W1607/W1609): a small island piece and three figures in the style of
/// D355 – warm flat colour areas, soft light steps, faceted trees and rocks, soft depth bands in the
/// sea – with fluid motion (idle → walk cross-fade, procedural wave). Presentation only, no simulation.
/// <para>User arguments (after <c>--</c>):</para>
/// <list type="bullet">
/// <item><c>--variant=flat|ink|grain</c> flat like D355 (default), with ink outlines, or with paper grain.</item>
/// <item><c>--shots=DIR</c> window run: saves overview and close-up PNGs into DIR, then quits.</item>
/// <item><c>--follow</c> camera follows the walking figure (for the video via <c>--write-movie</c>).</item>
/// </list>
/// Headless (CI, no window) it builds everything, runs 120 frames and prints <c>LOOK_OK</c>.
/// </summary>
public partial class LookPrototype : Node3D
{
    private const float WalkSpeed = 1.15f;
    private const float CycleSeconds = 22f;

    private readonly List<Figure> _figures = [];
    private LookData? _look;
    private Camera3D? _camera;
    private Figure? _walker;
    private Figure? _waver;
    private Vector3 _walkerPosition;
    private float _walkerAngle;
    private float _walkerBlend;
    private double _time;
    private int _frame;
    private string _variant = "flat";
    private string? _shotDirectory;
    private bool _follow;
    private int _shotStep;

    /// <inheritdoc/>
    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--variant=", StringComparison.Ordinal))
            {
                _variant = arg["--variant=".Length..];
            }
            else if (arg.StartsWith("--shots=", StringComparison.Ordinal))
            {
                _shotDirectory = arg["--shots=".Length..];
            }
            else if (arg == "--follow")
            {
                _follow = true;
            }
        }

        var content = ContentLocator.ContentDirectory();
        _look = LookData.Load(content);
        var comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
        var outline = _variant == "ink" ? GD.Load<Shader>("res://shaders/outline_hull.gdshader") : null;

        BuildLight();
        BuildLand(comic, outline);
        BuildFigures(comic, outline);
        BuildCamera();
        if (_variant == "grain")
        {
            var layer = new CanvasLayer { Name = "PaperGrain" };
            AddChild(layer);
            layer.AddChild(new ColorRect
            {
                Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/paper_grain.gdshader") },
                AnchorRight = 1,
                AnchorBottom = 1,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }

        GD.Print($"LOOK_READY variant={_variant} figures={_figures.Count} clips={string.Join(',', FigureFactory.Clips)}");
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _time += delta;
        _frame++;
        AnimateWalker((float)delta);
        AnimateWaver();
        if (_follow && _camera is not null && _walker is not null)
        {
            var target = _walker.Root.GlobalPosition + new Vector3(0, 0.9f, 0);
            var wanted = target + new Vector3(4.5f, 5.5f, 7.5f);
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(wanted, 1 - Mathf.Exp(-3f * (float)delta));
            _camera.LookAt(target, Vector3.Up);
        }

        if (_shotDirectory is not null)
        {
            TakeShots();
        }
        else if (DisplayServer.GetName() == "headless" && _frame == 120)
        {
            var sk = _walker!.Root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
            var arm = sk.FindBone("DEF-upper_arm.L");
            var animated = !sk.GetBonePoseRotation(arm).IsEqualApprox(sk.GetBoneRest(arm).Basis.GetRotationQuaternion());
            if (!animated)
            {
                GD.PushError("LOOK_FAIL figures are not animated");
            }
            GD.Print($"LOOK_OK frames={_frame} variant={_variant} animated={animated}");
            GetTree().Quit();
        }
    }

    private void BuildLight()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = _look!.Srgb("sea_deep"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(1f, 0.97f, 0.92f),
            AmbientLightEnergy = 0.5f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = environment });
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 0.58f,
            LightColor = new Color(1f, 0.96f, 0.88f),
            ShadowEnabled = true,
            ShadowBlur = 2.5f,
            DirectionalShadowMaxDistance = 80,
        };
        sun.Basis = Basis.LookingAt(new Vector3(-0.55f, -1f, 0.45f).Normalized(), Vector3.Up);
        AddChild(sun);
    }

    private ShaderMaterial Comic(Shader comic, Shader? outline)
    {
        var ramp = _look!.Palette.Ramp.OrderBy(r => r.Threshold).ToList();
        var material = new ShaderMaterial { Shader = comic };
        material.SetShaderParameter("shade_dark", (float)ramp[0].Brightness);
        material.SetShaderParameter("shade_mid", (float)ramp[Math.Min(1, ramp.Count - 1)].Brightness);
        material.SetShaderParameter("step_mid", (float)ramp[Math.Min(1, ramp.Count - 1)].Threshold);
        material.SetShaderParameter("step_lit", (float)ramp[^1].Threshold);
        material.SetShaderParameter("shadow_tint", _look.Srgb("shadow_tint"));
        if (outline is not null)
        {
            // Props are large; a hull of 4 cm reads as an ink line at the overview distance.
            var ink = new ShaderMaterial { Shader = outline };
            ink.SetShaderParameter("width", 0.04f);
            material.NextPass = ink;
        }

        return material;
    }

    private void BuildLand(Shader comic, Shader? outline)
    {
        var look = _look!;
        var ground = new MeshInstance3D { Name = "Ground", Mesh = IslandPiece.Ground(look, new Rect2(-34, -24, 80, 48), 0.2f), MaterialOverride = Comic(comic, null) };
        AddChild(ground);

        var sea = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sea.gdshader") };
        sea.SetShaderParameter("foam_color", look.Srgb("sea_foam"));
        sea.SetShaderParameter("shallow_color", look.Srgb("sea_shallow"));
        sea.SetShaderParameter("middle_color", look.Srgb("sea_deep"));
        sea.SetShaderParameter("deep_color", look.Srgb("sea_deep").Darkened(0.04f));
        AddChild(new MeshInstance3D { Name = "Sea", Mesh = new PlaneMesh { Size = new Vector2(80, 60), SubdivideWidth = 1 }, Position = new Vector3(30, 0, 0), MaterialOverride = sea });

        var stream = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/stream.gdshader") };
        stream.SetShaderParameter("water_color", look.Srgb("fresh_water"));
        AddChild(new MeshInstance3D { Name = "Stream", Mesh = IslandPiece.StreamRibbon(1.9f), MaterialOverride = stream });

        var props = Comic(comic, outline);
        var trees = new (float X, float Z, float S)[]
        {
            (-12.5f, -5.5f, 1f), (-13.5f, 0, 1.1f), (-11, 5.5f, 0.95f), (-13, 11, 1.15f), (-7.5f, 13, 0.9f), (-16, -3, 1f),
            (-16, 7, 1.1f), (-5, -12.8f, 0.85f), (-9, -15.5f, 1f), (-14, -15.5f, 1.05f), (-18, 1.5f, 1.1f),
        };
        foreach (var (x, z, s) in trees.Where(t => IslandPiece.OnGrass(t.X, t.Z, 1f)))
        {
            AddChild(Tree(look, props, new Vector3(x, IslandPiece.Height(x, z), z), s));
        }

        var rocks = new (float X, float Z, Vector3 Size, int Seed)[]
        {
            (-8.5f, 4.5f, new Vector3(2.4f, 1.5f, 2f), 3), (5.2f, -10.5f, new Vector3(2.2f, 1.4f, 1.9f), 7),
            (-4, 10, new Vector3(0.7f, 0.45f, 0.6f), 11), (-15, -5, new Vector3(0.6f, 0.4f, 0.5f), 13),
        };
        foreach (var (x, z, size, seed) in rocks)
        {
            var rock = new MeshInstance3D { Mesh = IslandPiece.Rock(seed, size, look.Linear("rock")), MaterialOverride = props };
            rock.Position = new Vector3(x, IslandPiece.Height(x, z) - 0.1f, z);
            rock.RotationDegrees = new Vector3(0, seed * 37 % 360, 0);
            AddChild(rock);
        }

        AddChild(Scatter("Tufts", BladeMesh(look.Linear("grass_edge").Darkened(0.12f)), 320, 1, props, 0.3f));
        AddChild(Scatter("Flowers", new SphereMesh { Radius = 0.09f, Height = 0.06f, RadialSegments = 5, Rings = 2 }, 260, 2, Tinted(props, look.Srgb("flower")), 0.4f));
        AddChild(Bush(look, props, new Vector3(-9, IslandPiece.Height(-9, 13.5f), 13.5f)));
    }

    private static Node3D Tree(LookData look, ShaderMaterial material, Vector3 at, float scale)
    {
        var tree = new Node3D { Position = at, Scale = Vector3.One * scale };
        tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.38f, Height = 1.9f, RadialSegments = 6 }, Position = new Vector3(0, 0.95f, 0), MaterialOverride = Tinted(material, look.Srgb("bark")) });
        var layers = new (float R, float H, float Y, string C)[] { (2.0f, 2.3f, 1.5f, "foliage_dark"), (1.6f, 2.0f, 2.7f, "foliage"), (1.1f, 1.7f, 3.8f, "foliage_dark") };
        foreach (var (r, h, y, c) in layers)
        {
            tree.AddChild(new MeshInstance3D { Mesh = IslandPiece.Cone(r, h, 7, look.Linear(c), y), MaterialOverride = material });
        }

        return tree;
    }

    private static Node3D Bush(LookData look, ShaderMaterial material, Vector3 at)
    {
        var bush = new Node3D { Position = at };
        bush.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.9f, Height = 1.2f, RadialSegments = 8, Rings = 4 }, Position = new Vector3(0, 0.4f, 0), MaterialOverride = Tinted(material, look.Srgb("bush")) });
        for (var i = 0; i < 9; i++)
        {
            var a = i * 2.4f;
            bush.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.14f, Height = 0.28f, RadialSegments = 6, Rings = 3 },
                Position = new Vector3(Mathf.Cos(a) * 0.75f, 0.55f + (0.25f * Mathf.Sin(a * 1.7f)), Mathf.Sin(a) * 0.6f),
                MaterialOverride = Tinted(material, look.Srgb("berry")),
            });
        }

        return bush;
    }

    private static ShaderMaterial Tinted(ShaderMaterial material, Color srgb)
    {
        var copy = (ShaderMaterial)material.Duplicate();
        copy.SetShaderParameter("use_vertex_color", false);
        copy.SetShaderParameter("albedo", srgb);
        return copy;
    }

    private static ArrayMesh BladeMesh(Color color)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetColor(color);
        for (var i = 0; i < 4; i++)
        {
            var a = (i * 1.3f) - 2f;
            var lean = new Vector3(Mathf.Sin(a) * 0.12f, 0, Mathf.Cos(a) * 0.05f);
            var side = new Vector3(Mathf.Cos(a), 0, -Mathf.Sin(a)) * 0.035f;
            tool.AddVertex(-side);
            tool.AddVertex(side);
            tool.AddVertex(new Vector3(0, 0.32f + (0.08f * (i % 2)), 0) + lean);
        }

        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>MultiMesh scatter on the grass with a deterministic hash (presentation only).</summary>
    private static MultiMeshInstance3D Scatter(string name, Mesh mesh, int count, int salt, Material material, float margin)
    {
        var placed = new List<Transform3D>();
        for (var i = 0; placed.Count < count && i < count * 20; i++)
        {
            var x = -32 + (IslandPiece.Hash((i * 7919) + salt) * 44);
            var z = -22 + (IslandPiece.Hash((i * 104729) + (salt * 31)) * 44);
            if (!IslandPiece.OnGrass(x, z, margin))
            {
                continue;
            }

            var basis = Basis.FromEuler(new Vector3(0, IslandPiece.Hash(i + (salt * 977)) * Mathf.Tau, 0));
            placed.Add(new Transform3D(basis, new Vector3(x, IslandPiece.Height(x, z), z)));
        }

        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = placed.Count };
        for (var i = 0; i < placed.Count; i++)
        {
            multi.SetInstanceTransform(i, placed[i]);
        }

        return new MultiMeshInstance3D { Name = name, Multimesh = multi, MaterialOverride = material };
    }

    private void BuildFigures(Shader comic, Shader? outline)
    {
        var a = _look!.Appearance;
        Color Pick(ICollection<string> colors, int index) => new(colors.ElementAt(index % colors.Count));
        var shoes = Pick(a.HairColors, 4);
        var woman = new FigureLook(Pick(a.SkinTones, 1), Pick(a.ClothingColors, 2), Pick(a.ClothingColors, 0), shoes, Pick(a.HairColors, 0), LongHair: true, ShortSleeves: true, Shorts: true, Dress: false, Scale: 0.95f, HeadScale: 1.6f);
        var man = new FigureLook(Pick(a.SkinTones, 2), Pick(a.ClothingColors, 1), Pick(a.ClothingColors, 4), shoes, Pick(a.HairColors, 1), LongHair: false, ShortSleeves: true, Shorts: false, Dress: false, Scale: 1f, HeadScale: 1.55f);
        var child = new FigureLook(Pick(a.SkinTones, 1), Pick(a.ClothingColors, 2), Pick(a.ClothingColors, 3), shoes, Pick(a.HairColors, 2), LongHair: false, ShortSleeves: true, Shorts: true, Dress: false, Scale: 0.62f, HeadScale: 1.85f);

        var facing = new Vector3(0.75f, 0, -0.65f).Normalized();
        Place(FigureFactory.Build(woman, comic, outline), new Vector3(1.4f, 0, 3.0f), facing, out var w);
        Place(FigureFactory.Build(child, comic, outline), new Vector3(3.2f, 0, 3.7f), facing, out _);
        _waver = w;

        _walker = FigureFactory.Build(man, comic, outline);
        AddChild(_walker.Root);
        _figures.Add(_walker);
        _walkerPosition = WalkerPath(0);
        UpdateWalker(0, Vector3.Forward);
    }

    private void Place(Figure figure, Vector3 at, Vector3 facing, out Figure placed)
    {
        figure.Root.Position = new Vector3(at.X, IslandPiece.Height(at.X, at.Z), at.Z);
        figure.Root.Rotation = new Vector3(0, Mathf.Atan2(facing.X, facing.Z), 0);
        AddChild(figure.Root);
        _figures.Add(figure);
        placed = figure;
    }

    /// <summary>Walking loop on the beach: an ellipse around the group.</summary>
    private static Vector3 WalkerPath(float angle) => new(2.2f + (4.2f * Mathf.Cos(angle)), 0, 1.5f + (6.5f * Mathf.Sin(angle)));

    /// <summary>
    /// 22 s cycle: walk, slow down, stand, wave, start again – every change is a cross-fade in the
    /// blend space, so legs never snap between poses.
    /// </summary>
    private void AnimateWalker(float delta)
    {
        if (_walker is null)
        {
            return;
        }

        var t = (float)(_time % CycleSeconds);
        var targetBlend = t switch
        {
            < 8f => 1f,
            < 9f => 1f - (t - 8f),
            < 15f => 0f,
            < 16f => t - 15f,
            _ => 1f,
        };
        _walkerBlend = Mathf.MoveToward(_walkerBlend, targetBlend, delta * 1.5f);
        _walker.Wave.Weight = Mathf.MoveToward(_walker.Wave.Weight, t is > 10.5f and < 13.5f ? 1f : 0f, delta * 2.5f);
        var speed = WalkSpeed * _walkerBlend;
        var previous = _walkerPosition;
        _walkerAngle += speed * delta / 5.4f;
        _walkerPosition = WalkerPath(_walkerAngle);
        UpdateWalker(_walkerBlend, _walkerPosition - previous);
    }

    private void UpdateWalker(float blend, Vector3 moved)
    {
        var walker = _walker!;
        FigureFactory.SetMotion(walker, blend);
        walker.Root.Position = new Vector3(_walkerPosition.X, IslandPiece.Height(_walkerPosition.X, _walkerPosition.Z), _walkerPosition.Z);
        if (moved.LengthSquared() > 1e-8f)
        {
            var yaw = Mathf.Atan2(moved.X, moved.Z);
            walker.Root.Rotation = new Vector3(0, Mathf.LerpAngle(walker.Root.Rotation.Y, yaw, 0.15f), 0);
        }
    }

    private void AnimateWaver()
    {
        if (_waver is null)
        {
            return;
        }

        var t = (float)(_time % 9.0);
        var target = t is > 4f and < 7f ? 1f : 0f;
        _waver.Wave.Weight = Mathf.MoveToward(_waver.Wave.Weight, target, (float)GetProcessDeltaTime() * 2.5f);
    }

    private void BuildCamera()
    {
        var data = _look!.Camera;
        var pitch = Mathf.DegToRad((float)data.PitchDeg);
        var yaw = Mathf.DegToRad((float)data.StartYawDeg);
        var distance = (float)data.DistanceM.Start;
        var target = new Vector3(-2.5f, 0, -0.5f);
        var offset = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw)) * Mathf.Cos(pitch) * distance;
        offset.Y = Mathf.Sin(pitch) * distance;
        _camera = new Camera3D
        {
            Name = "Camera",
            Fov = (float)data.FovDeg,
            Projection = data.Projection == LookCameraProjection.Orthographic ? Camera3D.ProjectionType.Orthogonal : Camera3D.ProjectionType.Perspective,
            Size = distance * 0.45f,
            Current = true,
        };
        AddChild(_camera);
        _camera.GlobalPosition = target + offset;
        _camera.LookAt(target, Vector3.Up);
    }

    /// <summary>Window run: overview after the scene settled, then a close-up of the group, then quit.</summary>
    private void TakeShots()
    {
        var size = GetViewport().GetVisibleRect().Size;
        var tag = string.Create(CultureInfo.InvariantCulture, $"{_variant}_{(int)size.X}x{(int)size.Y}");
        switch (_shotStep)
        {
            case 0 when _time > 4.0 && _frame > 200:
                Save($"overview_{tag}.png");
                _camera!.GlobalPosition = new Vector3(8.5f, 6.2f, 11.5f);
                _camera.LookAt(new Vector3(2.4f, 0.9f, 3f), Vector3.Up);
                _shotStep = 1;
                break;
            case 1 when _time > 5.5:
                Save($"closeup_{tag}.png");
                _camera!.GlobalPosition = new Vector3(-1.5f, 3.2f, 9.5f);
                _camera.LookAt(new Vector3(2.2f, 1.0f, 3.2f), Vector3.Up);
                _shotStep = 2;
                break;
            case 2 when _time > 7.0:
                Save($"figures_{tag}.png");
                GD.Print($"LOOK_SHOTS_OK {tag}");
                GetTree().Quit();
                _shotStep = 3;
                break;
        }
    }

    private void Save(string file)
    {
        System.IO.Directory.CreateDirectory(_shotDirectory!);
        var path = System.IO.Path.Combine(_shotDirectory!, file);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"LOOK_SHOT {path} {error}");
    }
}
