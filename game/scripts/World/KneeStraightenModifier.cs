using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Sitting on the ground or on a low seat (W1688): the seated clips sit on a chair, so on the ground the
/// knees straighten towards the rest pose (straight legs) by <see cref="Share"/> and the legs lie forward
/// on the ground instead of reaching into it. Runs after the clip and the other modifiers.
/// </summary>
public partial class KneeStraightenModifier : SkeletonModifier3D
{
    private static readonly string[] Bones = ["DEF-shin.L", "DEF-foot.L", "DEF-shin.R", "DEF-foot.R"];

    /// <summary>0 = the clip as is, 1 = shins and feet in their rest pose (straight legs).</summary>
    public float Share { get; set; }

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null || Share <= 0.001f)
        {
            return;
        }

        foreach (var name in Bones)
        {
            var bone = skeleton.FindBone(name);
            if (bone < 0)
            {
                continue;
            }

            var rest = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion();
            skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Slerp(rest, Mathf.Clamp(Share, 0, 1)));
        }
    }
}
