using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// What one figure looks like: body model (tools/figures/bodies.json), hair style and colours in sRGB
/// from content/look/appearance.json.
/// </summary>
public sealed record FigureLook(
    string Body,
    string Hair,
    Color Skin,
    Color Top,
    Color Bottom,
    Color Shoes,
    Color HairColor,
    bool ShortSleeves,
    bool Shorts,
    float Scale,
    float ArmSpread = 0.45f);

/// <summary>A built figure: its root, animation tree and the wave and look-at modifiers.</summary>
public sealed record Figure(Node3D Root, AnimationTree Tree, WaveModifier Wave, LookAtModifier Look);

/// <summary>
/// Builds a figure from the GlassWorld bodies (A1436, F5257=A): round D355-style bodies modelled in
/// Blender on the Quaternius CC0 rig, so every Quaternius clip runs on them. Body regions come as
/// vertex colours and are coloured by the warm comic shader; the hair style is one of the skinned
/// hair meshes in the model. An AnimationTree blends idle → walk → jog by speed (cross-fades).
/// </summary>
public static class FigureFactory
{
    /// <summary>Folder of the built body models.</summary>
    public const string ModelFolder = "res://assets/characters/glassworld";

    private const string BlendParameter = "parameters/blend_position";

    /// <summary>Clips used by the prototype, all from the Quaternius standard library.</summary>
    public static readonly string[] Clips = ["Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop"];

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
        material.SetShaderParameter("forearm_skin", look.ShortSleeves);
        material.SetShaderParameter("shin_skin", look.Shorts);
        if (outline is not null)
        {
            var ink = new ShaderMaterial { Shader = outline };
            ink.SetShaderParameter("width", 0.012f);
            material.NextPass = ink;
        }

        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            var name = mesh.Name.ToString();
            mesh.Visible = name == "Body" || name == $"Hair_{look.Hair}";
            mesh.MaterialOverride = material;
        }

        // Order matters: arms rest first, then the wave overrides the right arm, then the look-at.
        skeleton.AddChild(new ArmRestModifier { Spread = look.ArmSpread });
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
        return new Figure(root, tree, wave, lookAt);
    }

    /// <summary>Sets the blend between idle (0), walk (1) and jog (2).</summary>
    public static void SetMotion(Figure figure, float blend) => figure?.Tree.Set(BlendParameter, blend);
}
