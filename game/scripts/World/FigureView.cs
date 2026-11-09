using GlassWorld.Contracts;
using GlassWorld.Game.Prototype.Look;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.View;
using Godot;
using AnimationLibrary = GlassWorld.Contracts.AnimationLibrary;

namespace GlassWorld.Game.World;

/// <summary>
/// One inhabitant on the island (B2 R27–R29): the figure built from the core's appearance record, standing
/// on the ground height. <see cref="FigureMotion"/> moves it evenly from tick to tick and eases its facing;
/// while the core moves it, its AnimationTree plays the gait blend by game-time speed, played faster or
/// slower within the data range so the feet roughly match the screen; standing, it cross-fades between two
/// action slots to the clip of its action or to an idle behaviour from core values (deterministic from id
/// and core tick, each figure on its own idle slots, with a little speed variety and sometimes mirrored).
/// While the inhabitant has an open question, calm thought dots pulse above its head. Unknown or
/// not-yet-made clips are logged once. Presentation only.
/// </summary>
public partial class FigureView : Node3D
{
    private static readonly StringName LocoBlend = "parameters/loco/blend_position";
    private static readonly StringName LocoScale = "parameters/loco_speed/scale";
    private static readonly StringName ModeRequest = "parameters/mode/transition_request";
    private static readonly string[] ActSlots = ["act_a", "act_b"];
    private static readonly StringName[] ActScale = ["parameters/act_a_speed/scale", "parameters/act_b_speed/scale"];

    private FigureShared _shared = null!;
    private FigureMotion _motion = null!;
    private AnimationTree _tree = null!;
    private readonly AnimationNodeAnimation[] _act = new AnimationNodeAnimation[2];
    private readonly Label3D[] _dots = new Label3D[ThinkingDots.Count];
    private Node3D _body = null!;
    private Vector3 _bodyScale = Vector3.One;
    private bool _mirrored;
    private Node3D _thinking = null!;
    private double _thinkingPhase;
    private double _seconds;
    private Func<double, double, float> _ground = (_, _) => 0f;
    private double _scale = 1;
    private bool _walking = true;
    private int _slot = 1;
    private string? _actAsset;
    private double _actSpeed = 1;

    /// <summary>Inhabitant id.</summary>
    public string InhabitantId { get; private set; } = string.Empty;

    /// <summary>Anchor above the head (thought bubble, question mark, B3/B6).</summary>
    public Node3D HeadAnchor { get; private set; } = null!;

    /// <summary>The clip playing now (library id), for checks and screenshots.</summary>
    public string CurrentClip { get; private set; } = string.Empty;

    /// <summary>Builds the figure of an inhabitant.</summary>
    public static FigureView Create(InhabitantSnapshot inhabitant, FigureSpec spec, FigureShared shared, Func<double, double, float> groundHeight)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(shared);
        var figures = shared.Figures;
        var comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
        var outline = figures.Outline ? GD.Load<Shader>("res://shaders/outline_hull.gdshader") : null;
        var figure = FigureFactory.Build(FigureLook.From(spec), comic, outline);

        var start = new Point3(inhabitant.Position.X, groundHeight(inhabitant.Position.X, inhabitant.Position.Z), inhabitant.Position.Z);
        var view = new FigureView
        {
            Name = inhabitant.Id,
            InhabitantId = inhabitant.Id,
            _shared = shared,
            _ground = groundHeight,
            _scale = spec.Scale,
            _motion = new FigureMotion(start, figures.TurnRateDegS),
        };
        view.AddChild(figure.Root);
        view.HeadAnchor = new Node3D { Name = "HeadAnchor", Position = new Vector3(0, (float)(figures.HeadAnchorM * spec.Scale), 0) };
        view.AddChild(view.HeadAnchor);
        view._body = figure.Root;
        view._bodyScale = figure.Root.Scale;
        view.BuildThinkingDots(figures.ThinkingDots, spec.Scale);

