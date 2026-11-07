using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Torso radius per height of one figure with its body shapes applied (B2528): taken from the rest-pose
/// vertices whose dominant bone is the hips or the spine. Heights are relative to the hips bone.
/// </summary>
public sealed class TorsoProfile
{
    private const float SlabM = 0.04f;
    private readonly float[] _radius;
    private readonly float _from;

    private TorsoProfile(float from, float[] radius)
    {
        _from = from;
        _radius = radius;
    }

    /// <summary>Largest torso radius around the spine at a height above the hips (0 outside the torso).</summary>
    public float RadiusAt(float heightAboveHips)
    {
        var slab = (int)((heightAboveHips - _from) / SlabM);
        return slab < 0 || slab >= _radius.Length ? 0 : _radius[slab];
    }

    /// <summary>Measures the profile of a figure's body mesh with its current blend shape weights.</summary>
    public static TorsoProfile Of(MeshInstance3D body, Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(skeleton);
        var mesh = (ArrayMesh)body.Mesh;
        var arrays = mesh.SurfaceGetArrays(0);
        var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var shapes = mesh.SurfaceGetBlendShapeArrays(0);
        for (var s = 0; s < shapes.Count; s++)
        {
            var weight = body.GetBlendShapeValue(s);
            if (weight <= 0)
            {
                continue;
            }

            var shaped = shapes[s][(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (var v = 0; v < positions.Length; v++)
            {
                positions[v] += mesh.BlendShapeMode == Mesh.BlendShapeMode.Relative ? shaped[v] * weight : (shaped[v] - positions[v]) * weight;
            }
        }

        var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
        var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
        var per = bones.Length / Math.Max(1, positions.Length);
        var skin = body.Skin;
        string BoneOf(int bind)
        {
            var name = skin.GetBindName(bind).ToString();
            return string.IsNullOrEmpty(name) ? skeleton.GetBoneName(skin.GetBindBone(bind)) : name;
        }

        var hips = skeleton.GetBoneGlobalRest(skeleton.FindBone("DEF-hips")).Origin;
        var torso = new List<Vector3>();
        for (var v = 0; v < positions.Length; v++)
        {
            var best = 0;
            for (var k = 1; k < per; k++)
            {
                if (weights[(v * per) + k] > weights[(v * per) + best])
                {
                    best = k;
                }
            }

            var bone = BoneOf(bones[(v * per) + best]);
            if (bone == "DEF-hips" || bone.Contains("spine", StringComparison.Ordinal))
            {
                torso.Add(positions[v] - hips);
            }
        }

        var from = torso.Min(p => p.Y);
        var radius = new float[(int)Mathf.Ceil((torso.Max(p => p.Y) - from) / SlabM) + 1];
        foreach (var p in torso)
        {
            var slab = (int)((p.Y - from) / SlabM);
            radius[slab] = Mathf.Max(radius[slab], new Vector2(p.X, p.Z).Length());
        }

        return new TorsoProfile(from, radius);
    }
}

/// <summary>
/// Keeps the arms outside the torso for every body shape (B2528): after the clip (and the arm rest), the
/// elbow, wrist and hand tip are checked against the torso profile plus the arm thickness; where a point
/// would be inside, the upper arm turns outwards just far enough. Arms therefore rest on the body surface
/// of thin and heavy figures alike, with the clips unchanged.
/// </summary>
public partial class ArmClearanceModifier : SkeletonModifier3D
{
    /// <summary>The figure's torso profile.</summary>
    public TorsoProfile? Profile { get; set; }

    /// <summary>Arm thickness plus a small gap, in metres.</summary>
    public float ArmRadius { get; set; } = 0.06f;

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null || Profile is null)
        {
            return;
        }

        var hipsBone = skeleton.FindBone("DEF-hips");
        var chestBone = skeleton.FindBone("DEF-spine.003");
        if (hipsBone < 0 || chestBone < 0)
        {
            return;
        }

        var hipsRest = skeleton.GetBoneGlobalRest(hipsBone).Origin;
        foreach (var side in new[] { "L", "R" })
        {
            var upper = skeleton.FindBone($"DEF-upper_arm.{side}");
            var fore = skeleton.FindBone($"DEF-forearm.{side}");
            var hand = skeleton.FindBone($"DEF-hand.{side}");
            if (upper < 0 || fore < 0 || hand < 0)
            {
                continue;
            }

            // Up to three small corrections, as turning the arm moves all three points.
            for (var round = 0; round < 3; round++)
            {
                var hips = skeleton.GetBoneGlobalPose(hipsBone).Origin;
                var chest = skeleton.GetBoneGlobalPose(chestBone).Origin;
                var shoulder = skeleton.GetBoneGlobalPose(upper);
                var handPose = skeleton.GetBoneGlobalPose(hand);
                Vector3[] points = [skeleton.GetBoneGlobalPose(fore).Origin, handPose.Origin, handPose.Origin + (handPose.Basis.Y.Normalized() * 0.08f)];
                var push = 0f;
                var outward = Vector3.Zero;
                foreach (var p in points)
                {
                    var t = Mathf.Clamp((p.Y - hips.Y) / Mathf.Max(0.01f, chest.Y - hips.Y), 0, 1);
                    var axis = hips.Lerp(chest, t);
                    var radial = new Vector2(p.X - axis.X, p.Z - axis.Z);
                    var need = Profile.RadiusAt(p.Y - hips.Y + (hips.Y - hipsRest.Y) * 0) + ArmRadius - radial.Length();
                    var reach = Mathf.Max(0.1f, (p - shoulder.Origin).Length());
                    if (need > 0 && need / reach > push)
                    {
                        push = need / reach;
                        outward = radial.LengthSquared() > 1e-6f ? new Vector3(radial.X, 0, radial.Y).Normalized() : new Vector3(Mathf.Sign(shoulder.Origin.X - axis.X), 0, 0);
                    }
                }

                if (push <= 0.001f)
                {
                    break;
                }

                // Turn the upper arm about the axis that moves its lower end outwards.
                var down = ((points[0] - shoulder.Origin) with { Y = (points[0] - shoulder.Origin).Y }).Normalized();
                var turnAxis = down.Cross(outward).Normalized();
                if (turnAxis.LengthSquared() < 1e-6f)
                {
                    break;
                }

                var turn = new Quaternion(turnAxis, Mathf.Min(push * 1.1f, 0.6f));
                skeleton.SetBoneGlobalPose(upper, new Transform3D(new Basis(turn) * shoulder.Basis, shoulder.Origin));
            }
        }
    }
}
