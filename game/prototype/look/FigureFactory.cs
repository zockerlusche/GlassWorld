using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>What one prototype figure looks like (colours in sRGB from content/look/appearance.json).</summary>
public sealed record FigureLook(
    Color Skin,
    Color Top,
    Color Bottom,
    Color Shoes,
    Color Hair,
    bool LongHair,
    bool ShortSleeves,
    bool Shorts,
    bool Dress,
    float Scale,
    float HeadScale);

/// <summary>A built figure: its root, animation tree and wave modifier.</summary>
public sealed record Figure(Node3D Root, AnimationTree Tree, WaveModifier Wave);

/// <summary>
/// Builds a figure from the Quaternius CC0 mannequin (Universal Animation Library, standard): colours
/// body regions by the dominant bone of each vertex (clothing without extra meshes), adds simple
/// low-poly hair and eyes on the head bone, cute proportions and an AnimationTree that blends
/// idle → walk → jog by speed (cross-fading, no stiff switches).
/// </summary>
public static class FigureFactory
{
    /// <summary>Path of the imported mannequin with its clips.</summary>
    public const string ModelPath = "res://assets/characters/quaternius-ual1/ual1_standard.glb";

    private const string BlendParameter = "parameters/blend_position";

    /// <summary>Clips used by the prototype, all from the standard library.</summary>
    public static readonly string[] Clips = ["Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop"];

    /// <summary>Region index per bone name part (see warm_comic.gdshader region_color).</summary>
    private static int Region(string bone) => bone switch
    {
        _ when bone.Contains("head", StringComparison.Ordinal) || bone.Contains("neck", StringComparison.Ordinal) => 1,
        _ when bone.Contains("hand", StringComparison.Ordinal) || bone.Contains("f_", StringComparison.Ordinal) || bone.Contains("thumb", StringComparison.Ordinal) => 1,
        _ when bone.Contains("forearm", StringComparison.Ordinal) => 7,
        _ when bone.Contains("spine", StringComparison.Ordinal) || bone.Contains("shoulder", StringComparison.Ordinal) || bone.Contains("upper_arm", StringComparison.Ordinal) => 2,
        _ when bone.Contains("thigh", StringComparison.Ordinal) => 8,
        _ when bone.Contains("shin", StringComparison.Ordinal) => 6,
        _ when bone.Contains("foot", StringComparison.Ordinal) || bone.Contains("toe", StringComparison.Ordinal) => 4,
        _ => 3,
    };

    /// <summary>Builds one figure; <paramref name="comic"/> is the shared warm comic shader.</summary>
    public static Figure Build(FigureLook look, Shader comic, Shader? outline)
    {
        ArgumentNullException.ThrowIfNull(look);
        var root = GD.Load<PackedScene>(ModelPath).Instantiate<Node3D>();
        root.Scale = Vector3.One * look.Scale;
        var skeleton = root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
        var mesh = root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Single();
        var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();

        var material = new ShaderMaterial { Shader = comic };
        material.SetShaderParameter("use_regions", true);
        material.SetShaderParameter("region_skin", look.Skin);
        material.SetShaderParameter("region_top", look.Top);
        material.SetShaderParameter("region_bottom", look.Bottom);
        material.SetShaderParameter("region_shoes", look.Shoes);
        material.SetShaderParameter("region_hair", look.Hair);
        material.SetShaderParameter("forearm_skin", look.ShortSleeves);
        material.SetShaderParameter("shin_skin", look.Shorts || look.Dress);
        material.SetShaderParameter("thigh_top", look.Dress);
        if (outline is not null)
        {
            var ink = new ShaderMaterial { Shader = outline };
            ink.SetShaderParameter("width", 0.018f);
            material.NextPass = ink;
        }

        var headBounds = Recolor(mesh, skeleton, material);
        AddHeadDetails(skeleton, headBounds, look, material);

        skeleton.AddChild(new CuteProportions { HeadScale = look.HeadScale });
        var wave = new WaveModifier();
        skeleton.AddChild(wave);

        var space = new AnimationNodeBlendSpace1D { MinSpace = 0, MaxSpace = 2 };
        for (var i = 0; i < Clips.Length; i++)
        {
            // The Godot importer drops the "_Loop" suffix and marks such clips as looping.
            var name = player.HasAnimation(Clips[i]) ? Clips[i] : Clips[i].Replace("_Loop", string.Empty, StringComparison.Ordinal);
            if (!player.HasAnimation(name))
            {
                GD.PushError($"FIGURE clip missing: {Clips[i]}");
                continue;
            }

            player.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;
            space.AddBlendPoint(new AnimationNodeAnimation { Animation = name }, i, -1, name);
        }

        var tree = new AnimationTree { Name = "Motion", TreeRoot = space };
        root.AddChild(tree);
        tree.AnimPlayer = tree.GetPathTo(player);
        tree.Set(BlendParameter, 0.0f);
        tree.Active = true;
        return new Figure(root, tree, wave);
    }

    /// <summary>+1 if the model faces +Z in skeleton space (toes in front of the feet), else -1.</summary>
    public static float ForwardSignOf(Skeleton3D skeleton)
    {
        var foot = skeleton.FindBone("DEF-foot.L");
        var toe = skeleton.FindBone("DEF-toe.L");
        return foot < 0 || toe < 0 ? 1f : Mathf.Sign(skeleton.GetBoneGlobalRest(toe).Origin.Z - skeleton.GetBoneGlobalRest(foot).Origin.Z);
    }

