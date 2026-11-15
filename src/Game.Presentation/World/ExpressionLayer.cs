using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>What a figure shows of its feelings now (A1732): a face (null = neutral) and an arm gesture (or none).</summary>
/// <param name="Id">The expression's id (look expressions).</param>
/// <param name="Face">smile, sad or surprised; null = neutral.</param>
/// <param name="Gesture">The gesture, or null.</param>
/// <param name="Seconds">How long it shows as a moment (real seconds); null = the default.</param>
/// <param name="LookS">A1542: seconds the figure looks to the player as it starts; null = it does not.</param>
public sealed record ExpressionShown(string Id, string? Face, FigureGesture? Gesture, double? Seconds = null, double? LookS = null);

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

    /// <summary>
    /// An event of the inhabitant (with its detail, A1542: the message kind of mail_arrived): its expression shows unless
    /// one showed less than the cooldown ago - or the entry skips the pause (a knock); the shown one, else null.
    /// </summary>
    public ExpressionShown? Event(string kind, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return _data.Events?.FirstOrDefault(e => string.Equals(e.Event, kind, StringComparison.Ordinal) && (e.Detail is null || string.Equals(e.Detail, detail, StringComparison.Ordinal))) is { } trigger
            ? Show(trigger.Expression, trigger.SkipsPause == true)
            : null;
    }

    /// <summary>
    /// Shows this expression for its seconds unless one showed less than the cooldown ago (an event's, or A1733 joining in
    /// another's); the shown one, else null.
    /// </summary>
    public ExpressionShown? Show(string expressionId, bool skipPause = false)
    {
        ArgumentNullException.ThrowIfNull(expressionId);
        if ((!skipPause && _now < _calmUntil) || Shown(expressionId) is not { } shown)
        {
            return null;
        }

        var seconds = shown.Seconds ?? 2;
        _event = shown;
        _eventUntil = _now + seconds;
        _calmUntil = _now + seconds + _data.CooldownS;
        return shown;
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
        return new ExpressionShown(expression.Id, expression.Face?.ToString().ToLowerInvariant(), gesture, expression.Seconds, expression.LookS);
    }
}

/// <summary>Someone who might see another's expression (A1733): where it stands on the ground and whether it is awake.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="X">Position x, metres.</param>
/// <param name="Z">Position z, metres.</param>
/// <param name="Awake">False while it lies asleep: it sees nothing.</param>
public sealed record Onlooker(string Id, double X, double Z, bool Awake);

/// <summary>Who notices another's expression (A1733, B2716 (3)), from <c>look/expressions.json</c> <c>join_in</c>, without Godot.</summary>
public static class JoinIn
{
    /// <summary>
    /// The awake others within the data's radius of the one showing it, nearest first; nobody for an expression the
    /// data does not name or without data.
    /// </summary>
    public static IReadOnlyList<string> Who(ExpressionJoinIn? data, string expressionId, string shownBy, double x, double z, IEnumerable<Onlooker> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (data is null || !data.Notice.Contains(expressionId, StringComparer.Ordinal))
        {
            return [];
        }

        var reach = data.RadiusM * data.RadiusM;
        return [.. others
            .Where(o => o.Awake && !string.Equals(o.Id, shownBy, StringComparison.Ordinal))
            .Select(o => (o.Id, Distance: ((o.X - x) * (o.X - x)) + ((o.Z - z) * (o.Z - z))))
            .Where(o => o.Distance <= reach)
            .OrderBy(o => o.Distance)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => o.Id)];
    }
}
