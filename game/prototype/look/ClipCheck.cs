using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Headless check for A1436: every Quaternius clip plays on every GlassWorld body and moves the
/// skeleton away from the rest pose (no missing tracks after the body was rebuilt in Blender).
/// Run: <c>godot --headless --path game res://prototype/look/clip_check.tscn</c> → <c>CLIPS_OK …</c>.
/// </summary>
public partial class ClipCheck : Node3D
{
    private static readonly string[] Bodies = ["adult_female", "adult_male", "child"];

    private readonly List<(string Body, AnimationPlayer Player, Skeleton3D Skeleton, string[] Clips)> _subjects = [];
    private readonly List<string> _failures = [];
    private int _clip;
    private int _frame;
    private int _checked;

    /// <inheritdoc/>
    public override void _Ready()
    {
        foreach (var body in Bodies)
        {
            var root = GD.Load<PackedScene>($"{FigureFactory.ModelFolder}/{body}.glb").Instantiate<Node3D>();
            AddChild(root);
            var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
            var skeleton = root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
            var clips = player.GetAnimationList().Where(c => c != "A_TPose").Order(StringComparer.Ordinal).ToArray();
            _subjects.Add((body, player, skeleton, clips));
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        var count = _subjects.Min(s => s.Clips.Length);
        if (_clip >= count)
        {
            var bodies = string.Join(',', _subjects.Select(s => s.Body));
            if (_failures.Count > 0)
            {
                GD.PushError($"CLIPS_FAIL {string.Join("; ", _failures)}");
            }

            GD.Print($"CLIPS_OK bodies={bodies} clips={count} checked={_checked} failures={_failures.Count}");
            GetTree().Quit();
            _clip = int.MaxValue - 1;
            return;
        }

        if (_frame == 0)
        {
            foreach (var (_, player, _, clips) in _subjects)
            {
                player.Play(clips[_clip]);
                player.Seek(player.CurrentAnimationLength * 0.37, true);
            }
        }

        if (++_frame < 3)
        {
            return;
        }

        foreach (var (body, _, skeleton, clips) in _subjects)
        {
            var moved = 0;
            for (var b = 0; b < skeleton.GetBoneCount(); b++)
            {
                var rest = skeleton.GetBoneRest(b);
                if (!skeleton.GetBonePoseRotation(b).IsEqualApprox(rest.Basis.GetRotationQuaternion())
                    || !skeleton.GetBonePosePosition(b).IsEqualApprox(rest.Origin))
                {
                    moved++;
                }
            }

            _checked++;
            if (moved < 5)
            {
                _failures.Add($"{body}/{clips[_clip]} moved {moved} bones");
            }
        }

        _clip++;
        _frame = 0;
    }
}