    /// <summary>Sets the blend between idle (0), walk (1) and jog (2).</summary>
    public static void SetMotion(Figure figure, float blend) => figure?.Tree.Set(BlendParameter, blend);

    /// <summary>
    /// Writes the region index into the vertex colour of every surface and returns the bounds of the
    /// head vertices in skeleton space (rest pose), used to place hair and eyes.
    /// </summary>
    private static Aabb Recolor(MeshInstance3D instance, Skeleton3D skeleton, ShaderMaterial material)
    {
        var source = (ArrayMesh)instance.Mesh;
        var skin = instance.Skin;
        string BoneName(int bind)
        {
            if (skin is null)
            {
                return skeleton.GetBoneName(bind);
            }

            var name = skin.GetBindName(bind).ToString();
            return string.IsNullOrEmpty(name) ? skeleton.GetBoneName(skin.GetBindBone(bind)) : name;
        }

        var result = new ArrayMesh();
        Aabb? head = null;
        for (var s = 0; s < source.GetSurfaceCount(); s++)
        {
            var arrays = source.SurfaceGetArrays(s);
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var perVertex = bones.Length / Math.Max(1, positions.Length);
            var colors = new Color[positions.Length];
            for (var v = 0; v < positions.Length; v++)
            {
                var best = 0;
                for (var k = 1; k < perVertex; k++)
                {
                    if (weights[(v * perVertex) + k] > weights[(v * perVertex) + best])
                    {
                        best = k;
                    }
                }

                var region = Region(BoneName(bones[(v * perVertex) + best]));
                colors[v] = new Color(region / 10f, 0, 0);
                if (region == 1 && BoneName(bones[(v * perVertex) + best]).Contains("head", StringComparison.Ordinal))
                {
                    head = head is null ? new Aabb(positions[v], Vector3.Zero) : head.Value.Expand(positions[v]);
                }
            }

            arrays[(int)Mesh.ArrayType.Color] = colors;
            result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(s), arrays);
            result.SurfaceSetMaterial(s, material);
        }

        instance.Mesh = result;
        return head ?? new Aabb(new Vector3(-0.1f, 1.55f, -0.1f), new Vector3(0.2f, 0.27f, 0.2f));
    }

    /// <summary>Low-poly hair (cap, optional long tail) and two dot eyes, attached to the head bone.</summary>
    private static void AddHeadDetails(Skeleton3D skeleton, Aabb head, FigureLook look, ShaderMaterial bodyMaterial)
    {
        var bone = skeleton.FindBone("DEF-head");
        if (bone < 0)
        {
            return;
        }

        var attachment = new BoneAttachment3D { Name = "HeadDetails", BoneName = "DEF-head" };
        skeleton.AddChild(attachment);

        // Bind pose: mesh vertices are in skeleton space at rest; the attachment follows the bone pose.
        var rest = skeleton.GetBoneGlobalRest(bone);
        Vector3 Local(Vector3 skeletonSpace) => rest.AffineInverse() * skeletonSpace;

        var foot = skeleton.FindBone("DEF-foot.L");
        var toe = skeleton.FindBone("DEF-toe.L");
        var forwardSign = foot >= 0 && toe >= 0
            ? Mathf.Sign(skeleton.GetBoneGlobalRest(toe).Origin.Z - skeleton.GetBoneGlobalRest(foot).Origin.Z)
            : 1f;
        forwardSign = forwardSign == 0 ? 1 : forwardSign;
        var center = head.GetCenter();
        var size = head.Size;
        var hairMaterial = (ShaderMaterial)bodyMaterial.Duplicate();
        hairMaterial.SetShaderParameter("use_regions", false);
        hairMaterial.SetShaderParameter("albedo", look.Hair.SrgbToLinear());

        var cap = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = size.X * 0.56f, Height = size.Y * 0.8f, RadialSegments = 10, Rings = 5 },
            MaterialOverride = WhiteVertexColor(hairMaterial),
            Position = Local(center + new Vector3(0, size.Y * 0.22f, -forwardSign * size.Z * 0.08f)),
        };
        attachment.AddChild(cap);

        if (look.LongHair)
        {
            var tail = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = size.X * 0.42f, Height = size.Y * 1.5f, RadialSegments = 8, Rings = 3 },
                MaterialOverride = WhiteVertexColor(hairMaterial),
                Position = Local(center + new Vector3(0, -size.Y * 0.25f, -forwardSign * size.Z * 0.32f)),
            };
            attachment.AddChild(tail);
        }

        var eyeMaterial = (ShaderMaterial)hairMaterial.Duplicate();
        eyeMaterial.SetShaderParameter("albedo", new Color(0.12f, 0.09f, 0.08f).SrgbToLinear());
        foreach (var side in new[] { -1f, 1f })
        {
            var eye = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = size.X * 0.07f, Height = size.X * 0.14f, RadialSegments = 6, Rings = 3 },
                MaterialOverride = WhiteVertexColor(eyeMaterial),
                Position = Local(center + new Vector3(side * size.X * 0.2f, size.Y * 0.02f, forwardSign * ((size.Z * 0.5f) - 0.004f))),
            };
            attachment.AddChild(eye);
        }
    }

    /// <summary>Primitive meshes have no vertex colour; the shader multiplies by it, so use white.</summary>
    private static ShaderMaterial WhiteVertexColor(ShaderMaterial material)
    {
        material.SetShaderParameter("use_vertex_color", false);
        return material;
    }
}
