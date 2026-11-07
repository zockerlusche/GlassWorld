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
    private string[] _expected = [];
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

        // Reference: the clips of the Quaternius source library, not one of the rebuilt bodies.
        var source = GD.Load<PackedScene>("res://assets/characters/quaternius-ual1/ual1_standard.glb").Instantiate<Node3D>();
        var sourcePlayer = source.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
        _expected = sourcePlayer.GetAnimationList().Where(c => c != "A_TPose").Order(StringComparer.Ordinal).ToArray();
        source.Free();
        if (_expected.Length == 0)
        {
            _failures.Add("source library has no clips");
        }

        foreach (var (body, _, _, clips) in _subjects)
        {
            var missing = _expected.Except(clips, StringComparer.Ordinal).ToList();
            var extra = clips.Except(_expected, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                _failures.Add($"{body} misses clips: {string.Join(',', missing)}");
            }

            if (extra.Count > 0)
            {
                _failures.Add($"{body} has clips not in the source: {string.Join(',', extra)}");
            }
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        // Every body must carry exactly the clips of the source library (compared by name, not index).
        var expected = _expected;
        var count = expected.Length;
        if (_clip >= count)
        {
            var bodies = string.Join(',', _subjects.Select(s => s.Body));
            if (_checked != count * _subjects.Count)
            {
                _failures.Add($"checked {_checked} of {count * _subjects.Count} body clips");
            }

            if (_failures.Count > 0)
            {
                GD.PushError($"CLIPS_FAIL {string.Join("; ", _failures)}");
            }

            GD.Print($"CLIPS_OK bodies={bodies} reference=ual1_standard clips={count} checked={_checked} failures={_failures.Count}");
            GetTree().Quit();
            _clip = int.MaxValue - 1;
            return;
        }

        if (_frame == 0)
        {
            foreach (var (_, player, _, _) in _subjects)
            {
                if (!player.HasAnimation(expected[_clip]))
                {
                    continue;
                }

                player.Play(expected[_clip]);
                player.Seek(player.CurrentAnimationLength * 0.37, true);
            }
        }

        if (++_frame < 3)
        {
            return;
        }

        foreach (var (body, player, skeleton, _) in _subjects)
        {
            if (!player.HasAnimation(expected[_clip]))
            {
                continue;
            }

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
                _failures.Add($"{body}/{expected[_clip]} moved {moved} bones");
            }
        }

        _clip++;
        _frame = 0;
    }
}
