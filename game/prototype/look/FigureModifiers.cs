using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Procedural wave (no CC0 clip in the standard libraries; TL #18976): the upper arm goes out to the
/// side at about shoulder height, the elbow is bent so the forearm stands upright and the open hand is
/// at head height, swinging from side to side – never a stretched arm. Blended in and out with
/// <see cref="Weight"/> on top of whatever clip runs; directions are in skeleton space.
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

        // Upper arm: out to the side, slightly raised and a little forward (elbow about shoulder height).
        var upperTarget = new Vector3(outward * 0.9f, 0.18f, 0.35f).Normalized();
        Aim(skeleton, upper, fore, upperTarget, Weight);

        // Forearm: upright from the bent elbow, the hand swinging from side to side at head height.
        var swing = Mathf.Sin((float)(_time * Frequency * Mathf.Tau)) * 0.32f;
        var foreTarget = new Vector3(swing - (outward * 0.05f), 1f, 0.12f).Normalized();
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

/// <summary>
/// Turns head and neck smoothly towards a target (the camera) on top of the running clip, limited to a
/// natural angle. Used while waving or being spoken to (A1436). Directions are computed in skeleton space;
/// the figure faces +Z in its rest pose.
/// </summary>
public partial class LookAtModifier : SkeletonModifier3D
{
    /// <summary>0 = off, 1 = full turn; the prototype eases it.</summary>
    public float Weight { get; set; }

    /// <summary>Target in world space.</summary>
    public Vector3 Target { get; set; }

    /// <summary>Largest turn of the head in degrees.</summary>
    public float MaxAngleDegrees { get; set; } = 65f;

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null || Weight <= 0.001f)
        {
            return;
        }

        // Upper body first, then neck and head (TL #18980); the feet turn with the figure's root.
        var target = skeleton.GlobalTransform.AffineInverse() * Target;
        Turn(skeleton, "DEF-spine.002", target, Weight * 0.25f);
        Turn(skeleton, "DEF-spine.003", target, Weight * 0.25f);
        Turn(skeleton, "DEF-neck", target, Weight * 0.35f);
        Turn(skeleton, "DEF-head", target, Weight);
    }

    private void Turn(Skeleton3D skeleton, string name, Vector3 target, float weight)
    {
        var bone = skeleton.FindBone(name);
        if (bone < 0)
        {
            return;
        }

        var pose = skeleton.GetBoneGlobalPose(bone);
        var rest = skeleton.GetBoneGlobalRest(bone);
        var forward = (pose.Basis * rest.Basis.Inverse() * Vector3.Back).Normalized();
        var wanted = (target - pose.Origin).Normalized();
        if (wanted.LengthSquared() < 1e-6f)
        {
            return;
        }

        var angle = forward.AngleTo(wanted);
        var limit = Mathf.DegToRad(MaxAngleDegrees);
        var share = angle > limit ? limit / angle : 1f;
        var turn = Quaternion.Identity.Slerp(new Quaternion(forward, wanted), Mathf.Clamp(weight, 0, 1) * share);
        skeleton.SetBoneGlobalPose(bone, new Transform3D(new Basis(turn) * pose.Basis, pose.Origin));
    }
}

/// <summary>
/// Arms rest loosely against the body (B2528): while an upper arm hangs down, its sideways spread is
/// reduced towards the torso. Raised arms (waving, carrying) are left alone. The share comes from the
/// body data; heavier bodies keep more spread so arms do not go into the torso.
/// </summary>
public partial class ArmRestModifier : SkeletonModifier3D
{
    /// <summary>Share of the sideways spread that remains (1 = clip as is, 0 = straight down).</summary>
    public float Spread { get; set; } = 0.45f;

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        foreach (var side in new[] { "L", "R" })
        {
            var upper = skeleton.FindBone($"DEF-upper_arm.{side}");
            var fore = skeleton.FindBone($"DEF-forearm.{side}");
            if (upper < 0 || fore < 0)
            {
                continue;
            }

            var pose = skeleton.GetBoneGlobalPose(upper);
            var direction = (skeleton.GetBoneGlobalPose(fore).Origin - pose.Origin).Normalized();
            var hanging = Mathf.Clamp((-direction.Y - 0.3f) / 0.4f, 0, 1);
            if (hanging <= 0)
            {
                continue;
            }

            var wanted = new Vector3(direction.X * Spread, direction.Y, direction.Z).Normalized();
            var turn = Quaternion.Identity.Slerp(new Quaternion(direction, wanted), hanging);
            skeleton.SetBoneGlobalPose(upper, new Transform3D(new Basis(turn) * pose.Basis, pose.Origin));
        }
    }
}

