using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Headless check for B2528 (A1436 F2): with the strongest body shape (<c>body_heavy</c> = 1) and the arm
/// rest modifier as used for heavy bodies, every clip is sampled at three times; the skinned mesh is baked
/// and every forearm vertex is tested against the torso surface (radius per height slab and angle sector,
/// from the torso's own vertices). Reports the deepest penetration; fails above the tolerance.
/// Run: <c>godot --headless --path game res://prototype/look/penetration_check.tscn</c> → <c>PENETRATION_OK …</c>.
/// </summary>
public partial class PenetrationCheck : Node3D
{
    private const float ToleranceM = 0.03f;
    private const int Sectors = 16;
    private const float SlabM = 0.05f;
    private static readonly string[] Bodies = ["adult_female", "adult_male", "child"];
    private static readonly float[] Times = [0.25f, 0.5f, 0.75f];

    private readonly List<(string Body, AnimationPlayer Player, MeshInstance3D Mesh, string[] Clips)> _subjects = [];
    private readonly Dictionary<MeshInstance3D, Skinning> _skinning = [];
    private readonly Dictionary<MeshInstance3D, float> _measured = [];
    private readonly List<string> _worst = [];
    private int _clip;
    private int _time;
    private int _frame;
    private float _deepest;
    private string _deepestAt = "-";
    private int _samples;

