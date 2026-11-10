using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Sitting on the ground or on a low seat (W1688): the seated clips sit on a chair, so on the ground the
/// knees straighten towards the rest pose (straight legs) by <see cref="Share"/> and the legs lie forward
/// on the ground instead of reaching into it. Lying (W1729) the thighs open towards the rest pose by
/// <see cref="HipShare"/>, so the seated pose laid on its side is only slightly curled. Runs after the clip
/// and the other modifiers, and while <see cref="Watching"/> notes where the watched bones are then (the
/// final pose), so the lying body can be fitted to the ground.
/// </summary>
public partial class KneeStraightenModifier : SkeletonModifier3D
{
    private static readonly string[] Knees = ["DEF-shin.L", "DEF-foot.L", "DEF-shin.R", "DEF-foot.R"];
    private static readonly string[] Thighs = ["DEF-thigh.L", "DEF-thigh.R"];
    private string[] _watched = [];
    private int[]? _bones;
    private Vector3[] _points = [];
    private bool _watching;
    private bool _noted;

    /// <summary>0 = the clip as is, 1 = shins and feet in their rest pose (straight legs).</summary>
    public float Share { get; set; }

    /// <summary>0 = the clip as is, 1 = thighs in their rest pose (legs in line with the body).</summary>
    public float HipShare { get; set; }

    /// <summary>Whether the watched bones are noted each frame; off, the notes are forgotten.</summary>
    public bool Watching
    {
        get => _watching;
        set
        {
            _watching = value;
            _noted &= value;
        }
    }

    /// <summary>Whether the watched bones have been noted since <see cref="Watching"/> began.</summary>
    public bool Noted => _noted;

    /// <summary>The bones to watch, by name; a bone the rig does not have is never noted.</summary>
    public void Watch(IEnumerable<string> bones)
    {
        _watched = [.. bones];
        _bones = null;
        _points = new Vector3[_watched.Length];
        _noted = false;
    }

    /// <summary>Where the watched bone <paramref name="index"/> was in the final pose, in the skeleton's space.</summary>
    public bool TryPoint(int index, out Vector3 point)
    {
        point = _noted && _bones is not null && _bones[index] >= 0 ? _points[index] : Vector3.Zero;
        return _noted && _bones is not null && _bones[index] >= 0;
    }

    /// <inheritdoc/>
    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        Straighten(skeleton, Knees, Share);
        Straighten(skeleton, Thighs, HipShare);
        if (!_watching)
        {
            return;
        }

        _bones ??= [.. _watched.Select(skeleton.FindBone)];
        for (var i = 0; i < _bones.Length; i++)
        {
            if (_bones[i] >= 0)
            {
                _points[i] = skeleton.GetBoneGlobalPose(_bones[i]).Origin;
            }
        }

        _noted = true;
    }

    private static void Straighten(Skeleton3D skeleton, string[] bones, float share)
    {
        if (share <= 0.001f)
        {
            return;
        }

        foreach (var name in bones)
        {
            var bone = skeleton.FindBone(name);
            if (bone < 0)
            {
                continue;
            }

            var rest = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion();
            skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Slerp(rest, Mathf.Clamp(share, 0, 1)));
        }
    }
}
