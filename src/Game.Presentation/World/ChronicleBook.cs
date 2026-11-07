using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.World;

/// <summary>One chronicle entry: when, and the base line as text key plus filled parameters.</summary>
/// <param name="Tick">Tick of the event (sorting, replay).</param>
/// <param name="SeasonKey">Season text key of the day (<c>time.season.*</c>).</param>
/// <param name="Day">Day of the season, from 1.</param>
/// <param name="Year">Year since the founding, from 1.</param>
/// <param name="TextKey">Text key of the base line (content/chronicle/kinds.json).</param>
/// <param name="Args">Parameters; values are already display texts or text keys (see <see cref="ChronicleNames"/>).</param>
public sealed record ChronicleEntry(long Tick, string SeasonKey, int Day, long Year, string TextKey, IReadOnlyDictionary<string, string> Args);

/// <summary>
/// How parameters of a base line become text: names of inhabitants and things come from the caller
/// (B2535: player-given names never come from the AI; inventions have generated names in the player's
/// language). A null name falls back to a neutral key.
/// </summary>
/// <param name="Inhabitant">Display name of an inhabitant id.</param>
/// <param name="Target">Display name of a target (object or invention) id, or null if unknown.</param>
/// <param name="Action">Text key of an action or process id.</param>
public sealed record ChronicleNames(Func<string, string> Inhabitant, Func<string, string?> Target, Func<string, string> Action);

/// <summary>
/// The chronicle as the player reads it (B8 R9/R10, D471 D8): core events of the kinds listed in
/// content/chronicle/kinds.json become entries with a base line (text key + parameters, DE/EN, no AI),
/// newest first. Other events are ignored. The core keeps the truth; this is the view.
/// </summary>
public sealed class ChronicleBook(ChronicleKinds kinds, WorldCalendar calendar)
{
    private readonly Dictionary<string, ChronicleKind> _byEvent = (kinds ?? throw new ArgumentNullException(nameof(kinds))).Kinds.ToDictionary(k => k.Event, StringComparer.Ordinal);
    private readonly List<ChronicleEntry> _entries = [];

    /// <summary>Entries, newest first.</summary>
    public IReadOnlyList<ChronicleEntry> Entries => _entries;

    /// <summary>Changes whenever an entry is added (for the window to rebuild only then).</summary>
    public int Version { get; private set; }

    /// <summary>Adds entries for the chronicle events among <paramref name="events"/>.</summary>
    /// <returns>Number of new entries.</returns>
    public int Add(IEnumerable<WorldEvent> events, ChronicleNames names)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(calendar);
        var added = 0;
        foreach (var e in events)
        {
            if (!_byEvent.TryGetValue(e.Kind, out var kind))
            {
                continue;
            }

            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var param in kind.Params)
            {
                switch (param)
                {
                    case ChronicleParam.Inhabitant:
                        args["inhabitant"] = names.Inhabitant(e.InhabitantId);
                        break;
                    case ChronicleParam.Target:
                        args["target"] = (e.TargetId is null ? null : names.Target(e.TargetId)) ?? "ui.chronicle.something";
                        break;
                    case ChronicleParam.Action:
                        args["action"] = e.Action is null ? "ui.chronicle.something" : names.Action(e.Action);
                        break;
                }
            }

            var time = calendar.At(e.Tick);
            _entries.Insert(0, new ChronicleEntry(e.Tick, "time.season." + time.SeasonId, time.DayOfSeason, time.Year, kind.TextKey, args));
            added++;
        }

        if (added > 0)
        {
            Version++;
        }

        return added;
    }
}
