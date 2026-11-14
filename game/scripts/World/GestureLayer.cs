using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Arm gestures over the running clip (D958 D2): eating brings a hand to the mouth, drinking at the water scoops
/// with both hands. One two-bone IK per arm (upper arm, forearm, hand) moves the hand to a target before the mouth
/// or at the water; its influence follows <see cref="GestureCadence"/>, so the clip shows through between cycles.
/// Points come from the look data in the figure's frame (x towards the arm's own side, y up, z forward).
/// </summary>
public sealed class GestureLayer
{
    private readonly Skeleton3D _skeleton;
    private readonly Node3D _figure;
    private readonly float _scale;
    private readonly int _head;
    private readonly List<Arm> _arms = [];
    private FigureGesture? _gesture;
    private double _seconds;

    /// <summary>
    /// Adds the arm IK to the figure's skeleton; arms the rig does not have are left out. <paramref name="figure"/>
    /// stands on the ground (origin of the data's body points), <paramref name="scale"/> is the figure's size.
    /// </summary>
    public GestureLayer(Skeleton3D skeleton, Node3D figure, double scale)
    {
        _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        _figure = figure ?? throw new ArgumentNullException(nameof(figure));
        _scale = (float)scale;
        _head = skeleton.FindBone("DEF-head");
        foreach (var (side, suffix) in new[] { ("left", "L"), ("right", "R") })
        {
            var (root, middle, end) = ($"DEF-upper_arm.{suffix}", $"DEF-forearm.{suffix}", $"DEF-hand.{suffix}");
            var shoulder = skeleton.FindBone(root);
            if (shoulder < 0 || skeleton.FindBone(middle) < 0 || skeleton.FindBone(end) < 0)
            {
                continue;
            }

            var target = new Marker3D { Name = $"GestureTarget{suffix}" };
            var pole = new Marker3D { Name = $"GesturePole{suffix}" };
            skeleton.AddChild(target);
            skeleton.AddChild(pole);
            var ik = new TwoBoneIK3D { Name = $"Gesture{suffix}", Influence = 0, Active = false };
            ik.SetSettingCount(1);
            ik.SetRootBoneName(0, root);
            ik.SetMiddleBoneName(0, middle);
            ik.SetEndBoneName(0, end);
            skeleton.AddChild(ik);
            ik.SetTargetNode(0, new NodePath($"../{target.Name}"));
            ik.SetPoleNode(0, new NodePath($"../{pole.Name}"));

            // Which way is this arm's own side in the rig (the rest pose), so the data's x points outwards for both arms.
            var outward = Math.Sign(skeleton.GetBoneGlobalRest(shoulder).Origin.X);
            _arms.Add(new Arm(side, ik, target, pole, outward == 0 ? (side == "left" ? 1 : -1) : outward));
        }
    }

    /// <summary>The gesture shown now (null: none); each frame. A new gesture starts its cycle from the beginning.</summary>
    public void Update(double delta, FigureGesture? gesture)
    {
        if (!ReferenceEquals(gesture, _gesture))
        {
            _gesture = gesture;
            _seconds = 0;
        }
        else
        {
            _seconds += delta;
        }

        var state = gesture is null ? new GestureState(0, 1) : GestureCadence.At(gesture, _seconds);
        // The body's facing from the skeleton (without the rig's unit scale), metres from the data times the figure's size.
        var skeleton = _skeleton.GlobalTransform;
        var facing = skeleton.Basis.Orthonormalized();
        var ground = _figure.GlobalPosition;
        var head = _head >= 0 ? skeleton * _skeleton.GetBoneGlobalPose(_head).Origin : ground;
        foreach (var arm in _arms)
        {
            var moves = gesture is not null && state.Weight > 0.001 && gesture.Arms.Contains(arm.Side, StringComparer.Ordinal);
            arm.Ik.Active = moves;
            arm.Ik.Influence = moves ? (float)state.Weight : 0;
            if (!moves)
            {
                continue;
            }

            var atHead = head + (facing * Point(gesture!.ToHead, arm.Outward, _scale));
            var target = gesture.FromBody is { Count: 3 } from
                ? (ground + (facing * Point(from, arm.Outward, _scale))).Lerp(atHead, (float)state.ToHead)
                : atHead;
            arm.Target.GlobalPosition = target;
            arm.Pole.GlobalPosition = ground + (facing * Point(gesture.Pole, arm.Outward, _scale));
        }
    }

    private static Vector3 Point(ICollection<double> xyz, int outward, float scale)
    {
        var v = xyz.ToArray();
        return new Vector3((float)(v[0] * outward), (float)v[1], (float)v[2]) * scale;
    }

    private sealed record Arm(string Side, TwoBoneIK3D Ik, Marker3D Target, Marker3D Pole, int Outward);
}
