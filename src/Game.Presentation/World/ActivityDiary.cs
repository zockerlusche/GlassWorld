using System.Globalization;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.World;

/// <summary>A line of an inhabitant's diary: when (as the clock showed it) and what it did.</summary>
/// <param name="Day">Game day.</param>
/// <param name="Time">Time of day as shown by the clock.</param>
/// <param name="Text">The activity, in the player's language.</param>
public sealed record DiaryLine(int Day, string Time, string Text);

/// <summary>
/// What each inhabitant did, one after the other (Marcel W1726: "eine Historie, was der jeweilige Charakter
/// tut, damit man sieht, oh, der hat das schon gemacht"). The core keeps the diary (TL #20472: one entry per
/// started action, saved with the game, nothing missed at x16); this takes what is new and keeps the newest
/// <c>shown</c> entries per inhabitant. Entries stay language-neutral; the lines are built when they are drawn,
/// so a language switch or a newly learned name shows at once. Godot-free.
/// </summary>
public sealed class ActivityDiary(int shown, WorldCalendar calendar)
{
    private readonly int _shown = Math.Max(1, shown);
    private readonly Dictionary<string, List<DiaryEntry>> _entries = new(StringComparer.Ordinal);
    private IInhabitantDiary? _source;
    private long _taken;

    /// <summary>Changes whenever an entry is added or the diary starts over.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Takes the core's entries that are new since the last call (all of them the first time). Another diary,
    /// or one with an older version (a different or older world was loaded), starts over.
    /// </summary>
    /// <returns>Number of new entries.</returns>
    public int Sync(IInhabitantDiary diary)
    {
        ArgumentNullException.ThrowIfNull(diary);
        if (!ReferenceEquals(diary, _source) || diary.Version < _taken)
        {
            _source = diary;
            _taken = 0;
            if (_entries.Count > 0)
            {
                _entries.Clear();
                Version++;
            }
        }

        if (diary.Version <= _taken)
        {
            return 0;
        }

        var added = 0;
        foreach (var entry in diary.Since(_taken))
        {
            _taken = Math.Max(_taken, entry.Seq);
            if (!_entries.TryGetValue(entry.InhabitantId, out var list))
            {
                _entries[entry.InhabitantId] = list = [];
            }

            list.Add(entry);
            if (list.Count > _shown)
            {
                list.RemoveAt(0);
            }

            added++;
        }

        _taken = Math.Max(_taken, diary.Version);
        if (added > 0)
        {
            Version++;
        }

        return added;
    }

    /// <summary>The kept entries of an inhabitant, newest first.</summary>
    public IReadOnlyList<DiaryEntry> EntriesOf(string inhabitantId) =>
        _entries.TryGetValue(inhabitantId, out var list) ? [.. Enumerable.Reverse(list)] : [];

    /// <summary>
    /// The diary of an inhabitant, newest first, in the language of now: <paramref name="tr"/> translates a
    /// key (returning the key when unknown), <paramref name="nameOf"/> is the screen's name of an inhabitant id.
    /// </summary>
    public IReadOnlyList<DiaryLine> Of(string inhabitantId, Func<string, string> tr, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(nameOf);
        return [.. EntriesOf(inhabitantId).Select(e =>
        {
            var time = calendar.At(e.Tick);
            var other = e.TargetId is not null && e.TargetType is null ? e.TargetId : null;
            return new DiaryLine(
                time.DayOfSeason,
                string.Create(CultureInfo.InvariantCulture, $"{time.Hour:00}:{time.Minute:00}"),
                InhabitantActivity.Line(e.Action, other, e.Topic, e.TargetType, tr, nameOf));
        })];
    }
}
