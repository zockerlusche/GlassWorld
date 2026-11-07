using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Cute proportions (D355): scales the head bone after the animation has posed the skeleton, so
/// every clip keeps the larger head without editing the clips.
/// </summary>
public partial class CuteProportions : SkeletonModifier3D
{
    /// <summary>Head scale relative to the model.</summary>
    public float HeadScale { get; set; } = 1.35f;

    /// <summary>Thickness of arms and legs relative to the model (bone length stays, so feet stay on the ground).</summary>
    public float LimbThickness { get; set; } = 0.8f;

    /// <summary>Width of the upper body relative to the model.</summary>
    public float TorsoWidth { get; set; } = 0.88f;

    private static readonly string[] Limbs = ["DEF-upper_arm.L", "DEF-upper_arm.R", "DEF-thigh.L", "DEF-thigh.R"];
    private static readonly string[] Torso = ["DEF-spine.002", "DEF-spine.003"];

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        // Children inherit the scale: thinning the upper limb thins the whole arm or leg, and the
        // head is scaled back against the torso width so it keeps its own size.
        foreach (var name in Limbs)
        {
            SetScale(skeleton, name, new Vector3(LimbThickness, 1, LimbThickness));
        }

        foreach (var name in Torso)
        {
            SetScale(skeleton, name, new Vector3(TorsoWidth, 1, 1));
        }

        var inherited = TorsoWidth * TorsoWidth;
        SetScale(skeleton, "DEF-head", new Vector3(HeadScale / inherited, HeadScale, HeadScale));
    }

    private static void SetScale(Skeleton3D skeleton, string bone, Vector3 scale)
    {
        var index = skeleton.FindBone(bone);
        if (index >= 0)
        {
            skeleton.SetBonePoseScale(index, scale);
        }
    }
}

/// <summary>
/// Procedural wave (no CC0 clip in the standard libraries): lifts the right upper arm up and out and
/// swings the forearm, blended in and out with <see cref="Weight"/> on top of whatever clip runs.
/// Directions are computed in skeleton space, so they do not depend on the bones' local axes.
/// </summary>
public partial class WaveModifier : SkeletonModifier3D
{
    private double _time;

    /// <summary>0 = off, 1 = full wave; the prototype eases it.</summary>
    public float Weight { get; set; }

    /// <summary>Swings per second.</summary>
    public float Frequency { get; set; } = 1.6f;

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        _time += delta;
        var skeleton = GetSkeleton();
        if (skeleton is null || Weight <= 0.001f)
        {
            return;
        }

        var upper = skeleton.FindBone("DEF-upper_arm.R");
        var fore = skeleton.FindBone("DEF-forearm.R");
        var hand = skeleton.FindBone("DEF-hand.R");
        if (upper < 0 || fore < 0 || hand < 0)
        {
            return;
        }

        var outward = Mathf.Sign(skeleton.GetBoneGlobalPose(upper).Origin.X);
        outward = outward == 0 ? -1 : outward;

        // Upper arm: up and a little out to the side and forward.
        var upperTarget = new Vector3(outward * 0.45f, 0.85f, 0.25f).Normalized();
        Aim(skeleton, upper, fore, upperTarget, Weight);

        // Forearm: mostly up, swinging left and right.
        var swing = Mathf.Sin((float)(_time * Frequency * Mathf.Tau)) * 0.45f;
        var foreTarget = new Vector3(outward * (0.15f + swing), 0.95f, 0.15f).Normalized();
        Aim(skeleton, fore, hand, foreTarget, Weight);
    }

    /// <summary>Rotates <paramref name="bone"/> so the direction to <paramref name="child"/> points along target.</summary>
    private static void Aim(Skeleton3D skeleton, int bone, int child, Vector3 target, float weight)
    {
        var pose = skeleton.GetBoneGlobalPose(bone);
        var direction = (skeleton.GetBoneGlobalPose(child).Origin - pose.Origin).Normalized();
        if (direction.LengthSquared() < 1e-6f)
        {
            return;
        }

        var full = new Quaternion(direction, target);
        var turn = Quaternion.Identity.Slerp(full, Mathf.Clamp(weight, 0, 1));
        var rotated = new Transform3D(new Basis(turn) * pose.Basis, pose.Origin);
        skeleton.SetBoneGlobalPose(bone, rotated);
    }
}
