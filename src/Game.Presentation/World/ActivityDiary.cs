namespace GlassWorld.Presentation.World;

/// <summary>A line of an inhabitant's diary: when (as the clock showed it) and what it did.</summary>
/// <param name="Day">Game day.</param>
/// <param name="Time">Time of day as shown by the clock.</param>
/// <param name="Text">The activity, in the player's language.</param>
public sealed record DiaryLine(int Day, string Time, string Text);

/// <summary>
/// What each inhabitant did, one after the other (Marcel W1726: "eine Historie, was der jeweilige Charakter
/// tut, damit man sieht, oh, der hat das schon gemacht"). The screen notes the activity line of every
/// inhabitant when it refreshes; a line is added only when the activity changed, and standing around between
/// two activities is left out. Newest first, the newest <c>capacity</c> lines per inhabitant. Godot-free.
/// </summary>
public sealed class ActivityDiary(int capacity)
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly Dictionary<string, List<DiaryLine>> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);

    /// <summary>Changes whenever a line is added.</summary>
    public long Version { get; private set; }

    /// <summary>Notes what an inhabitant does now; <paramref name="idle"/> marks "nothing in particular".</summary>
    public void Note(string inhabitantId, string activity, bool idle, int day, string time)
    {
        ArgumentException.ThrowIfNullOrEmpty(inhabitantId);
        ArgumentNullException.ThrowIfNull(activity);
        if (_last.GetValueOrDefault(inhabitantId) == activity)
        {
            return;
        }

        _last[inhabitantId] = activity;
        if (idle || activity.Length == 0)
        {
            return;
        }

        if (!_lines.TryGetValue(inhabitantId, out var lines))
        {
            _lines[inhabitantId] = lines = [];
        }

        lines.Add(new DiaryLine(day, time, activity));
        if (lines.Count > _capacity)
        {
            lines.RemoveAt(0);
        }

        Version++;
    }

    /// <summary>The diary of an inhabitant, newest first.</summary>
    public IReadOnlyList<DiaryLine> Of(string inhabitantId) =>
        _lines.TryGetValue(inhabitantId, out var lines) ? [.. Enumerable.Reverse(lines)] : [];
}
