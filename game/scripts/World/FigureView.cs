using GlassWorld.Contracts;
using GlassWorld.Game.Prototype.Look;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.View;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// One inhabitant on the island (B2 R27–R29): the figure built from the core's appearance record, standing
/// on the ground height, turned towards where it walks. Its AnimationTree mixes the gaits by speed without
/// steps (blend space over the library gaits) and cross-fades to the clip of the current action or, without
/// an action, to an idle behaviour chosen from core values (deterministic from id and time slot). Unknown or
/// not-yet-made clips play the fallback and are logged once. Presentation only.
/// </summary>
public partial class FigureView : Node3D
{
    private const string LocoBlend = "parameters/loco/blend_position";
    private const string ModeRequest = "parameters/mode/transition_request";

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    private AnimationChooser _chooser = null!;
    private AnimationTree _tree = null!;
    private AnimationNodeAnimation _act = null!;
    private HashSet<string> _gaitClips = [];
    private Func<double, double, float> _ground = (_, _) => 0f;
    private Vector3 _from;
    private Vector3 _to;
    private float _speedCmS;
    private string _mode = "loco";
    private string? _actAsset;
    private double _idleSlotS;
    private double _clock;

    /// <summary>Inhabitant id.</summary>
    public string InhabitantId { get; private set; } = string.Empty;

    /// <summary>Anchor above the head (thought bubble, question mark, B3/B6).</summary>
    public Node3D HeadAnchor { get; private set; } = null!;

    /// <summary>The clip playing now (library id), for checks and screenshots.</summary>
    public string CurrentClip { get; private set; } = string.Empty;

    /// <summary>Builds the figure of an inhabitant.</summary>
    public static FigureView Create(InhabitantSnapshot inhabitant, FigureSpec spec, AnimationLibrary library, LookFigures figures, Func<double, double, float> groundHeight)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(figures);
        var comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
        var outline = figures.Outline ? GD.Load<Shader>("res://shaders/outline_hull.gdshader") : null;
        var look = new FigureLook(
            spec.Model,
            spec.HairMesh,
            Srgb(spec.Skin),
            Srgb(spec.Top),
            Srgb(spec.Bottom),
            Srgb(spec.Shoes),
            Srgb(spec.HairColor),
            spec.ShortSleeves,
            spec.Shorts,
            (float)spec.Scale,
            Shapes: spec.Shapes.ToDictionary(s => s.Key, s => (float)s.Value),
            HeadScale: (float)spec.HeadScale,
            Elder: spec.Elder);
        var figure = FigureFactory.Build(look, comic, outline);

        var view = new FigureView
        {
            Name = inhabitant.Id,
            InhabitantId = inhabitant.Id,
            _chooser = new AnimationChooser(library),
            _ground = groundHeight,
            _idleSlotS = figures.IdleSlotS,
            _gaitClips = library.Gaits.Select(g => g.Clip).ToHashSet(StringComparer.Ordinal),
        };
        view.AddChild(figure.Root);
        view.HeadAnchor = new Node3D { Name = "HeadAnchor", Position = new Vector3(0, (float)(figures.HeadAnchorM * spec.Scale), 0) };
        view.AddChild(view.HeadAnchor);

        // Replace the prototype's idle/walk/jog tree by the library's gaits and an action slot.
        figure.Tree.Active = false;
        figure.Tree.QueueFree();
        var player = figure.Root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
        view.BuildTree(figure.Root, player, library, figures.CrossfadeS);

