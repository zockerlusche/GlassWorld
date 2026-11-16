using GlassWorld.Contracts;
using GlassWorld.Game.Prototype.Look;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;
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
/// not-yet-made clips are logged once. Seated clips sit on a seat object near the figure or on the ground,
/// never in the air; a figure that heard the player looks and turns to the camera, waves or talks with its
/// answer (W1688); its silhouette shows through whatever hides it (W1689); pause freezes all of its motion.
/// To sleep it sits down, rolls onto its side and lies fitted to the ground, and gets up the same way in
/// reverse; lying or sitting it never turns its body, only its head looks to the voice (W1729).
/// Presentation only.
/// </summary>
public partial class FigureView : Node3D
{
    private static readonly StringName LocoBlend = "parameters/loco/blend_position";
    private static readonly StringName LocoScale = "parameters/loco_speed/scale";
    private static readonly StringName ModeRequest = "parameters/mode/transition_request";
    private static readonly string[] ActSlots = ["act_a", "act_b"];
    private static readonly StringName[] ActScale = ["parameters/act_a_speed/scale", "parameters/act_b_speed/scale"];

    /// <summary>The pose key while a figure gets up: no clip has it, so the cadence's pose shows once it stands.</summary>
    private const string GettingUp = "/getting-up";

    private FigureShared _shared = null!;
    private FigureMotion _motion = null!;
    private PoseCadence _cadence = null!;
    private string? _gait;
    private Point3 _to;
    private string _shownPose = string.Empty;
    private bool _standsIdle;
    private bool _lying;
    private ShaderMaterial? _paint;
    private double _wetShown = 1.0;
    private string _topColor = "#ffffff";
    private string? _wornShown;

    /// <summary>A1732: an event of this inhabitant - the expression layer may show it (cheering at an invention); what shows, else null.</summary>
    public ExpressionShown? Express(string kind, string? detail = null)
    {
        var shown = _expressions?.Event(kind, detail);
        if (shown?.LookS is { } look && look > 0)
        {
            // A1542: a knock at the glass - it turns to the player first (as when it hears the voice).
            React(new Reaction(look, 0, 0));
        }

        return shown;
    }

    /// <summary>
    /// A1733 (B2716 (3)): another figure nearby shows a moment - this one looks over at it (head and upper body) for
    /// its seconds and may show an expression of its own (joining in), subject to its own calm pause.
    /// </summary>
    public void Watch(Node3D at, double seconds, string? joinIn)
    {
        _watched = at;
        _watchLeft = Math.Max(_watchLeft, seconds);
        if (joinIn is not null)
        {
            _expressions?.Show(joinIn);
        }
    }

    /// <summary>Whether the figure lies now (asleep): it shows no speech, thought or thinking (D1112 #8).</summary>
    public bool Lying => _lying;
    private double _lieSeconds;
    private FigureClip _lieClip;
    private LieClip? _lieStage;
    private bool _laid;
    private readonly List<BodyPoint> _bodyPoints = [];
    private Func<double, double, double> _groundM = (_, _) => 0;
    private AnimationPlayer _player = null!;
    private Transform3D _skeletonInBody = Transform3D.Identity;
    private Vector3 _bodyPosition;
    private AnimationTree _tree = null!;
    private readonly AnimationNodeAnimation[] _act = new AnimationNodeAnimation[2];
    private readonly Label3D[] _dots = new Label3D[ThinkingDots.Count];
    private Node3D _body = null!;
    private MeshInstance3D? _carried;
    private string? _carriedType;
    private FigureGesture? _carryGesture;
    private Vector3 _bodyScale = Vector3.One;
    private Vector3 _bodyRotation;
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
    private WaveModifier _wave = null!;
    private LookAtModifier _lookAt = null!;
    private FaceDriver _face = null!;
    private KneeStraightenModifier _knees = null!;
    private GestureLayer? _gestures;
    private FigureGesture? _gestureWanted;
    private ComfortLook _comfortLook = ComfortLook.None;
    private ExpressionLayer? _expressions;
    private ExpressionShown? _expressionShown;
    private IReadOnlyDictionary<string, double> _moods = new Dictionary<string, double>();
    private IReadOnlyDictionary<string, double> _needs = new Dictionary<string, double>();
    private double _shiverSeconds;
    private bool _shivering;
    private bool _actionShown;
    private Func<IReadOnlyList<Seat>> _seats = () => [];
    private SeatPose? _seat;
    private SeatPose _lastSeat;
    private double _seatShare;
    private bool _frozen;
    private double _lookLeft;

