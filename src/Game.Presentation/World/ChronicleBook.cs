using GlassWorld.Contracts;
using GlassWorld.Simulation.Inventions;
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
/// The chronicle as the player reads it (B8 R9/R10, D471 D8): the core keeps the chronicle as saved
/// state (<see cref="IChronicle"/>, GW1 #75); this view takes its entries of the kinds listed in
/// content/chronicle/kinds.json, newest first, also those from before a save game was loaded.
/// <see cref="Line"/> gives the base line (text key + parameters, DE/EN, no AI).
/// </summary>
public sealed class ChronicleBook(ChronicleKinds kinds, WorldCalendar calendar)
{
    private readonly Dictionary<string, ChronicleKind> _byId = (kinds ?? throw new ArgumentNullException(nameof(kinds))).Kinds.ToDictionary(k => k.Id, StringComparer.Ordinal);
    private readonly List<ChronicleEntry> _entries = [];
    private long _lastSeq;
    private IChronicle? _source;

    /// <summary>Entries, newest first.</summary>
    public IReadOnlyList<ChronicleEntry> Entries => _entries;

    /// <summary>Changes whenever an entry is added (for the window to update only then).</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Takes the core's entries that are new since the last call (all of them the first time). Another
    /// chronicle, or one with fewer entries (a different or older world was loaded), starts the book
    /// over, so no entry is skipped (GW1 review #89 K1).
    /// </summary>
    /// <returns>Number of new entries.</returns>
    public int Sync(IChronicle chronicle)
    {
        ArgumentNullException.ThrowIfNull(chronicle);
        if (!ReferenceEquals(chronicle, _source) || chronicle.LastSeq < _lastSeq)
        {
            var hadEntries = _entries.Count > 0;
            _source = chronicle;
            _entries.Clear();
            _lastSeq = 0;
            if (hadEntries)
            {
                Version++;
            }
        }

        return chronicle.LastSeq > _lastSeq ? Add(chronicle.Since(_lastSeq)) : 0;
    }

    /// <summary>Adds core entries in sequence order; entries already taken and unknown kinds are skipped.</summary>
    /// <returns>Number of new entries.</returns>
    public int Add(IEnumerable<ChronicleRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(calendar);
        var added = 0;
        foreach (var r in records.OrderBy(r => r.Seq))
        {
            if (r.Seq <= _lastSeq)
            {
                continue;
            }

            _lastSeq = r.Seq;
            if (_byId.TryGetValue(r.Kind, out var kind))
            {
                var time = calendar.At(r.Tick);
                _entries.Insert(0, new ChronicleEntry(r.Tick, "time.season." + time.SeasonId, time.DayOfSeason, time.Year, kind, r.InhabitantId, r.TargetId, r.Action));
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
    /// Name of a chronicle target in the display language: an invention type ("invented") or a placed
    /// object of an invention type ("first_use"). Null for anything else (the line says "something").
    /// The names are the AI's, checked by the rule guard, in DE and EN (B2535: no player names here).
    /// </summary>
    /// <param name="id">Target id from the entry (type id or object id).</param>
    /// <param name="catalog">Runtime types of the world.</param>
    /// <param name="typeOfObject">Type id of an object id, or null if there is no such object.</param>
    /// <param name="locale">Display language (de/en).</param>
    public static string? InventionName(string id, IRuntimeCatalog catalog, Func<string, string?> typeOfObject, string locale)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(typeOfObject);
        var invention = catalog.Invention(id) ?? (typeOfObject(id) is { } type ? catalog.Invention(type) : null);
        if (invention is null)
        {
            return null;
        }

        var name = locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? invention.Name.En : invention.Name.De;
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
                case "other":
                    // A second inhabitant (quarrel, partnership, a parent): the event target by name.
                    args["other"] = entry.TargetId is null ? "ui.chronicle.someone" : names.Inhabitant(entry.TargetId);
                    break;
                case "action":
                    args["action"] = entry.Action is null ? "ui.chronicle.something" : names.Action(entry.Action);
                    break;
            }
        }

        return (entry.Kind.TextKey, args);
    }
}
