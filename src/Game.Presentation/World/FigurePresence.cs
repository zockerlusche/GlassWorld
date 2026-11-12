using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>A thing an inhabitant can sit on: where it lies and how high its seat is above the ground.</summary>
/// <param name="X">Position x in metres.</param>
/// <param name="Z">Position z in metres.</param>
/// <param name="HeightM">Seat height above the ground in metres.</param>
public readonly record struct Seat(double X, double Z, double HeightM);

/// <summary>How a seated figure sits.</summary>
/// <param name="OnSeat">True on a seat object, false on the ground.</param>
/// <param name="X">Where the hips go (x): the seat, or the figure's own spot.</param>
/// <param name="Z">Where the hips go (z).</param>
/// <param name="LiftM">Offset of the figure's root above the ground there (negative: lowered).</param>
/// <param name="KneeShare">How far the knees straighten (0 = clip as is, 1 = legs straight).</param>
public readonly record struct SeatPose(bool OnSeat, double X, double Z, double LiftM, double KneeShare);

/// <summary>
/// Seated poses only on something (W1688), without Godot: a seated clip sits on the nearest seat object
/// within the data's distance, its hips at that object's seat height; without one the figure sits on the
/// ground, lowered so the hips touch the terrain, with straightened legs. Never in the air.
/// </summary>
public static class SeatChoice
{
    /// <summary>Whether a library clip shows a seated pose (data: seated_clips).</summary>
    public static bool IsSeated(FigureSitting data, string clipId)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.SeatedClips.Contains(clipId);
    }

    /// <summary>The seat objects among the objects (types and seat heights from the data).</summary>
    public static List<Seat> Seats(FigureSitting data, IEnumerable<WorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objects);
        var heights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var seat in data.Seats)
        {
            foreach (var type in seat.Types)
            {
                heights.TryAdd(type, seat.SeatHeightM);
            }
        }

        var seats = new List<Seat>();
        foreach (var o in objects)
        {
            if (heights.TryGetValue(o.TypeId, out var height))
            {
                seats.Add(new Seat(o.Position.X, o.Position.Z, height));
            }
        }

        return seats;
    }

    /// <summary>
    /// How a figure of <paramref name="scale"/> standing at (x, z) sits. A seat another figure holds
    /// (<paramref name="taken"/>, A1570) is not offered: two figures never share one stone.
    /// </summary>
    public static SeatPose Choose(FigureSitting data, double x, double z, double scale, IReadOnlyList<Seat> seats, Func<Seat, bool>? taken = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(seats);
        var clipSeat = data.ClipSeatHeightM * scale;
        Seat? best = null;
        var bestDistance = data.SeatSearchM;
        foreach (var seat in seats)
        {
            if (taken?.Invoke(seat) == true)
            {
                continue;
            }

            var d = Math.Sqrt(((seat.X - x) * (seat.X - x)) + ((seat.Z - z) * (seat.Z - z)));
            if (d <= bestDistance)
            {
                best = seat;
                bestDistance = d;
            }
        }

        if (best is { } s)
        {
            // A seat lower than the clip's chair straightens the knees by the missing share, so the feet
            // stay on the ground instead of in it.
            var missing = clipSeat > 0 ? Math.Clamp(1 - (s.HeightM / clipSeat), 0, 1) : 0;
            return new SeatPose(true, s.X, s.Z, s.HeightM - clipSeat, missing * data.GroundKneeStraighten);
        }

        return new SeatPose(false, x, z, -clipSeat, data.GroundKneeStraighten);
    }
}

/// <summary>
/// Who sits where (A1570, D843 b): a seat belongs to the figure that sat down on it until it gets up or the seat is
/// gone, so a second figure takes another seat or the ground. Presentation only; the core keeps its own spacing.
/// </summary>
public sealed class SeatBook
{
    private readonly Dictionary<string, Seat> _held = new(StringComparer.Ordinal);

    /// <summary>Whether another figure than <paramref name="figureId"/> holds the seat.</summary>
    public bool TakenByOther(string figureId, Seat seat) =>
        _held.Any(h => !string.Equals(h.Key, figureId, StringComparison.Ordinal) && Same(h.Value, seat));

    /// <summary>The figure sits on <paramref name="pose"/> (a seat) or not (ground or standing): it holds that seat or none.</summary>
    public void Sit(string figureId, SeatPose? pose)
    {
        ArgumentException.ThrowIfNullOrEmpty(figureId);
        if (pose is { OnSeat: true } p)
        {
            _held[figureId] = new Seat(p.X, p.Z, 0);
        }
        else
        {
            _held.Remove(figureId);
        }
    }

    /// <summary>The figure is gone (removed from the island): its seat is free.</summary>
    public void Leave(string figureId) => _held.Remove(figureId);

    private static bool Same(Seat a, Seat b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Z - b.Z) < 0.01;
}