    // The expression of an answer (TL #21712): shown instead of the idle behaviour for its seconds.
    private string? _expression;
    private double _expressLeft;
    private double _waveLeft;
    private double _talkLeft;
    private Node3D? _watched;
    private double _watchLeft;

    /// <summary>Inhabitant id.</summary>
    public string InhabitantId { get; private set; } = string.Empty;

    /// <summary>Anchor above the head (thought bubble, question mark, B3/B6).</summary>
    public Node3D HeadAnchor { get; private set; } = null!;

    /// <summary>The clip playing now (library id), for checks and screenshots.</summary>
    public string CurrentClip { get; private set; } = string.Empty;

    /// <summary>Builds the figure of an inhabitant.</summary>
    /// <param name="inhabitant">The inhabitant's snapshot.</param>
    /// <param name="spec">How it is drawn.</param>
    /// <param name="shared">What all figures share.</param>
    /// <param name="groundHeight">Drawn ground height at (x, z).</param>
    /// <param name="seats">The seat objects of the world now (null: none, every seated clip sits on the ground).</param>
    public static FigureView Create(InhabitantSnapshot inhabitant, FigureSpec spec, FigureShared shared, Func<double, double, float> groundHeight, Func<IReadOnlyList<Seat>>? seats = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(shared);
        var figures = shared.Figures;
        // Figures mark their pixels in the stencil buffer, so their silhouette shows only behind cover (W1695).
        var comic = GD.Load<Shader>("res://shaders/figure_comic.gdshader");
        var outline = figures.Outline && shared.HullOutline ? GD.Load<Shader>("res://shaders/outline_hull.gdshader") : null;
        var figure = FigureFactory.Build(FigureLook.From(spec), comic, outline);

        var start = new Point3(inhabitant.Position.X, groundHeight(inhabitant.Position.X, inhabitant.Position.Z), inhabitant.Position.Z);
        var view = new FigureView
        {
            Name = inhabitant.Id,
            InhabitantId = inhabitant.Id,
            _shared = shared,
            _ground = groundHeight,
            _groundM = (x, z) => groundHeight(x, z),
            _scale = spec.Scale,
            _motion = new FigureMotion(start, figures.TurnRateDegS, figures.TurnBackAfterSteps ?? 1),
            _cadence = new PoseCadence(figures.PoseCadence, figures.IdleSlotSpread, inhabitant.Id, figures.Lying),
            _seats = seats ?? (() => []),
            _wave = figure.Wave,
            _lookAt = figure.Look,
            _face = figure.Face,
        };
        view.AddChild(figure.Root);
        view.HeadAnchor = new Node3D { Name = "HeadAnchor", Position = new Vector3(0, (float)(figures.HeadAnchorM * spec.Scale), 0) };
        view.AddChild(view.HeadAnchor);
        view._body = figure.Root;
        view._bodyScale = figure.Root.Scale;
        view._bodyRotation = figure.Root.Rotation;
        view._bodyPosition = figure.Root.Position;
        view.BuildThinkingDots(figures.ThinkingDots, spec.Scale);
        view._knees = new KneeStraightenModifier { Name = "Knees" };
        view._knees.Watch(figures.Lying.BodyPoints.Select(p => p.Bone));
        var skeleton = figure.Root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
        skeleton.AddChild(view._knees);

        // Eating and drinking as arm gestures over the clip (D958 D2), after the clip and the knees.
        view._gestures = new GestureLayer(skeleton, view, spec.Scale);
        for (Node node = skeleton; node != figure.Root; node = node.GetParent())
        {
            // Where the skeleton is in the figure: the watched bones are read in the skeleton's space.
            view._skeletonInBody = node is Node3D spatial ? spatial.Transform * view._skeletonInBody : view._skeletonInBody;
        }
        // A1577: the shared paint of body and clothing - a worn thing colours the top (region_top) while it is worn.
        view._paint = figure.Body.MaterialOverride as ShaderMaterial;
        view._topColor = spec.Top;
        if (figures.Silhouette.Enabled && figure.Body.MaterialOverride is { } material)
        {
            // Behind a tree, rock or object the figure shows as a calm silhouette in its colour (W1689).
            var last = material;
            while (last.NextPass is { } next)
            {
                last = next;
            }

            last.NextPass = Silhouette(figures.Silhouette, spec);
        }

        // Replace the prototype's idle/walk/jog tree by the library's gaits and two action slots.
        figure.Tree.Active = false;
        figure.Tree.QueueFree();
        var player = figure.Root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().Single();
        view._player = player;
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
    public void Apply(InhabitantSnapshot inhabitant, long tick, int ticks, bool place = false, IReadOnlyList<InhabitantSnapshot>? all = null, IReadOnlyList<WorldObject>? objects = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var figures = _shared.Figures;
        // A1385 (D1105 H3b): up in a tree the figure is drawn at the height it climbed (the core's ClimbedCm).
        var up = (inhabitant.ClimbedCm ?? 0) / 100.0;
        var to = new Point3(inhabitant.Position.X, _ground(inhabitant.Position.X, inhabitant.Position.Z) + up, inhabitant.Position.Z);
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
        if (all is not null && MayTurn && InhabitantActivity.FacePoint(inhabitant, all, objects) is { } face)
        {
            // Only an upright figure turns to the other inhabitant or the thing it acts on (W1936); lying or sitting
            // it does not swivel (W1729).
            _motion.Face(face.X, face.Z, figures.WalkMinStepM);
        }

        // What the core state asks for; the cadence shows it calmly, in real seconds (W1697, pose_cadence).
        var target = all is null ? null : InhabitantActivity.TargetKind(inhabitant, all);
        var conversation = all is not null && InhabitantActivity.InConversation(inhabitant, all);
        // A1632: how high the thing is - crouch at the ground, reach at a bush (look data, through the variant's root).
        var reach = target == AnimationMappingTarget.Object && inhabitant.TargetId is { } aim && objects?.FirstOrDefault(o => o.Id == aim) is { } thing
            ? _shared.ReachOf?.Invoke(thing.TypeId)
            : null;
        _cadence.Want(chooser, inhabitant.Action, moving, _shared.Values, target, conversation, Time.GetTicksMsec() / 1000.0, reach);
        _gestureWanted = GestureCadence.Choose(figures.Gestures, inhabitant.Action, reach);
        // A1729 (A1379 rest): how warm it feels shows when no action's gesture does (cold: arms to the body, a shiver).
        _comfortLook = GestureCadence.ForComfort(figures.Comfort, figures.Gestures, inhabitant.ComfortLevel);
        // A1785 (B2823): what it carries sits in its hands - drawn here, not on the ground by the object view.
        Carry(inhabitant.Held is { } held ? objects?.FirstOrDefault(o => o.Id == held)?.TypeId : null, figures);
        (_moods, _needs) = (inhabitant.Moods, inhabitant.Needs);
        _gait = inhabitant.Gait;
        _to = to;
        _standsIdle = chooser.StandsIdle(inhabitant.Action);
        Show(_cadence.Update(Time.GetTicksMsec() / 1000.0, (long)Engine.GetProcessFrames(), _shared.Gate));
        // After the pose (D1112 #8): a figure lying asleep shows no thinking dots.
        // A1788: the dots only while just the effect is open; a puzzling question shows the screen's one "?" (no dots beside it).
        var mark = ThinkingDots.Mark(inhabitant.OpenQuestionIds.Count, inhabitant.PuzzlingQuestionIds.Count, _lying);
        _thinking.Visible = mark == HeadMark.Dots;
        foreach (var dot in _dots)
        {
            dot.Visible = mark == HeadMark.Dots;
        }


        // A1577 (K4): what it wears shows in the colour of its top; taken off, the outfit's own colour is back.
        var worn = inhabitant.Worn is { } wornId ? _shared.WornColor?.Invoke(wornId) : null;
        // A1385 (B5-8): wet clothes and hair look darker, in steps of the wetness (BodySystem).
        var wetShade = GlassWorld.Presentation.World.BareGround.WetShade(figures.WetShade, inhabitant.WetPermille);
        if (wetShade != _wetShown && _paint is not null)
        {
            _paint.SetShaderParameter("wet_shade", (float)wetShade);
            _wetShown = wetShade;
        }

        if (worn != _wornShown && _paint is not null)
        {
            _paint.SetShaderParameter("region_top", Color.FromHtml(worn ?? _topColor));
            _wornShown = worn;
        }
        if (place)
        {
            // Placed (after a warm-up, a load) it lies or stands at once, without the way there.
            _lieSeconds = _lying ? LyingPose.TotalS(figures.Lying) : 0;
        }
    }

    /// <summary>Whether the figure may turn its whole body: upright only (W1729).</summary>
    private bool MayTurn => LyingPose.MayTurn(_lying, _lieSeconds, _seat is not null, _seatShare);

    /// <summary>Plays what the cadence shows now: the walk blend or an action/idle clip, seated where it sits.</summary>
    private void Show(FigureClip clip)
    {
        var chooser = _shared.Chooser;
        var figures = _shared.Figures;
        _shownPose = PoseCadence.PoseKey(clip, figures.Lying);
        var lying = !clip.Walking && LyingPose.IsLying(figures.Lying, clip.Clip.ClipId);
        // The gesture shows only with the action's own clip: not on the way there, not lying, not idling.
        _actionShown = !clip.Walking && !lying && !chooser.IsIdleClip(clip.Clip.ClipId);
        if (clip.Missing is { } missing)
        {
            Report(missing);
        }

        if (lying)
        {
            // Lying down belongs to the action (W1729): it sits down, then rolls onto its side (see _Process);
            // already seated on the ground it rolls over at once.
            if (!_lying && !_lastSeat.OnSeat && _seatShare >= 0.5)
            {
                _lieSeconds = Math.Max(_lieSeconds, figures.Lying.SitS);
            }

            _lying = true;
            _lieClip = clip;
            _seat = null;
            _shared.Seats.Sit(InhabitantId, null);
            Report(clip.Clip);
            return;
        }

        _lying = false;
        if (_lieSeconds > 0)
        {
            // It gets up first, in reverse; what the cadence shows plays once it stands.
            _shownPose = GettingUp;
            return;
        }

        if (_expressLeft > 0 && _expression is { } shown && !clip.Walking && _standsIdle)
        {
            // Its answer without words (or with them): the expression it shows, standing where it is (TL #21712).
            clip = new FigureClip(chooser.ForExpression(shown), Walking: false, clip.Missing);
        }
        else if (_lookLeft > 0 && !clip.Walking && _standsIdle)
        {
            // Hearing the player it stands and looks up instead of its idle behaviour (no ducking, W1688).
            clip = new FigureClip(chooser.ForClip(figures.Greeting.ListenClip), Walking: false, clip.Missing);
        }

        _seat = !clip.Walking && SeatChoice.IsSeated(figures.Sitting, clip.Clip.ClipId)
            ? SeatChoice.Choose(figures.Sitting, _to.X, _to.Z, _scale, _seats(), seat => _shared.Seats.TakenByOther(InhabitantId, seat))
            : null;
        _shared.Seats.Sit(InhabitantId, _seat);
        if (clip.Walking)
        {
            // The core's gait (stroll … run, #112) plays as it is; without one the gait follows the game-time
            // speed. Either way the playback roughly follows the speed on screen.
            if (_gait is { } gaitId && chooser.ForGait(gaitId) is { } core)
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
        Act(clip.Clip.Asset, clip.Speed, clip.Mirror);
        CurrentClip = clip.Clip.ClipId;
    }

    /// <summary>
    /// Plays the clip of the stage between standing and lying: the sit transition, the lying clip, the rise
    /// transition; the transitions at the speed that fits them into the data's sitting time.
    /// </summary>
    private void ShowLieStage(LieClip stage)
    {
        if (stage == _lieStage)
        {
            return;
        }

        _lieStage = stage;
        var lying = _shared.Figures.Lying;
        if (stage == LieClip.Lie)
        {
            Act(_lieClip.Clip.Asset, _lieClip.Speed, _lieClip.Mirror);
            CurrentClip = _lieClip.Clip.ClipId;
            return;
        }

        var choice = _shared.Chooser.ForClip(stage == LieClip.Sit ? lying.SitClip : lying.RiseClip);
        Report(choice);
        var fits = !choice.FellBack && _player.HasAnimation(choice.Asset);
        Act(choice.Asset, fits ? LyingPose.ClipSpeed(lying, _player.GetAnimation(choice.Asset).Length) : choice.Speed, _mirrored);
        CurrentClip = choice.ClipId;
    }

    /// <summary>
    /// Pause and speed 0 (W1688): a frozen figure stops everything that moves it, clips, cross-fades, the
    /// procedural wave, look and face, its walk and the thought dots; unfrozen it goes on where it stopped.
    /// </summary>
    public void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        _body.ProcessMode = frozen ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
    }

    /// <summary>The figure heard the player or answers (W1688): it looks and turns to the voice, waves, talks.</summary>
    public void React(Reaction reaction)
    {
        _lookLeft = Math.Max(_lookLeft, reaction.LookS);
        _waveLeft = Math.Max(_waveLeft, reaction.WaveS);
        _talkLeft = Math.Max(_talkLeft, reaction.TalkS);
        if (reaction.Expression is { } expression && reaction.ExpressS > 0)
        {
            _expression = expression;
            _expressLeft = reaction.ExpressS;
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        // A figure that leaves the island frees its seat (A1570).
        _shared.Seats.Leave(InhabitantId);
        base._ExitTree();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_frozen)
        {
            return;
        }

        // A pose held back by the cadence shows once its time has come (one figure per frame, W1697).
        if (_shownPose.Length > 0)
        {
            var clip = _cadence.Update(Time.GetTicksMsec() / 1000.0, (long)Engine.GetProcessFrames(), _shared.Gate);
            if (PoseCadence.PoseKey(clip, _shared.Figures.Lying) != _shownPose)
            {
                Show(clip);
            }
        }

        // Even pace from tick to tick (R17); the height follows the ground under the figure. A figure that
        // gets up stays where it lay until it stands.
        var figures = _shared.Figures;
        var lying = figures.Lying;
        if (_lying || _lieSeconds <= 0)
        {
            _motion.Advance(delta);
        }

        var p = _motion.Position;

        // Seated: the hips move to the seat (or down to the ground) at the data's pace, never in the air.
        // Changing seat (A1570): it first gets up from where it sits, then sits down on the new place - no jump.
        var shareRate = figures.Sitting.SettleMS / Math.Max(0.05, figures.Sitting.ClipSeatHeightM * _scale);
        (_lastSeat, _seatShare) = SeatEasing.Step(_seat, _lastSeat, _seatShare, shareRate, delta);
        var (x, z) = _lastSeat.OnSeat ? (p.X + ((_lastSeat.X - p.X) * _seatShare), p.Z + ((_lastSeat.Z - p.Z) * _seatShare)) : (p.X, p.Z);
        var y = _ground(x, z) + (float)(_lastSeat.LiftM * _seatShare);
        var (knees, hips) = (_lastSeat.KneeShare * _seatShare, 0.0);

        // Sleeping it lies on its side on the ground (W1695, W1729): it sits down on the ground where it
        // stands (off a seat object first), rolls about its hips onto its side and lies fitted to the ground;
        // getting up runs the same seconds back.
        if (!_lying || _lieSeconds > 0 || !_lastSeat.OnSeat || _seatShare <= 0)
        {
            _lieSeconds = LyingPose.Advance(lying, _lieSeconds, _lying, delta);
        }

        _knees.Watching = _lying || _lieSeconds > 0;
        if (_lieSeconds > 0)
        {
            var stage = LyingPose.Stage(lying, _lieSeconds, _lying);
            ShowLieStage(stage.Clip);
            var onGround = SeatChoice.Choose(figures.Sitting, p.X, p.Z, _scale, []);
            _seatShare = 0;
            (x, z) = (p.X, p.Z);
            y = _ground(x, z) + (float)(onGround.LiftM * stage.Sit);
            var legs = LyingPose.Legs(lying, stage.Roll);
            knees = (onGround.KneeShare * stage.Sit) + ((lying.KneeStraighten - onGround.KneeShare) * legs);
            hips = lying.HipOpen * legs;
            Lay(lying, stage.Roll, x, y, z);
        }
        else
        {
            _lieStage = null;
            if (_laid)
            {
                _body.Transform = new Transform3D(BodyBasis(), _bodyPosition);
                _laid = false;
            }
        }

        Position = new Vector3((float)x, y, (float)z);
        _knees.Share = (float)knees;
        _knees.HipShare = (float)hips;

        // A1732 (B2716): what it feels shows over the action - an event's short expression, else a mood or need.
        _expressions ??= _shared.Expressions is { } expressionData ? new ExpressionLayer(expressionData, figures.Gestures) : null;
        _expressionShown = !_lying && _lieSeconds <= 0 ? _expressions?.Update(delta, _moods, _needs) : null;
        HearAndAnswer(delta, figures.Greeting);
        var still = !_lying && _lieSeconds <= 0;
        var actionGesture = _actionShown && still ? _gestureWanted : null;
        // A1729: the comfort gesture only while it stands idle or walks - never over an action, never lying.
        // A1785: carrying holds the arms over every other gesture but an action's own.
        _gestures?.Update(delta, actionGesture ?? (still && _carryGesture is not null ? _carryGesture : !_actionShown && still ? _expressionShown?.Gesture ?? _comfortLook.Gesture : null));

        // A1729: a slight sideways shiver of the body while it is cold; back in its place once it stops.
        _shiverSeconds += delta;
        var shiver = still && !_laid ? GestureCadence.ShiverAt(_comfortLook, _shiverSeconds, _bodyScale.X) : 0;
        if (shiver != 0 || _shivering)
        {
            _body.Position = _bodyPosition + new Vector3((float)shiver, 0, 0);
            _shivering = shiver != 0;
        }

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

    /// <summary>The figure's own turn and size in its node, mirrored while its clip plays mirrored.</summary>
    private Basis BodyBasis() =>
        Basis.FromEuler(_bodyRotation) * Basis.FromScale(new Vector3(_mirrored ? -_bodyScale.X : _bodyScale.X, _bodyScale.Y, _bodyScale.Z));

    /// <summary>
    /// Rolls the seated figure about its hips onto its side by <paramref name="roll"/> (0 seated, 1 lying),
    /// slides the hips to the lying spot and fits the body to the ground there (<see cref="LyingPose.Fit"/>
    /// over the watched bones of the final pose): tilted to the slope and lifted so nothing is below the
    /// ground. On the way (sitting down, rolling, getting up) it is lifted as far as its lowest bone needs.
    /// </summary>
    private void Lay(FigureLying lying, double roll, double x, double y, double z)
    {
        var body = BodyBasis();
        var yaw = new Basis(Vector3.Up, (float)_motion.Yaw);
        var laid = new Transform3D(body, _bodyPosition);
        _laid = true;
        if (roll > 0)
        {
            var pivot = LyingPose.Pivot(lying, _shared.Figures.Sitting, _scale);
            var hips = new Vector3(0, (float)pivot.Y, (float)pivot.Z);
            var full = new Basis(Vector3.Back, (float)LyingPose.RollRad(lying, 1));
            WatchedPoints(lying, new Transform3D(yaw * full * body, yaw * (full * (_bodyPosition - hips))));
            var spot = yaw * (hips + new Vector3((float)LyingPose.Slide(lying, _scale, 1), 0, 0));
            var fit = LyingPose.Fit(lying, _bodyPoints, x + spot.X, z + spot.Z, _scale, _groundM);
            var up = (yaw.Inverse() * new Vector3((float)fit.Up.X, (float)fit.Up.Y, (float)fit.Up.Z)).Normalized();
            var tilt = Quaternion.Identity.Slerp(new Quaternion(Vector3.Up, up), (float)roll);
            var turn = new Basis(tilt) * new Basis(Vector3.Back, (float)LyingPose.RollRad(lying, roll));
            var lifted = hips + new Vector3((float)LyingPose.Slide(lying, _scale, roll), (float)((fit.HipY - (y + hips.Y)) * roll), 0);
            laid = new Transform3D(turn * body, lifted + (turn * (_bodyPosition - hips)));
        }

        // Where the watched bones are with this turn, about the figure's spot: none may be below the ground.
        WatchedPoints(lying, new Transform3D(yaw * laid.Basis, yaw * laid.Origin));
        laid.Origin += new Vector3(0, (float)LyingPose.Lift(_bodyPoints, x, y, z, _groundM), 0);
        _body.Transform = laid;
    }

    /// <summary>Fills the body points from the watched bones of the final pose, moved by <paramref name="place"/> (from the figure's model to world axes).</summary>
    private void WatchedPoints(FigureLying lying, Transform3D place)
    {
        _bodyPoints.Clear();
        var index = 0;
        foreach (var point in lying.BodyPoints)
        {
            if (_knees.TryPoint(index++, out var bone))
            {
                var at = place * (_skeletonInBody * bone);
                _bodyPoints.Add(new BodyPoint(at.X, at.Y, at.Z, point.RadiusM * _scale));
            }
        }
    }

    /// <summary>Looks and turns to the voice (the camera) while it hears, waves and talks for their seconds.</summary>
    private void HearAndAnswer(double delta, FigureGreeting greeting)
    {
        _lookLeft = Math.Max(0, _lookLeft - delta);
        _expressLeft = Math.Max(0, _expressLeft - delta);
        _waveLeft = Math.Max(0, _waveLeft - delta);
        _talkLeft = Math.Max(0, _talkLeft - delta);
        _watchLeft = Math.Max(0, _watchLeft - delta);
        var ease = greeting.EasePerS;
        var looking = _lookLeft > 0 && GetViewport()?.GetCamera3D() is not null;
        if (looking)
        {
            var voice = GetViewport().GetCamera3D().GlobalPosition;
            _lookAt.Target = voice;
            if (!_walking && MayTurn)
            {
                // Standing, the whole body turns to the voice; walking, lying or sitting only the head and
                // upper body do (W1729: a lying body does not turn on the ground).
                _motion.Face(GreetingReaction.YawTowards(Position.X, Position.Z, voice.X, voice.Z));
            }
        }

        // A1733: the voice first; otherwise it looks over at another's moment while awake (head and upper body only).
        var watching = !looking && _watchLeft > 0 && !_lying && _lieSeconds <= 0 && _watched is not null && IsInstanceValid(_watched);
        if (watching)
        {
            _lookAt.Target = _watched!.GlobalPosition;
        }

        // Lying it keeps its arms down: no wave from the ground.
        _lookAt.Weight = (float)GreetingReaction.Ease(_lookAt.Weight, looking || watching ? 1 : 0, ease, delta);
        _wave.Weight = (float)GreetingReaction.Ease(_wave.Weight, _waveLeft > 0 && _lieSeconds <= 0 ? 1 : 0, ease, delta);
        _face.Expression = _talkLeft > 0 ? "talk" : _expressionShown?.Face ?? "neutral";
    }

    /// <summary>A1785: the shape of an object type and its size factor (from the object view), set by the presenter.</summary>
    public Func<string, (Mesh Mesh, Material Material, double SizeM)?>? ShapeOf { get; set; }

    /// <summary>The type it carries now (checks), or null.</summary>
    public string? CarriedType => _carriedType;

    /// <summary>
    /// A1785 (B2823): the carried thing in front of the body at the data's height and lie, turning with the body; the arms
    /// hold it with a steady carry gesture. Changed only when another thing (or none) is carried.
    /// </summary>
    private void Carry(string? typeId, LookFigures figures)
    {
        if (string.Equals(typeId, _carriedType, StringComparison.Ordinal))
        {
            return;
        }

        _carriedType = typeId;
        _carried?.QueueFree();
        _carried = null;
        _carryGesture = null;
        if (typeId is null || _shared.Carried is not { } data || ShapeOf?.Invoke(typeId) is not { } shape)
        {
            return;
        }

        var look = CarriedPose.For(data, typeId, _shared.PropertiesOf?.Invoke(typeId) ?? new HashSet<string>());
        var turn = look.Lie switch
        {
            CarryWayLie.Across => new Basis(Vector3.Forward, Mathf.Pi / 2),
            CarryWayLie.Slanted => new Basis(Vector3.Forward, Mathf.Pi / 4) * new Basis(Vector3.Right, -Mathf.Pi / 8),
            _ => Basis.Identity,
        };
        // Under the figure's root, which carries the figure's own scale (review GW1 #500): height and distance are metres
        // for size 1 times the figure's size, so divided by the root's scale; the thing keeps its size in the world.
        var root = new Vector3(Math.Abs(_bodyScale.X), _bodyScale.Y, _bodyScale.Z);
        var size = (float)shape.SizeM;
        _carried = new MeshInstance3D
        {
            Name = "Carried",
            Mesh = shape.Mesh,
            MaterialOverride = shape.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Transform = new Transform3D(turn.Scaled(new Vector3(size / root.X, size / root.Y, size / root.Z)), new Vector3(0, (float)(look.HeightM * _scale) / root.Y, (float)(look.ForwardM * _scale) / root.Z)),
        };
        _body.AddChild(_carried);
        var gesture = CarriedPose.GestureOf(look);
        _carryGesture = figures.Gestures?.FirstOrDefault(g => string.Equals(g.Id, gesture, StringComparison.Ordinal));
    }

    /// <summary>The see-through pass: drawn only where something in front hides the figure (W1689).</summary>
    private static ShaderMaterial Silhouette(FigureSilhouette look, FigureSpec spec)
    {
        var color = look.Color switch
        {
            FigureSilhouetteColor.Bottom => spec.Bottom,
            FigureSilhouetteColor.Hair => spec.HairColor,
            FigureSilhouetteColor.Skin => spec.Skin,
            _ => spec.Top,
        };
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/figure_silhouette.gdshader") };
        material.SetShaderParameter("color", new Color(color));
        material.SetShaderParameter("fill_alpha", (float)look.FillAlpha);
        material.SetShaderParameter("edge_alpha", (float)look.EdgeAlpha);
        material.SetShaderParameter("min_cover_m", (float)(look.MinCoverM * spec.Scale));
        return material;
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

    private void Act(string asset, double speed, bool mirror)
    {
        if (!_walking && asset == _actAsset && mirror == _mirrored)
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
        if (mirror != _mirrored)
        {
            _mirrored = mirror;
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

                // Above the cloud cover (R25), the outline just below the dots.
                RenderPriority = CloudCover.AbovePriority + 1,
                OutlineRenderPriority = CloudCover.AbovePriority,
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
