using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>Where a gesture stands now: how strongly it overrides the clip, and how far the hand is on its way to the head.</summary>
/// <param name="Weight">Influence of the arm IK (0 = the clip alone, up to the gesture's <c>influence</c>).</param>
/// <param name="ToHead">0 = at the start point (<c>from_body</c>), 1 = at the head (<c>to_head</c>).</param>
public readonly record struct GestureState(double Weight, double ToHead);

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
            return new GestureState(peak, 1);
        }

        t -= gesture.HoldS;
        return t < gesture.LowerS
            ? new GestureState(peak * (1 - Smooth(t / gesture.LowerS)), 1)
            : new GestureState(0, 1);
    }

    /// <summary>Calm start and end of a move (smoothstep).</summary>
    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - (2 * x));
    }
}