/// <summary>
/// Sitting down, getting up and changing seat at the data's pace (A1570, D843 b), without Godot: a figure that
/// is to sit somewhere else first gets up from where it sits (the share eases to 0) and only then sits down on
/// the new place - it never slides or jumps from one seat to the next.
/// </summary>
public static class SeatEasing
{
    /// <summary>Share below which a figure counts as standing (it may move to another seat).</summary>
    public const double Standing = 0.001;

    /// <summary>
    /// One frame: <paramref name="wanted"/> is where the figure should sit now (null = standing),
    /// <paramref name="shown"/> where it sits, <paramref name="share"/> how far (0 standing .. 1 seated).
    /// Returns where it sits after this frame and how far.
    /// </summary>
    public static (SeatPose Shown, double Share) Step(SeatPose? wanted, SeatPose shown, double share, double perSecond, double seconds)
    {
        if (wanted is { } w && (share <= Standing || SamePlace(w, shown)))
        {
            shown = w;
        }

        var goal = wanted is { } g && SamePlace(g, shown) ? 1.0 : 0.0;
        return (shown, GreetingReaction.Ease(share, goal, perSecond, seconds));
    }

    /// <summary>Whether two poses sit at the same place (a seat, or the same spot on the ground).</summary>
    public static bool SamePlace(SeatPose a, SeatPose b) =>
        a.OnSeat == b.OnSeat && Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Z - b.Z) < 0.01 && Math.Abs(a.LiftM - b.LiftM) < 0.01;
}

/// <summary>
/// Pause and speed 0 freeze the figures (W1688), without Godot: the presentation follows the speed factor
/// and reports when the figures must stop playing (clips, cross-fades, procedural motion) or go on.
/// </summary>
public sealed class PlaybackFreeze
{
    /// <summary>Whether the figures are frozen now.</summary>
    public bool Frozen { get; private set; }

    /// <summary>Takes the speed of this frame; true when <see cref="Frozen"/> changed.</summary>
    public bool Update(double speed)
    {
        var frozen = !(speed > 0);
        if (frozen == Frozen)
        {
            return false;
        }

        Frozen = frozen;
        return true;
    }
}

/// <summary>What one figure shows when it heard the player, in real seconds (0 = nothing of it).</summary>
/// <param name="LookS">Look up and turn to the voice.</param>
/// <param name="WaveS">Raise the arm and wave.</param>
/// <param name="TalkS">Move the mouth (a spoken answer).</param>
/// <param name="Expression">The expression of the answer to show instead of the idle behaviour, or null (TL #21712).</param>
/// <param name="ExpressS">Show that expression.</param>
public readonly record struct Reaction(double LookS, double WaveS, double TalkS, string? Expression = null, double ExpressS = 0);

/// <summary>
/// Visible reaction to the player's voice (W1688), without Godot: who hears a sentence (the one spoken to
/// and every inhabitant within the data's radius of it), how long hearers look and turn to the voice (the
/// camera), and what the speaker shows with its answer (a wave for the data's expressions, talking for a
/// spoken reply). Presentation only.
/// </summary>
public static class GreetingReaction
{
    /// <summary>The ids that hear a sentence to <paramref name="addressedId"/>, the addressed first.</summary>
    public static List<string> Hearers(FigureGreeting data, string addressedId, IEnumerable<(string Id, double X, double Z)> figures)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(figures);
        var all = figures.ToList();
        var hearers = new List<string>();
        if (all.FirstOrDefault(f => f.Id == addressedId) is not { Id: not null } addressed)
        {
            return hearers;
        }

        hearers.Add(addressedId);
        foreach (var f in all)
        {
            var d = Math.Sqrt(((f.X - addressed.X) * (f.X - addressed.X)) + ((f.Z - addressed.Z) * (f.Z - addressed.Z)));
            if (f.Id != addressedId && d <= data.HearRadiusM)
            {
                hearers.Add(f.Id);
            }
        }

