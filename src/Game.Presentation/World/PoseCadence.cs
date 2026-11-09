using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Shared by all figures of a scene (W1697): at most one figure changes its pose per frame, and two standing
/// pose changes lie at least <see cref="FigurePoseCadence.StaggerS"/> real seconds apart, so figures never sit
/// down or stand up in lock-step. Starting to walk only waits for a free frame (it must not glide).
/// </summary>
public sealed class PoseGate(double staggerS)
{
    private long _frame = long.MinValue;
    private double _at = double.NegativeInfinity;

    /// <summary>
    /// Whether a figure may change its pose in <paramref name="frame"/> at real time <paramref name="nowS"/>
    /// (<paramref name="frameOnly"/>: walking, actions, leaving a low pose; only the frame counts, the stagger
    /// is for idle variety); if so, the frame is taken.
    /// </summary>
    public bool TryPass(long frame, double nowS, bool frameOnly = false)
    {
        if (frame == _frame || (!frameOnly && nowS - _at < staggerS))
        {
            return false;
        }

        _frame = frame;
        _at = frameOnly ? _at : nowS;
        return true;
    }
}

/// <summary>
/// Calm poses of one figure (W1697), without Godot: what the figure plays after a tick, from the clip the
/// <see cref="AnimationChooser"/> wants and real time. Idle variety starts only after standing idle for a
/// while and changes in real seconds (not core ticks, so fast forward stays calm); a pose is held at least
/// min_pose_s; in a conversation only the data's standing idle behaviours play; pose changes pass the
/// scene's <see cref="PoseGate"/>, so no two figures change in the same frame. All numbers from <c>content/look/figures.json</c> (pose_cadence).
/// </summary>
public sealed class PoseCadence
{
    /// <summary>Idle slots are counted in tenths of a real second.</summary>
    public const double SlotUnitsPerSecond = 10;

    private readonly FigurePoseCadence _data;
    private readonly double _spread;
    private readonly string _inhabitantId;
    private readonly HashSet<string> _conversation;
    private readonly FigureLying? _lying;
    private double _idleSince = double.NaN;
    private FigureClip? _shown;
    private FigureClip? _wanted;
    private bool _leaveAtOnce;
    private bool _wantedIdle;
    private bool _shownIdle;
    private double _shownSince;

    /// <summary>Creates the cadence of one inhabitant's figure.</summary>
    public PoseCadence(FigurePoseCadence data, double idleSlotSpread, string inhabitantId, FigureLying? lying = null)
    {
        _lying = lying;
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(inhabitantId);
        _data = data;
        _spread = idleSlotSpread;
        _inhabitantId = inhabitantId;
        _conversation = new HashSet<string>(data.ConversationIdle, StringComparer.Ordinal);
    }

    /// <summary>The idle slot length in units (<see cref="SlotUnitsPerSecond"/>).</summary>
    public static int SlotUnits(FigurePoseCadence data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Math.Max(1, (int)Math.Round(data.IdleSlotS * SlotUnitsPerSecond));
    }

    /// <summary>
    /// After a tick: what the figure should play from the core's state (<see cref="Update"/> decides when it
    /// shows). <paramref name="nowS"/> is the scene's real clock in seconds; <paramref name="conversation"/> is
    /// true while the inhabitant speaks to another or is spoken to (<see cref="InhabitantActivity.InConversation"/>).
    /// </summary>
    public void Want(AnimationChooser chooser, string? action, bool moving, IReadOnlyDictionary<string, double> values, AnimationMappingTarget? target, bool conversation, double nowS)
    {
        ArgumentNullException.ThrowIfNull(chooser);
        var wanted = chooser.ForInhabitant(action, moving, _inhabitantId, 0, values, target);
        if (!wanted.Walking && chooser.StandsIdle(action))
        {
            _idleSince = double.IsNaN(_idleSince) ? nowS : _idleSince;
            var idleS = nowS - _idleSince;
            var only = conversation ? _conversation : null;
            var clip = idleS < _data.IdleVarietyAfterS
                ? chooser.IdleById(_inhabitantId, _data.RestIdle)
                : chooser.Idle(_inhabitantId, AnimationChooser.IdleSlot(_inhabitantId, (long)((idleS - _data.IdleVarietyAfterS) * SlotUnitsPerSecond), SlotUnits(_data), _spread), values, only);
            if (conversation && !Allowed(chooser, clip))
            {
                clip = chooser.IdleById(_inhabitantId, _data.RestIdle);
            }

            wanted = clip with { Missing = wanted.Missing };
        }
        else
        {
            _idleSince = double.NaN;
        }

        // A conversation begins while the figure sits or bends: it gets up without waiting out the hold.
        _leaveAtOnce = conversation && _shown is { Walking: false } shown && chooser.IsIdleClip(shown.Clip.ClipId) && !Allowed(chooser, shown);
        _wanted = wanted;
        _wantedIdle = !wanted.Walking && chooser.IsIdleClip(wanted.Clip.ClipId);
    }

    /// <summary>
    /// Every frame: the clip the figure plays now. The wanted clip shows once the current pose was held long
    /// enough and the scene's gate lets it pass (one figure per frame); starting to walk shows at the next free frame.
    /// </summary>
    public FigureClip Update(double nowS, long frame, PoseGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (_wanted is not { } wanted)
        {
            throw new InvalidOperationException("Want must come first.");
        }

        if (_shown is not { } shown)
        {
            return Show(wanted, nowS);
        }

        if (PoseKey(shown, _lying) == PoseKey(wanted, _lying))
        {
            // The same pose (speed may differ): nothing to hold back.
            _shown = wanted;
            return wanted;
        }

        // Starting or stopping to walk (GW3 review #164: arriving walkers never march in place) and leaving a
        // low pose for a conversation only wait for a free frame. Only idle variety (idle to idle) keeps the
        // stagger between figures; every standing pose is held min_pose_s.
        var free = wanted.Walking || shown.Walking || _leaveAtOnce;
        var variety = _shownIdle && _wantedIdle;
        var hold = free ? 0 : _data.MinPoseS;
        return nowS - _shownSince >= hold && gate.TryPass(frame, nowS, frameOnly: free || !variety) ? Show(wanted, nowS) : shown;
    }

    /// <summary>
    /// The key of what a figure plays: walking, or the asset and mirroring of its clip; a clip played lying
    /// (<paramref name="lying"/>) is a pose of its own even with the asset of a standing one.
    /// </summary>
    public static string PoseKey(FigureClip clip, FigureLying? lying = null) =>
        clip.Walking ? "walk"
        : clip.Clip.Asset + (clip.Mirror ? "/m" : string.Empty) + (lying is not null && LyingPose.IsLying(lying, clip.Clip.ClipId) ? "/lying" : string.Empty);

    private bool Allowed(AnimationChooser chooser, FigureClip clip) =>
        _data.ConversationIdle.Any(id => chooser.IdleById(_inhabitantId, id).Clip.ClipId == clip.Clip.ClipId);

    private FigureClip Show(FigureClip clip, double nowS)
    {
        _shown = clip;
        _shownIdle = _wantedIdle;
        _shownSince = nowS;
        return clip;
    }
}
