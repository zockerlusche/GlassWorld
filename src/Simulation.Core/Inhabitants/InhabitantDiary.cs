using System.Collections.Frozen;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// One diary entry (TL #20472): an action an inhabitant started. Language-neutral; the screen builds the
/// line from the action's text key and these values when it draws, so names and language are those of now.
/// The conversation request reads the start tick of the running action from it (W1737); the answers are
/// logged, so a replay does not depend on the diary.
/// </summary>
/// <param name="Seq">Running number over all inhabitants, starting at 1, never reused.</param>
/// <param name="Tick">Tick the action started (after walking to the target).</param>
/// <param name="InhabitantId">Who.</param>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">The target's id (an inhabitant or an object), if any.</param>
/// <param name="TargetType">The object type of a thing target at the start (the thing may be gone later); null for an inhabitant or no target.</param>
/// <param name="Topic">What an action towards another inhabitant is about (an object type), if any.</param>
public sealed record DiaryEntry(long Seq, long Tick, string InhabitantId, string Action, string? TargetId, string? TargetType, string? Topic);

/// <summary>Read access to the diary for the presentation.</summary>
public interface IInhabitantDiary
{
    /// <summary>Number of the newest entry (0 = empty); grows with every entry.</summary>
    long Version { get; }

    /// <summary>Kept entries with a number greater than <paramref name="version"/>, oldest first.</summary>
    IReadOnlyList<DiaryEntry> Since(long version);
}

/// <summary>
/// What each inhabitant did (TL #20472, W1726): one entry per started action, taken from the core's
/// action-started event, so nothing is missed at any speed. The newest <c>capacity</c> entries per
/// inhabitant are kept; actions the data leave out (resting in place) get none. Presentation-only history:
/// the simulation never reads it, so it is saved with the game but kept OUT of the world hash (like the
/// content fingerprint); it is not a world system.
/// </summary>
public sealed class InhabitantDiary : IInhabitantDiary
{
    private readonly int _capacity;
    private readonly FrozenSet<string> _leftOut;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly SortedDictionary<string, Queue<DiaryEntry>> _entries = new(StringComparer.Ordinal);

    // All entries in seq order, append-only; entries trimmed from an inhabitant's queue are only marked and
    // leave in one compaction once they are the majority, so Since costs O(log n + new entries) (review GW3 #201).
    private readonly List<DiaryEntry> _log = [];
    private readonly HashSet<long> _trimmed = [];

    /// <summary>Creates the diary from the data (null = no diary: nothing is kept).</summary>
    public InhabitantDiary(Contracts.Diary? settings, InhabitantsSystem inhabitants, WorldObjects objects)
    {
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(objects);
        _capacity = Math.Max(0, settings?.Capacity ?? 0);
        _leftOut = (settings?.LeftOutActions ?? []).ToFrozenSet(StringComparer.Ordinal);
        _inhabitants = inhabitants;
        _objects = objects;
    }

    /// <summary>Entries kept per inhabitant.</summary>
    public int Capacity => _capacity;

    /// <inheritdoc/>
    public long Version { get; private set; }

    /// <summary>All kept entries, oldest first (save games).</summary>
    public IReadOnlyList<DiaryEntry> All => [.. _log.Where(e => !_trimmed.Contains(e.Seq))];

    /// <summary>The kept entries of one inhabitant, oldest first.</summary>
    public IReadOnlyList<DiaryEntry> Of(string inhabitantId) =>
        _entries.TryGetValue(inhabitantId, out var queue) ? [.. queue] : [];

    /// <inheritdoc/>
    public IReadOnlyList<DiaryEntry> Since(long version)
    {
        if (version >= Version)
        {
            return [];
        }

        // Binary search for the first entry with a number greater than version.
        int low = 0, high = _log.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_log[mid].Seq <= version)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        var result = new List<DiaryEntry>(_log.Count - low);
        for (var i = low; i < _log.Count; i++)
        {
            if (!_trimmed.Contains(_log[i].Seq))
            {
                result.Add(_log[i]);
            }
        }

        return result;
    }

    /// <summary>Takes a reported core event; only started actions that are not left out become entries.</summary>
    public void Observe(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (_capacity == 0 || worldEvent.Kind != InhabitantsSystem.ActionStarted || worldEvent.Action is not { } action || _leftOut.Contains(action))
        {
            return;
        }

        string? type = null;
        string? topic = null;
        if (worldEvent.TargetId is { } targetId)
        {
            if (_objects.TryGet(targetId, out var thing))
            {
                type = thing.TypeId;
            }
            else if (_inhabitants.All.FirstOrDefault(i => i.Id == worldEvent.InhabitantId) is { } teller)
            {
                topic = _inhabitants.TopicOf(teller);
            }
        }

        Add(new DiaryEntry(Version + 1, worldEvent.Tick, worldEvent.InhabitantId, action, worldEvent.TargetId, type, topic));
    }

    /// <summary>
    /// Replaces the diary with saved entries (save games; older saves have none). <paramref name="version"/> is
    /// the saved running number (null in saves that did not write it: the newest saved number), so numbers
    /// continue across save and load even when nothing was kept. Note: entries beyond the capacity of the
    /// current data are dropped here, oldest first, so a lower capacity in the data shortens the history of a
    /// loaded world for good (review GW2 #201).
    /// </summary>
    public void Restore(IEnumerable<DiaryEntry> entries, long? version = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        _log.Clear();
        _trimmed.Clear();
        Version = 0;
        foreach (var entry in entries.OrderBy(e => e.Seq))
        {
            if (entry.Seq <= Version)
            {
                throw new InvalidDataException($"Diary entry {entry.Seq} is out of order.");
            }

            Version = entry.Seq;
            Keep(entry);
        }

        Version = Math.Max(Version, version ?? 0);
    }

    private void Add(DiaryEntry entry)
    {
        if (Keep(entry))
        {
            Version = entry.Seq;
        }
    }

    /// <summary>Keeps an entry (false when the capacity is 0) and trims its inhabitant's oldest beyond the capacity.</summary>
    private bool Keep(DiaryEntry entry)
    {
        if (_capacity == 0)
        {
            return false;
        }

        if (!_entries.TryGetValue(entry.InhabitantId, out var queue))
        {
            _entries[entry.InhabitantId] = queue = new Queue<DiaryEntry>();
        }

        queue.Enqueue(entry);
        _log.Add(entry);
        while (queue.Count > _capacity)
        {
            _trimmed.Add(queue.Dequeue().Seq);
        }

        if (_trimmed.Count > _log.Count / 2)
        {
            _log.RemoveAll(e => _trimmed.Contains(e.Seq));
            _trimmed.Clear();
        }

        return true;
    }
}
