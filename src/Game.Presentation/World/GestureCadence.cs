using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>Where a gesture stands now: how strongly it overrides the clip, and how far the hand is on its way to the head.</summary>
/// <param name="Weight">Influence of the arm IK (0 = the clip alone, up to the gesture's <c>influence</c>).</param>
/// <param name="ToHead">0 = at the start point (<c>from_body</c>), 1 = at the head (<c>to_head</c>).</param>
/// <param name="PullM">A1542: how far the hand is drawn back from the head target now (a tap at the glass), metres for size 1.</param>
public readonly record struct GestureState(double Weight, double ToHead, double PullM = 0);

/// <summary>How a comfort level shows on a figure (A1729): a gesture (or none) and a shiver.</summary>
/// <param name="Gesture">The gesture, or null.</param>
/// <param name="ShiverM">Amplitude of the shiver for a figure of size 1, metres; 0 = none.</param>
/// <param name="ShiverHz">Its frequency.</param>
public sealed record ComfortLook(FigureGesture? Gesture, double ShiverM, double ShiverHz)
{
    /// <summary>Nothing to show.</summary>
    public static ComfortLook None { get; } = new(null, 0, 0);
}

/// <summary>
/// Arm gestures over the running pose (D958 D2, look data <c>gestures</c>), without Godot: which gesture shows an
/// action, and its calm cycle - reach out (rise), stay at the start point (dip), move to the head (lift), hold,
/// lower, rest. Frame, not outcome: the core says that an inhabitant eats or drinks; this only shows it.
/// </summary>
public static class GestureCadence
{
    /// <summary>The first gesture for <paramref name="action"/> whose reach fits (a gesture without reach fits any), or null.</summary>
    public static FigureGesture? Choose(IEnumerable<FigureGesture>? gestures, string? action, AnimationMappingReach? reach)
    {
        if (gestures is null || action is null)
        {
            return null;
        }

        var height = reach switch
        {
            AnimationMappingReach.Low => "low",
            AnimationMappingReach.Mid => "mid",
            AnimationMappingReach.High => "high",
            _ => null,
        };
        return gestures.FirstOrDefault(g => g.Actions.Contains(action, StringComparer.Ordinal)
            && (g.Reach is null || (height is not null && g.Reach.Contains(height, StringComparer.Ordinal))));
    }

    /// <summary>
    /// A1729 (A1379 rest): how a comfort level shows - the gesture of its comfort entry (looked up among the gestures)
    /// and the shiver (amplitude in metres for size 1, frequency). Nothing for a level without an entry or no level.
    /// </summary>
    public static ComfortLook ForComfort(IEnumerable<FigureComfort>? comfort, IEnumerable<FigureGesture>? gestures, string? level)
    {
        if (comfort is null || level is null || comfort.FirstOrDefault(c => string.Equals(c.Level, level, StringComparison.Ordinal)) is not { } entry)
        {
            return ComfortLook.None;
        }

        var gesture = entry.Gesture is { } id ? gestures?.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal)) : null;
        return new ComfortLook(gesture, entry.ShiverM ?? 0, entry.ShiverHz ?? 0);
    }

    /// <summary>The shiver's offset now (metres, sideways): a sine at the data's frequency, scaled by the figure; 0 without one.</summary>
    public static double ShiverAt(ComfortLook look, double seconds, double scale)
    {
        ArgumentNullException.ThrowIfNull(look);
        return look.ShiverM > 0 && look.ShiverHz > 0 ? look.ShiverM * scale * Math.Sin(2 * Math.PI * look.ShiverHz * seconds) : 0;
    }

    /// <summary>Real seconds of one full cycle.</summary>
    public static double CycleS(FigureGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        // Dip and lift belong to a start point; without one the hand goes to the head at once (as in At).
        var path = gesture.FromBody is { Count: 3 } ? (gesture.DipS ?? 0) + (gesture.LiftS ?? 0) : 0;
        return gesture.RiseS + path + gesture.HoldS + gesture.LowerS + gesture.RestS;
    }

    /// <summary>The gesture <paramref name="seconds"/> after it began (the cycle repeats while the action lasts).</summary>
    public static GestureState At(FigureGesture gesture, double seconds)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var cycle = CycleS(gesture);
        var t = cycle <= 0 ? 0 : Math.Max(0, seconds) % cycle;
        var fromStart = gesture.FromBody is { Count: 3 };
        var (dip, lift) = fromStart ? (gesture.DipS ?? 0, gesture.LiftS ?? 0) : (0, 0);
        var start = fromStart ? 0.0 : 1.0;
        var peak = gesture.Influence;

        if (t < gesture.RiseS)
        {
            return new GestureState(peak * Smooth(t / gesture.RiseS), start);
        }

        t -= gesture.RiseS;
        if (t < dip)
        {
            return new GestureState(peak, start);
        }

        t -= dip;
        if (t < lift)
        {
            return new GestureState(peak, Smooth(t / lift));
        }

        t -= lift;
        if (t < gesture.HoldS)
        {
            return new GestureState(peak, 1, Tap(gesture, t));
        }

        t -= gesture.HoldS;
        return t < gesture.LowerS
            ? new GestureState(peak * (1 - Smooth(t / gesture.LowerS)), 1)
            : new GestureState(0, 1);
    }

    /// <summary>
    /// A1542: how far the hand is drawn back <paramref name="held"/> seconds into the hold - whole taps over the hold
    /// (a gentle cosine), so the hold begins and ends at the target; 0 without tap data.
    /// </summary>
    private static double Tap(FigureGesture gesture, double held)
    {
        if (gesture.TapHz is not > 0 || gesture.TapM is not > 0 || gesture.HoldS <= 0)
        {
            return 0;
        }

        var taps = Math.Max(1, Math.Round(gesture.HoldS * gesture.TapHz.Value));
        return gesture.TapM.Value * (1 - Math.Cos(2 * Math.PI * taps * held / gesture.HoldS)) / 2;
    }

    /// <summary>Calm start and end of a move (smoothstep).</summary>
    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - (2 * x));
    }
}
