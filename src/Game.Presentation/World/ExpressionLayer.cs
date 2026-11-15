using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>What a figure shows of its feelings now (A1732): a face (null = neutral) and an arm gesture (or none).</summary>
/// <param name="Id">The expression's id (look expressions).</param>
/// <param name="Face">smile, sad or surprised; null = neutral.</param>
/// <param name="Gesture">The gesture, or null.</param>
public sealed record ExpressionShown(string Id, string? Face, FigureGesture? Gesture);

/// <summary>
/// The expression layer of one figure (A1732, B2716 (2)), without Godot: an event of the inhabitant shows its expression
/// for the data's seconds (then a calm pause before the next event shows one); otherwise the first mood at or above its
/// level, then the first need at or below its level, shows while it lasts. All from <c>look/expressions.json</c>.
/// </summary>
public sealed class ExpressionLayer(LookExpressions data, IEnumerable<FigureGesture>? gestures)
{
    private readonly LookExpressions _data = data ?? throw new ArgumentNullException(nameof(data));
    private readonly IReadOnlyList<FigureGesture> _gestures = [.. gestures ?? []];
    private ExpressionShown? _event;
    private double _eventUntil;
    private double _calmUntil;
    private double _now;

    /// <summary>An event of the inhabitant: its expression shows unless one showed less than the cooldown ago.</summary>
    public void Event(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (_now < _calmUntil || _data.Events?.FirstOrDefault(e => string.Equals(e.Event, kind, StringComparison.Ordinal)) is not { } trigger
            || Shown(trigger.Expression) is not { } shown)
        {
            return;
        }

        var seconds = _data.Expressions.First(x => x.Id == trigger.Expression).Seconds ?? 2;
        _event = shown;
        _eventUntil = _now + seconds;
        _calmUntil = _now + seconds + _data.CooldownS;
    }

    /// <summary>One frame: what shows now - the event's expression, else the state's (moods, then needs), else null.</summary>
    public ExpressionShown? Update(double realSeconds, IReadOnlyDictionary<string, double> moods, IReadOnlyDictionary<string, double> needs)
    {
        ArgumentNullException.ThrowIfNull(moods);
        ArgumentNullException.ThrowIfNull(needs);
        _now += Math.Max(0, realSeconds);
        if (_event is not null && _now < _eventUntil)
        {
            return _event;
        }

        _event = null;
        foreach (var mood in _data.Moods ?? [])
        {
            if (moods.TryGetValue(mood.Mood, out var value) && value >= mood.AtLeast && Shown(mood.Expression) is { } shown)
            {
                return shown;
            }
        }

        foreach (var need in _data.Needs ?? [])
        {
            if (needs.TryGetValue(need.Need, out var value) && value <= need.AtMost && Shown(need.Expression) is { } shown)
            {
                return shown;
            }
        }

        return null;
    }

    private ExpressionShown? Shown(string id)
    {
        if (_data.Expressions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal)) is not { } expression)
        {
            return null;
        }

        var gesture = expression.Gesture is { } g ? _gestures.FirstOrDefault(x => string.Equals(x.Id, g, StringComparison.Ordinal)) : null;
        return new ExpressionShown(expression.Id, expression.Face?.ToString().ToLowerInvariant(), gesture);
    }
}
