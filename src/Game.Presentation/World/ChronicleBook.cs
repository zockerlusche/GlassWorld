using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.World;

/// <summary>
/// One chronicle entry: when, which kind, and the ids involved. Names are resolved only when the line
/// is shown (GW1 review #64 C2), so a language switch or a later name (B9) shows up in old entries too.
/// </summary>
/// <param name="Tick">Tick of the event (sorting, replay).</param>
/// <param name="SeasonKey">Season text key of the day (<c>time.season.*</c>).</param>
/// <param name="Day">Day of the season, from 1.</param>
/// <param name="Year">Year since the founding, from 1.</param>
/// <param name="Kind">Chronicle kind (content/chronicle/kinds.json).</param>
/// <param name="InhabitantId">Who.</param>
/// <param name="TargetId">The thing, if any.</param>
/// <param name="Action">The action or process, if any.</param>
public sealed record ChronicleEntry(long Tick, string SeasonKey, int Day, long Year, ChronicleKind Kind, string InhabitantId, string? TargetId, string? Action);

/// <summary>
/// How parameters of a base line become text at display time: names of inhabitants and things come
/// from the caller (B2535: player-given names never come from the AI; inventions have generated names
/// in the player's language). A null name falls back to a neutral key.
/// </summary>
/// <param name="Inhabitant">Display name of an inhabitant id.</param>
/// <param name="Target">Display name of a target (object or invention) id, or null if unknown.</param>
/// <param name="Action">Text key of an action or process id.</param>
public sealed record ChronicleNames(Func<string, string> Inhabitant, Func<string, string?> Target, Func<string, string> Action);

/// <summary>
/// The chronicle as the player reads it (B8 R9/R10, D471 D8): core events of the kinds listed in
/// content/chronicle/kinds.json become entries, newest first; <see cref="Line"/> gives the base line
/// (text key + parameters, DE/EN, no AI). Other events are ignored. Until the core keeps the chronicle
/// as state (D5, GW1), this view collects from the event feed since the game started.
/// </summary>
public sealed class ChronicleBook(ChronicleKinds kinds, WorldCalendar calendar)
{
    private readonly Dictionary<string, ChronicleKind> _byEvent = (kinds ?? throw new ArgumentNullException(nameof(kinds))).Kinds.ToDictionary(k => k.Event, StringComparer.Ordinal);
    private readonly List<ChronicleEntry> _entries = [];

    /// <summary>Entries, newest first.</summary>
    public IReadOnlyList<ChronicleEntry> Entries => _entries;

    /// <summary>Changes whenever an entry is added (for the window to update only then).</summary>
    public int Version { get; private set; }

    /// <summary>Adds entries for the chronicle events among <paramref name="events"/>.</summary>
    /// <returns>Number of new entries.</returns>
    public int Add(IEnumerable<WorldEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(calendar);
        var added = 0;
        foreach (var e in events)
        {
            if (_byEvent.TryGetValue(e.Kind, out var kind))
            {
                var time = calendar.At(e.Tick);
                _entries.Insert(0, new ChronicleEntry(e.Tick, "time.season." + time.SeasonId, time.DayOfSeason, time.Year, kind, e.InhabitantId, e.TargetId, e.Action));
                added++;
            }
        }

        if (added > 0)
        {
            Version++;
        }

        return added;
    }

    /// <summary>
    /// Text key and parameters of an entry's base line, with names resolved now. Values are display
    /// texts (names) or text keys (actions, the neutral word), which the caller translates.
    /// </summary>
    public static (string TextKey, IReadOnlyDictionary<string, string> Args) Line(ChronicleEntry entry, ChronicleNames names)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(names);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var param in entry.Kind.Params)
        {
            switch (param)
            {
                case "inhabitant":
                    args["inhabitant"] = names.Inhabitant(entry.InhabitantId);
                    break;
                case "target":
                    args["target"] = (entry.TargetId is null ? null : names.Target(entry.TargetId)) ?? "ui.chronicle.something";
                    break;
                case "action":
                    args["action"] = entry.Action is null ? "ui.chronicle.something" : names.Action(entry.Action);
                    break;
            }
        }

        return (entry.Kind.TextKey, args);
    }
}