        // Replace the prototype's idle/walk/jog tree by the library's gaits and two action slots.
        figure.Tree.Active = false;
        figure.Tree.QueueFree();
        var player = figure.Root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
        view.BuildTree(figure.Root, player, shared.Library, figures.CrossfadeS);
        view.Position = ToVector(start);
        return view;
    }

    /// <summary>
    /// Takes the inhabitant's state after <paramref name="ticks"/> core ticks (now at <paramref name="tick"/>):
    /// the figure walks evenly to the new position over the real time of those ticks, or with
    /// <paramref name="place"/> stands there at once (after a warm-up). <paramref name="all"/> (the snapshot's
    /// inhabitants) lets the clip follow the kind of target and turns two inhabitants of one action to each other.
    /// </summary>
    public void Apply(InhabitantSnapshot inhabitant, long tick, int ticks, bool place = false, IReadOnlyList<InhabitantSnapshot>? all = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var figures = _shared.Figures;
        var to = new Point3(inhabitant.Position.X, _ground(inhabitant.Position.X, inhabitant.Position.Z), inhabitant.Position.Z);
        if (place)
        {
            _motion.Place(to);
            Position = ToVector(to);
        }
        else
        {
            _motion.Retarget(to, Math.Max(1, ticks), _shared.GameSecondsPerTick, _shared.RealSecondsPerTick, figures.WalkMinStepM);
        }

        var chooser = _shared.Chooser;
        AnimationChooser.CoreValues(_shared.Values, inhabitant.Needs, inhabitant.Moods, inhabitant.Traits);
        var moving = !place && _motion.StepM >= figures.WalkMinStepM;
        var slot = AnimationChooser.IdleSlot(InhabitantId, tick, figures.IdleSlotTicks, figures.IdleSlotSpread);
        if (all is not null && InhabitantActivity.FacePoint(inhabitant, all) is { } face)
        {
            _motion.Face(face.X, face.Z, figures.WalkMinStepM);
        }

        var target = all is null ? null : InhabitantActivity.TargetKind(inhabitant, all);
        var clip = chooser.ForInhabitant(inhabitant.Action, moving, InhabitantId, slot, _shared.Values, target);
        _thinking.Visible = ThinkingDots.Shown(inhabitant.OpenQuestionIds.Count);
        if (clip.Missing is { } missing)
        {
            Report(missing);
        }

        if (clip.Walking)
        {
            // The core's gait (stroll … run, #112) plays as it is; without one the gait follows the game-time
            // speed. Either way the playback roughly follows the speed on screen.
            if (inhabitant.Gait is { } gaitId && chooser.ForGait(gaitId) is { } core)
            {
                Walk(core.Blend, FigureMotion.GaitTimeScale(_motion.ScreenSpeedCmS, core.GroundSpeedCmS, _scale, figures.GaitTimeScale.Min, figures.GaitTimeScale.Max));
                CurrentClip = core.Clip.ClipId;
                return;
            }

            var gait = chooser.ForSpeed(_motion.GameSpeedCmS);
            var scale = FigureMotion.GaitTimeScale(_motion.ScreenSpeedCmS, chooser.GroundSpeedCmS(_motion.GameSpeedCmS), _scale, figures.GaitTimeScale.Min, figures.GaitTimeScale.Max);
            Walk(_motion.GameSpeedCmS, scale);
            CurrentClip = gait.ClipId;
            return;
        }

        Report(clip.Clip);
        Act(clip);
        CurrentClip = clip.Clip.ClipId;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        // Even pace from tick to tick (R17); the height follows the ground under the figure.
        _motion.Advance(delta);
        var p = _motion.Position;
        Position = new Vector3((float)p.X, _ground(p.X, p.Z), (float)p.Z);
        Rotation = new Vector3(0, (float)_motion.Yaw, 0);
        if (_thinking.Visible)
        {
            _seconds += delta;
            var look = _shared.Figures.ThinkingDots;
            for (var i = 0; i < _dots.Length; i++)
            {
                var alpha = (float)ThinkingDots.Alpha(look, i, _seconds, _thinkingPhase);
                _dots[i].Modulate = new Color(1, 1, 1, alpha);
                _dots[i].OutlineModulate = new Color(0.12f, 0.12f, 0.16f, alpha * 0.8f);
            }
        }
    }

    private void Report(ClipChoice choice)
    {
        if (choice.FellBack && _shared.FirstClipReport(choice.ClipId))
        {
            GD.Print($"ANIMATION_FALLBACK clip={choice.ClipId} plays {choice.Asset}");
        }
    }

    private void Walk(double blend, double scale)
    {
        _tree.Set(LocoBlend, blend);
        _tree.Set(LocoScale, scale);
        if (!_walking)
        {
            _tree.Set(ModeRequest, "loco");
            _walking = true;
        }
    }

    private void Act(FigureClip clip)
    {
        var (asset, speed) = (clip.Clip.Asset, clip.Speed);
        if (!_walking && asset == _actAsset && clip.Mirror == _mirrored)
        {
            if (speed != _actSpeed)
            {
                _tree.Set(ActScale[_slot], speed);
                _actSpeed = speed;
            }

            return;
        }

        // A new clip goes into the other slot, so the transition cross-fades from the one playing and
        // restarts the new one from its first frame (input reset). Mirroring swaps left and right of the
        // whole body (idle variety); it changes only with a new clip and stays while walking.
        _slot = 1 - _slot;
        _act[_slot].Animation = asset;
        _tree.Set(ActScale[_slot], speed);
        _tree.Set(ModeRequest, ActSlots[_slot]);
        _actAsset = asset;
        _actSpeed = speed;
        _walking = false;
        if (clip.Mirror != _mirrored)
        {
            _mirrored = clip.Mirror;
            _body.Scale = new Vector3(_mirrored ? -_bodyScale.X : _bodyScale.X, _bodyScale.Y, _bodyScale.Z);
        }
    }

    /// <summary>Three dots above the head (hidden until the core reports an open question), facing the camera.</summary>
    private void BuildThinkingDots(FigureThinkingDots look, double scale)
    {
        _thinking = new Node3D { Name = "ThinkingDots", Position = new Vector3(0, (float)(look.AboveHeadM * scale), 0), Visible = false };
        HeadAnchor.AddChild(_thinking);
        _thinkingPhase = ThinkingDots.Phase(InhabitantId);
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Label3D
            {
                Name = "Dot" + i,
                Text = ThinkingDots.Glyph,
                FontSize = look.FontPx,
                OutlineSize = Math.Max(1, look.FontPx / 8),
                Offset = new Vector2((float)ThinkingDots.OffsetPx(look, i), 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                NoDepthTest = true,
                Modulate = new Color(1, 1, 1, (float)look.AlphaMin),
            };
            _thinking.AddChild(_dots[i]);
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
                if (_shared.FirstReport("gait:" + gait.Id))
                {
                    GD.PushError($"FIGURE gait '{gait.Id}' clip missing in the model: {asset}");
                }

                continue;
            }

            player.GetAnimation(asset).LoopMode = Animation.LoopModeEnum.Linear;
            loco.AddBlendPoint(new AnimationNodeAnimation { Animation = asset }, gait.MinSpeedCmS, -1, gait.Id);
        }

        var transition = new AnimationNodeTransition { XfadeTime = (float)crossfade };
        transition.AddInput("loco");
        var blend = new AnimationNodeBlendTree();
        blend.AddNode("loco", loco);
        blend.AddNode("loco_speed", new AnimationNodeTimeScale());
        blend.ConnectNode("loco_speed", 0, "loco");
        blend.AddNode("mode", transition);
        blend.ConnectNode("mode", 0, "loco_speed");
        for (var i = 0; i < ActSlots.Length; i++)
        {
            var slot = ActSlots[i];
            _act[i] = new AnimationNodeAnimation { Animation = Asset(library.FallbackClip)! };
            transition.AddInput(slot);
            transition.SetInputReset(i + 1, true);
            blend.AddNode(slot, _act[i]);
            blend.AddNode(slot + "_speed", new AnimationNodeTimeScale());
            blend.ConnectNode(slot + "_speed", 0, slot);
            blend.ConnectNode("mode", i + 1, slot + "_speed");
        }

        blend.ConnectNode("output", 0, "mode");

        _tree = new AnimationTree { Name = "Motion", TreeRoot = blend };
        root.AddChild(_tree);
        _tree.AnimPlayer = _tree.GetPathTo(player);
        _tree.Set(LocoScale, 1.0);
        foreach (var scale in ActScale)
        {
            _tree.Set(scale, 1.0);
        }

        _tree.Active = true;
    }

    private static Vector3 ToVector(Point3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
}
