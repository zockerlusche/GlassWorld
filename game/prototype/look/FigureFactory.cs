using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// What one figure looks like: body model (tools/figures/bodies.json), hair mesh (null = bald), the
/// clothing part meshes (F4), colours in sRGB, and the paint of the body regions clothing may cover.
/// </summary>
public sealed record FigureLook(
    string Body,
    string? Hair,
    Color Skin,
    Color Top,
    Color Bottom,
    Color Shoes,
    Color HairColor,
    IReadOnlyList<string> Clothing,
    float Scale,
    float ArmSpread = 0.45f,
    IReadOnlyDictionary<string, float>? Shapes = null,
    float HeadScale = 1f,
    bool Elder = false,
    RegionPaint Forearm = RegionPaint.Skin,
    RegionPaint Thigh = RegionPaint.Bottom,
    RegionPaint LowerThigh = RegionPaint.Bottom,
    RegionPaint Shin = RegionPaint.Bottom)
{
    /// <summary>The look of a resolved figure spec (core appearance record and look data).</summary>
    public static FigureLook From(FigureSpec spec, float armSpread = 0.45f)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new FigureLook(
            spec.Model,
            spec.HairMesh,
            Color.FromHtml(spec.Skin),
            Color.FromHtml(spec.Top),
            Color.FromHtml(spec.Bottom),
            Color.FromHtml(spec.Shoes),
            Color.FromHtml(spec.HairColor),
            spec.ClothingMeshes,
            (float)spec.Scale,
            armSpread,
            spec.Shapes.ToDictionary(s => s.Key, s => (float)s.Value),
            (float)spec.HeadScale,
            spec.Elder,
            spec.Forearm,
            spec.Thigh,
            spec.LowerThigh,
            spec.Shin);
    }
}

/// <summary>A built figure: its root, animation tree, modifiers, the body mesh and the visible hair and clothing meshes.</summary>
public sealed record Figure(Node3D Root, AnimationTree Tree, WaveModifier Wave, LookAtModifier Look, MeshInstance3D Body, FaceDriver Face, IReadOnlyList<MeshInstance3D> Worn);

/// <summary>
/// Builds a figure from the GlassWorld bodies (A1436, F5257=A): round D355-style bodies modelled in
/// Blender on the Quaternius CC0 rig, so every Quaternius clip runs on them. Body regions come as
/// vertex colours and are coloured by the warm comic shader; the hair style is one of the skinned
/// hair meshes in the model, the clothing parts (F4) are skinned meshes shown by the outfit. An AnimationTree blends idle → walk → jog by speed (cross-fades).
/// </summary>
public static class FigureFactory
{
    /// <summary>Folder of the built body models.</summary>
    public const string ModelFolder = "res://assets/characters/glassworld";

    private const string BlendParameter = "parameters/blend_position";

    /// <summary>Clips used by the prototype, all from the Quaternius standard library.</summary>
    public static readonly string[] Clips = ["Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop"];

    /// <summary>Clearance radius around the arm for the given limb thickness factor (1 = slim body).</summary>
    public static float ArmRadiusFor(float limbs) => (0.067f * limbs) + 0.025f;

    /// <summary>Builds one figure; <paramref name="comic"/> is the shared warm comic shader.</summary>
    public static Figure Build(FigureLook look, Shader comic, Shader? outline)
    {
        ArgumentNullException.ThrowIfNull(look);
        var root = GD.Load<PackedScene>($"{ModelFolder}/{look.Body}.glb").Instantiate<Node3D>();
        root.Scale = Vector3.One * look.Scale;
        var skeleton = root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
        var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();

        var material = new ShaderMaterial { Shader = comic };
        material.SetShaderParameter("use_regions", true);
        material.SetShaderParameter("region_skin", look.Skin);
        material.SetShaderParameter("region_top", look.Top);
        material.SetShaderParameter("region_bottom", look.Bottom);
        material.SetShaderParameter("region_shoes", look.Shoes);
        material.SetShaderParameter("region_hair", look.HairColor);
        material.SetShaderParameter("forearm_paint", (int)look.Forearm);
        material.SetShaderParameter("thigh_paint", (int)look.Thigh);
        material.SetShaderParameter("lower_thigh_paint", (int)look.LowerThigh);
        material.SetShaderParameter("shin_paint", (int)look.Shin);
        if (outline is not null)
        {
            var ink = new ShaderMaterial { Shader = outline };
            ink.SetShaderParameter("width", 0.012f);
            material.NextPass = ink;
        }

        MeshInstance3D? body = null;
        var worn = new List<MeshInstance3D>();
        var wanted = look.Clothing.Select(c => $"Cloth_{c}").ToHashSet(StringComparer.Ordinal);
        if (look.Hair is { } hair)
        {
            wanted.Add($"Hair_{hair}");
        }

        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            var name = mesh.Name.ToString();
            mesh.Visible = name == "Body" || wanted.Remove(name);
            mesh.MaterialOverride = material;
            body = name == "Body" ? mesh : body;
            if (mesh.Visible && name != "Body")
            {
                worn.Add(mesh);
            }
        }

        if (body is null)
        {
            throw new InvalidOperationException($"figure model {look.Body} has no Body mesh");
        }

        foreach (var missing in wanted)
        {
            GD.PushError($"FIGURE mesh missing in {look.Body}: {missing}");
        }

        // Body shapes and sex proportions mix as blend shapes (B2528, F4) on the body and on the
        // clothing (same shapes, so the body never shows through); weights come from the core record.
        foreach (var (shape, weight) in look.Shapes ?? new Dictionary<string, float>())
        {
            foreach (var mesh in worn.Prepend(body))
            {
                var index = mesh.FindBlendShapeByName($"body_{shape}");
                if (index >= 0)
                {
                    mesh.SetBlendShapeValue(index, Mathf.Clamp(weight, 0, 1));
                }
            }
        }

        // Order matters: proportions, arms rest, then the wave overrides the right arm, then the look-at.
        skeleton.AddChild(new ProportionModifier { HeadScale = look.HeadScale, Stoop = look.Elder ? 14f : 0f });
        skeleton.AddChild(new ArmRestModifier { Spread = look.ArmSpread });
        // Arm thickness grows with the heavy and strong shapes (blend shapes widen the limbs too).
        var limbs = 1f + (0.3f * (look.Shapes?.GetValueOrDefault("heavy") ?? 0)) + (0.3f * (look.Shapes?.GetValueOrDefault("strong") ?? 0));
        skeleton.AddChild(new ArmClearanceModifier { Profile = TorsoProfile.Of(body, skeleton), ArmRadius = ArmRadiusFor(limbs) });
        var wave = new WaveModifier();
        skeleton.AddChild(wave);
        var lookAt = new LookAtModifier();
        skeleton.AddChild(lookAt);

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
        var face = new FaceDriver { Body = body, Seed = (uint)look.GetHashCode() };
        root.AddChild(face);
        return new Figure(root, tree, wave, lookAt, body, face, worn);
    }

    /// <summary>Sets the blend between idle (0), walk (1) and jog (2).</summary>
    public static void SetMotion(Figure figure, float blend) => figure?.Tree.Set(BlendParameter, blend);
}