    /// <inheritdoc/>
    public override void _Ready()
    {
        foreach (var body in Bodies)
        {
            var root = GD.Load<PackedScene>($"{FigureFactory.ModelFolder}/{body}.glb").Instantiate<Node3D>();
            AddChild(root);
            var skeleton = root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
            var mesh = root.FindChildren("Body", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Single();
            mesh.SetBlendShapeValue(mesh.FindBlendShapeByName("body_heavy"), 1f);
            skeleton.AddChild(new ArmRestModifier { Spread = 0.8f });
            skeleton.AddChild(new ArmClearanceModifier { Profile = TorsoProfile.Of(mesh, skeleton), ArmRadius = (0.06f * 1.3f) + 0.02f });
            var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
            _skinning[mesh] = Skinning.Of(mesh, skeleton, "body_heavy");

            // Measure the final pose (after the modifiers), which Godot reports with this signal.
            var measured = mesh;
            skeleton.SkeletonUpdated += () =>
            {
                if (_frame >= 2)
                {
                    _measured[measured] = Measure(_skinning[measured].Pose());
                }
            };
            var clips = player.GetAnimationList().Where(c => c != "A_TPose").Order(StringComparer.Ordinal).ToArray();
            _subjects.Add((body, player, mesh, clips));
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        var clips = _subjects[0].Clips;
        if (_clip >= clips.Length)
        {
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PENETRATION_OK samples={_samples} deepest_cm={_deepest * 100:F1} at={_deepestAt} tolerance_cm={ToleranceM * 100:F1} over={_worst.Count}");
            foreach (var group in _worst.GroupBy(w => w.Split('/')[1].Split('@')[0]).OrderByDescending(g => g.Count()))
            {
                GD.Print($"PENETRATION_CLIP {group.Key} over={group.Count()}");
            }

            if (_worst.Count > 0)
            {
                GD.PushError("PENETRATION_FAIL " + string.Join("; ", _worst.Take(12)));
            }

            GD.Print(line);
            GetTree().Quit();
            _clip = int.MaxValue - 1;
            return;
        }

        if (_frame == 0)
        {
            foreach (var (_, player, _, _) in _subjects)
            {
                if (player.HasAnimation(clips[_clip]))
                {
                    player.Play(clips[_clip]);
                    player.Seek(player.CurrentAnimationLength * Times[_time], true);
                }
            }
        }

        if (++_frame < 4)
        {
            return;
        }

        foreach (var (body, player, mesh, _) in _subjects)
        {
            if (!player.HasAnimation(clips[_clip]))
            {
                continue;
            }

            if (!_measured.TryGetValue(mesh, out var depth))
            {
                continue;
            }

            _samples++;
            if (depth > _deepest)
            {
                _deepest = depth;
                _deepestAt = $"{body}/{clips[_clip]}@{Times[_time]}";
            }

            if (depth > ToleranceM)
            {
                _worst.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{body}/{clips[_clip]}@{Times[_time]} {depth * 100:F1} cm"));
            }
        }

        _frame = 0;
        _measured.Clear();
        if (++_time >= Times.Length)
        {
            _time = 0;
            _clip++;
        }
    }

    /// <summary>Deepest forearm vertex inside the torso surface, in metres (0 if none).</summary>
    private static float Measure((Vector3[] Positions, string[] Dominant) posed)
    {
        var (positions, dominant) = posed;
        // Torso and arm vertices by their dominant bone (sleeves share the shirt colour, so colour
        // regions would count the upper arms as torso).
        var torso = new List<Vector3>();
        var arms = new List<Vector3>();
        for (var i = 0; i < positions.Length; i++)
        {
            var bone = dominant[i];
            if (bone.Contains("spine", StringComparison.Ordinal) || bone == "DEF-hips")
            {
                torso.Add(positions[i]);
            }
            else if (bone.Contains("forearm", StringComparison.Ordinal) || bone.Contains("hand", StringComparison.Ordinal))
            {
                arms.Add(positions[i]);
            }
        }

        if (torso.Count == 0 || arms.Count == 0)
        {
            return 0;
        }

        var minY = torso.Min(p => p.Y);
        var slabs = (int)Mathf.Ceil((torso.Max(p => p.Y) - minY) / SlabM) + 1;
        var centre = new Vector2[slabs];
        var counts = new int[slabs];
        foreach (var p in torso)
        {
            var s = (int)((p.Y - minY) / SlabM);
            centre[s] += new Vector2(p.X, p.Z);
            counts[s]++;
        }

        for (var s = 0; s < slabs; s++)
        {
            centre[s] = counts[s] > 0 ? centre[s] / counts[s] : Vector2.Zero;
        }

        var radius = new float[slabs, Sectors];
        foreach (var p in torso)
        {
            var s = (int)((p.Y - minY) / SlabM);
            var offset = new Vector2(p.X, p.Z) - centre[s];
            var sector = Sector(offset);
            radius[s, sector] = Mathf.Max(radius[s, sector], offset.Length());
        }

        var deepest = 0f;
        foreach (var p in arms)
        {
            var s = (int)((p.Y - minY) / SlabM);
            if (s < 0 || s >= slabs || counts[s] == 0)
            {
                continue;
            }

            var offset = new Vector2(p.X, p.Z) - centre[s];
            var inside = radius[s, Sector(offset)] - offset.Length();
            deepest = Mathf.Max(deepest, inside);
        }

        return deepest;
    }

    private static int Sector(Vector2 offset) =>
        (int)((Mathf.Atan2(offset.Y, offset.X) + Mathf.Pi) / Mathf.Tau * Sectors) % Sectors;

    /// <summary>
    /// CPU skinning of one surface (the headless renderer does not skin): blend shape applied at full
    /// weight, then every vertex moved by its bones (global pose × bind pose), in skeleton space.
    /// </summary>
    private sealed class Skinning
    {
        private Vector3[] _rest = [];
        private string[] _dominant = [];
        private int[] _bones = [];
        private float[] _weights = [];
        private int _per;
        private Transform3D[] _bind = [];
        private int[] _bindBone = [];
        private Skeleton3D? _skeleton;

        public static Skinning Of(MeshInstance3D instance, Skeleton3D skeleton, string blendShape)
        {
            var mesh = (ArrayMesh)instance.Mesh;
            var arrays = mesh.SurfaceGetArrays(0);
            var rest = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var shape = instance.FindBlendShapeByName(blendShape);
            if (shape >= 0)
            {
                var shaped = mesh.SurfaceGetBlendShapeArrays(0)[shape][(int)Mesh.ArrayType.Vertex].AsVector3Array();
                rest = mesh.BlendShapeMode == Mesh.BlendShapeMode.Relative
                    ? [.. rest.Select((v, i) => v + shaped[i])]
                    : shaped;
            }

            var skin = instance.Skin;
            var binds = skin.GetBindCount();
            var s = new Skinning
            {
                _rest = rest,

                _bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array(),
                _weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array(),
                _bind = [.. Enumerable.Range(0, binds).Select(skin.GetBindPose)],
                _bindBone = [.. Enumerable.Range(0, binds).Select(b =>
                {
                    var name = skin.GetBindName(b).ToString();
                    return string.IsNullOrEmpty(name) ? skin.GetBindBone(b) : skeleton.FindBone(name);
                })],
                _skeleton = skeleton,
            };
            s._per = s._bones.Length / Math.Max(1, rest.Length);
            s._dominant = new string[rest.Length];
            for (var v = 0; v < rest.Length; v++)
            {
                var best = 0;
                for (var k = 1; k < s._per; k++)
                {
                    if (s._weights[(v * s._per) + k] > s._weights[(v * s._per) + best])
                    {
                        best = k;
                    }
                }

                s._dominant[v] = skeleton.GetBoneName(s._bindBone[s._bones[(v * s._per) + best]]);
            }

            return s;
        }

        public (Vector3[] Positions, string[] Dominant) Pose()
        {
            var skeleton = _skeleton!;
            var matrices = new Transform3D[_bind.Length];
            for (var b = 0; b < _bind.Length; b++)
            {
                matrices[b] = skeleton.GetBoneGlobalPose(_bindBone[b]) * _bind[b];
            }

            var posed = new Vector3[_rest.Length];
            for (var v = 0; v < _rest.Length; v++)
            {
                var sum = Vector3.Zero;
                for (var k = 0; k < _per; k++)
                {
                    var w = _weights[(v * _per) + k];
                    if (w > 0)
                    {
                        sum += matrices[_bones[(v * _per) + k]] * _rest[v] * w;
                    }
                }

                posed[v] = sum;
            }

            return (posed, _dominant);
        }
    }
}