        return hearers;
    }

    /// <summary>
    /// The reaction to a conversation event: hearers of a sentence look at the voice; the speaker of an
    /// answer keeps looking, waves for a wave expression and talks with a spoken reply. Null for other events.
    /// </summary>
    public static Reaction? For(FigureGreeting data, string kind, string? expression, bool hasText)
    {
        ArgumentNullException.ThrowIfNull(data);
        var wave = expression is not null && data.WaveExpressions.Contains(expression) ? data.WaveS : 0;
        var reaction = kind switch
        {
            "conversation_started" => new Reaction(data.ReactS, 0, 0),
            "said" => new Reaction(data.AfterAnswerS, wave, hasText ? data.TalkS : 0),
            "gestured" or "declined" => new Reaction(data.AfterAnswerS, wave, 0),
            "not_understood" => new Reaction(data.AfterAnswerS, wave, hasText ? data.TalkS : 0),

            // No answer came (the service was away or too slow): the figure keeps looking, says nothing (TL #21712).
            "no_answer" => new Reaction(data.AfterAnswerS, 0, 0),
            _ => (Reaction?)null,
        };

        return reaction is { } r && kind != "conversation_started" && Expression(data, kind, expression, hasText) is { } shown
            ? r with { Expression = shown, ExpressS = data.ExpressS ?? 0 }
            : reaction;
    }

    /// <summary>
    /// The expression an answering figure shows (TL #21712, D763 Z26: a reaction without words instead of a fixed
    /// sentence): the one the model chose, unless it is a wave (the wave has its own motion); for a decline or a
    /// not-understanding without words and without a chosen expression the quiet default of the data. Null = none.
    /// </summary>
    public static string? Expression(FigureGreeting data, string kind, string? expression, bool hasText)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((data.ExpressS ?? 0) <= 0 || kind is not ("said" or "gestured" or "declined" or "not_understood"))
        {
            return null;
        }

        if (expression is not null)
        {
            return data.WaveExpressions.Contains(expression) ? null : expression;
        }

        return hasText ? null : kind switch
        {
            "declined" => data.DeclineExpression,
            "not_understood" => data.NotUnderstoodExpression,
            _ => null,
        };
    }

    /// <summary>Facing (radians, atan2 of x and z like <see cref="FigureMotion.Yaw"/>) from a figure towards the voice.</summary>
    public static double YawTowards(double fromX, double fromZ, double toX, double toZ) => Math.Atan2(toX - fromX, toZ - fromZ);

    /// <summary>Eases a weight towards its target at <paramref name="perSecond"/>.</summary>
    public static double Ease(double weight, double target, double perSecond, double seconds)
    {
        var step = Math.Max(0, perSecond * seconds);
        return Math.Abs(target - weight) <= step ? target : weight + (Math.Sign(target - weight) * step);
    }
}

/// <summary>
/// Tree crowns that hide an inhabitant fade (W1689), without Godot: a crown near the line from the camera
/// to the figure (between the two) becomes semi-transparent, the more the closer it is to the line.
/// </summary>
public static class CrownFade
{
    /// <summary>The opacity of a crown at <paramref name="crown"/>: 1 outside the radius, the data's alpha on the line.</summary>
    public static double Alpha(FigureCrownFade data, Point3 crown, Point3 camera, Point3 figure)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (dx, dy, dz) = (figure.X - camera.X, figure.Y - camera.Y, figure.Z - camera.Z);
        var length2 = (dx * dx) + (dy * dy) + (dz * dz);
        if (length2 <= 1e-9 || data.RadiusM <= 0)
        {
            return 1;
        }

        var t = (((crown.X - camera.X) * dx) + ((crown.Y - camera.Y) * dy) + ((crown.Z - camera.Z) * dz)) / length2;
        if (t <= 0 || t > 1 + (data.RadiusM / Math.Sqrt(length2)))
        {
            return 1; // behind the camera or behind the figure: it hides nothing
        }

        t = Math.Min(t, 1);
        var (px, py, pz) = (camera.X + (dx * t) - crown.X, camera.Y + (dy * t) - crown.Y, camera.Z + (dz * t) - crown.Z);
        var distance = Math.Sqrt((px * px) + (py * py) + (pz * pz));
        if (distance >= data.RadiusM)
        {
            return 1;
        }

        var share = distance / data.RadiusM;
        return data.Alpha + ((1 - data.Alpha) * share * share);
    }

    /// <summary>Quick test before <see cref="Alpha"/>: whether a point lies in the box around the line, widened by the radius.</summary>
    public static bool NearLine(FigureCrownFade data, Point3 point, Point3 camera, Point3 figure)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = data.RadiusM;
        return point.X >= Math.Min(camera.X, figure.X) - r && point.X <= Math.Max(camera.X, figure.X) + r
            && point.Z >= Math.Min(camera.Z, figure.Z) - r && point.Z <= Math.Max(camera.Z, figure.Z) + r;
    }
}

/// <summary>
/// The bubble above a figure for a conversation event (B6 R13), without Godot: "…" while it thinks about
/// the player's sentence, the answer for the data's speech time, the asking back when it did not understand
/// (a question mark without a sentence), the calm line of the data after no answer (W1701; nothing without
/// one). Null for other events.
/// </summary>
public static class TalkBubble
{
    /// <summary>How long the thinking dots of a sentence stay at most (the answer replaces them).</summary>
    public const double WaitingS = 120;

    /// <summary>Text and real seconds of the bubble for an event, or null.</summary>
    public static (string Text, double Seconds)? For(string kind, string? text, double speechS) => kind switch
    {
        "conversation_started" => ("…", WaitingS),
        "said" or "declined" or "not_understood" when text is { Length: > 0 } => (text, speechS),
        "gestured" or "declined" => ("…", 2.0),
        "not_understood" => ("?", speechS),
        // No answer came: no fixed sentence in the inhabitant's mouth (TL #21712). The dots stay only briefly, like
        // a gesture's - no answer is coming, so they must not read as a wait that never ends (review GW1 #281).
        "no_answer" => ("…", 2.0),
        _ => null,
    };

    /// <summary>Whether a spoken answer must stay (a thought of the same figure does not replace it).</summary>
    public static bool IsSpeech(string kind, string? text) => kind is "said" or "declined" or "not_understood" && text is { Length: > 0 };
}