        var start = new Vector3((float)inhabitant.Position.X, groundHeight(inhabitant.Position.X, inhabitant.Position.Z), (float)inhabitant.Position.Z);
        view._from = view._to = start;
        view.Position = start;
        return view;
    }

    /// <summary>
    /// Takes the inhabitant's state after a tick: the new position (eased towards over the tick), the action
    /// and the core values for idle. <paramref name="tickSeconds"/> is the real time one tick lasts.
    /// </summary>
    public void Apply(InhabitantSnapshot inhabitant, double tickSeconds)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        _from = Position;
        _to = new Vector3((float)inhabitant.Position.X, _ground(inhabitant.Position.X, inhabitant.Position.Z), (float)inhabitant.Position.Z);
        var flat = new Vector2(_to.X - _from.X, _to.Z - _from.Z);
        _speedCmS = tickSeconds > 0 ? (float)(flat.Length() * 100 / tickSeconds) : 0f;
        if (flat.LengthSquared() > 1e-6f)
        {
            Rotation = new Vector3(0, Mathf.Atan2(flat.X, flat.Y), 0);
        }

        ClipChoice choice;
        if (inhabitant.Action is { } action)
        {
            choice = _chooser.ForAction(action);
            if (_gaitClips.Contains(choice.ClipId))
            {
                // Walking actions: the gait follows the real speed (stroll … run), no fixed clip.
                SetMode("loco", null);
                _tree.Set(LocoBlend, _speedCmS);
                CurrentClip = _chooser.ForSpeed(_speedCmS).ClipId;
                return;
            }
        }
        else
        {
            var values = inhabitant.Needs.Concat(inhabitant.Moods).Concat(inhabitant.Traits)
                .GroupBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            choice = _chooser.ForIdle(InhabitantId, (long)Math.Floor(_clock / _idleSlotS), values);
        }

        if (choice.FellBack && Reported.Add(choice.ClipId))
        {
            GD.Print($"ANIMATION_FALLBACK clip={choice.ClipId} plays {choice.Asset}");
        }

        SetMode("act", choice.Asset, choice.Speed);
        CurrentClip = choice.ClipId;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _clock += delta;

        // Ease towards the position of the latest tick (R17: interpolate between ticks).
        Position = Position.Lerp(_to, 1 - Mathf.Exp(-12f * (float)delta));
    }

    private void SetMode(string mode, string? asset, double speed = 1.0)
    {
        if (mode == "act" && asset is not null && asset != _actAsset)
        {
            _act.Animation = asset;
            _actAsset = asset;
            _tree.Set("parameters/act_speed/scale", speed);
            _tree.Set(ModeRequest, "loco");
            _tree.Set(ModeRequest, "act");
        }

        if (_mode != mode)
        {
            _tree.Set(ModeRequest, mode);
            _mode = mode;
        }
    }

    private void BuildTree(Node3D root, AnimationPlayer player, AnimationLibrary library, double crossfade)
    {
        string? Asset(string clipId) => library.Clips.FirstOrDefault(c => c.Id == clipId && c.Source == AnimationClipSource.Asset)?.Asset;

        var loco = new AnimationNodeBlendSpace1D { MinSpace = 0, MaxSpace = library.Gaits.Max(g => g.MinSpeedCmS) + 100 };
        foreach (var gait in library.Gaits.OrderBy(g => g.MinSpeedCmS))
        {
            var asset = Asset(gait.Clip) ?? Asset(library.FallbackClip)!;
            if (!player.HasAnimation(asset))
            {
                GD.PushError($"FIGURE gait clip missing in the model: {asset}");
                continue;
            }

            player.GetAnimation(asset).LoopMode = Animation.LoopModeEnum.Linear;
            loco.AddBlendPoint(new AnimationNodeAnimation { Animation = asset }, gait.MinSpeedCmS);
        }

        _act = new AnimationNodeAnimation { Animation = Asset(library.FallbackClip)! };
        var transition = new AnimationNodeTransition { XfadeTime = (float)crossfade };
        transition.AddInput("loco");
        transition.AddInput("act");

        var blend = new AnimationNodeBlendTree();
        blend.AddNode("loco", loco);
        blend.AddNode("act", _act);
        blend.AddNode("act_speed", new AnimationNodeTimeScale());
        blend.AddNode("mode", transition);
        blend.ConnectNode("act_speed", 0, "act");
        blend.ConnectNode("mode", 0, "loco");
        blend.ConnectNode("mode", 1, "act_speed");
        blend.ConnectNode("output", 0, "mode");

        _tree = new AnimationTree { Name = "Motion", TreeRoot = blend };
        root.AddChild(_tree);
        _tree.AnimPlayer = _tree.GetPathTo(player);
        _tree.Set("parameters/act_speed/scale", 1.0);
        _tree.Active = true;
    }

    private static Color Srgb(string hex) => Color.FromHtml(hex);
}