/// <summary>Head size per inhabitant and a slight forward stoop for elders (on top of every clip).</summary>
public partial class ProportionModifier : SkeletonModifier3D
{
    /// <summary>Head scale from the appearance data.</summary>
    public float HeadScale { get; set; } = 1f;

    /// <summary>Forward bend of the upper spine in degrees (elders).</summary>
    public float Stoop { get; set; }

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        var head = skeleton.FindBone("DEF-head");
        if (head >= 0 && !Mathf.IsEqualApprox(HeadScale, 1f))
        {
            skeleton.SetBonePoseScale(head, Vector3.One * HeadScale);
        }

        if (Stoop > 0)
        {
            foreach (var name in new[] { "DEF-spine.002", "DEF-spine.003" })
            {
                var bone = skeleton.FindBone(name);
                if (bone < 0)
                {
                    continue;
                }

                var pose = skeleton.GetBoneGlobalPose(bone);
                var bend = new Quaternion(Vector3.Right, Mathf.DegToRad(Stoop * 0.5f));
                skeleton.SetBoneGlobalPose(bone, new Transform3D(new Basis(bend) * pose.Basis, pose.Origin));
            }
        }
    }
}

/// <summary>
/// The face (B2528): mouth expression as blend shapes (neutral, smile, sad, surprised, talk) with soft
/// changes, talking opens and closes the mouth, and the eyes blink at irregular but deterministic
/// intervals (from the figure seed, presentation only).
/// </summary>
public partial class FaceDriver : Node
{
    private static readonly string[] Mouths = ["smile", "sad", "surprised", "talk"];
    private double _time;
    private double _nextBlink = 2.0;
    private uint _state;

    /// <summary>The body mesh with the blend shapes.</summary>
    public MeshInstance3D? Body { get; set; }

    /// <summary>Seed for the blink rhythm.</summary>
    public uint Seed { get; set; }

    /// <summary>Current expression: neutral, smile, sad, surprised or talk.</summary>
    public string Expression { get; set; } = "neutral";

    /// <summary>Whether the eyes blink.</summary>
    public bool Blinking { get; set; } = true;

    /// <inheritdoc/>
    public override void _Ready() => _state = Seed | 1;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (Body is null)
        {
            return;
        }

        _time += delta;
        var talkOpen = Expression == "talk" ? (Mathf.Sin((float)_time * 9f) > 0 ? 1f : 0.15f) : 0f;
        foreach (var mouth in Mouths)
        {
            var index = Body.FindBlendShapeByName($"mouth_{mouth}");
            if (index < 0)
            {
                continue;
            }

            var target = mouth == "talk" ? talkOpen : (Expression == mouth ? 1f : 0f);
            var current = Body.GetBlendShapeValue(index);
            Body.SetBlendShapeValue(index, Mathf.MoveToward(current, target, (float)delta * (mouth == "talk" ? 14f : 4f)));
        }

        var blink = Body.FindBlendShapeByName("blink");
        if (blink >= 0)
        {
            var closed = 0f;
            if (Blinking && _time >= _nextBlink)
            {
                var phase = (_time - _nextBlink) / 0.16;
                closed = phase < 1 ? Mathf.Sin((float)(phase * Mathf.Pi)) : 0f;
                if (phase >= 1)
                {
                    _nextBlink = _time + 2.5 + (Next() % 3000 / 1000.0);
                }
            }

            Body.SetBlendShapeValue(blink, closed);
        }
    }

    private uint Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }
}
