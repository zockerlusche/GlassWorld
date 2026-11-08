using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inventions;

/// <summary>
/// One chronicle entry (B8 R9): language-neutral; the base line is built from the kind's text key and
/// these parameters at display time, so names follow the player's language.
/// </summary>
/// <param name="Seq">Running number, starting at 1, never reused.</param>
/// <param name="Tick">Tick of the event.</param>
/// <param name="Kind">Chronicle kind id (content/chronicle/kinds.json).</param>
/// <param name="InhabitantId">Who.</param>
/// <param name="TargetId">What (an invention's type id, an object id …), if any.</param>
/// <param name="Action">Action or process id, if any.</param>
public sealed record ChronicleRecord(long Seq, long Tick, string Kind, string InhabitantId, string? TargetId, string? Action);

/// <summary>Read access to the chronicle for the UI (D471 D8).</summary>
public interface IChronicle
{
    /// <summary>Number of the newest entry (0 = empty).</summary>
    long LastSeq { get; }

    /// <summary>Entries after <paramref name="afterSeq"/>, oldest first.</summary>
    IReadOnlyList<ChronicleRecord> Since(long afterSeq);

    /// <summary>Up to <paramref name="take"/> entries before <paramref name="beforeSeq"/>, newest first (paging).</summary>
    IReadOnlyList<ChronicleRecord> Page(long beforeSeq, int take);
}

/// <summary>
/// The chronicle as core state (a world system): every reported core event whose kind is named by a
/// chronicle kind in the data becomes an entry. Entries are saved and part of the world hash; the
/// system stays out of the hash while it is empty (golden hashes of worlds without entries).
/// </summary>
public sealed class ChronicleSystem : IWorldSystem, IOptionalWorldState, IChronicle
{
    private readonly FrozenDictionary<string, string> _kindByEvent;
    private readonly List<ChronicleRecord> _records = [];

    /// <summary>Creates the chronicle for the kinds in the data.</summary>
    public ChronicleSystem(ChronicleKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var byEvent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in kinds.Kinds)
        {
            if (!byEvent.TryAdd(kind.Event, kind.Id))
            {
                throw new InvalidDataException($"Chronicle: event '{kind.Event}' is named by more than one kind.");
            }
        }

        _kindByEvent = byEvent.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public string Name => "chronicle";

    /// <inheritdoc/>
    public bool HasState => _records.Count > 0;

    /// <inheritdoc/>
    public long LastSeq => _records.Count == 0 ? 0 : _records[^1].Seq;

    /// <summary>All entries, oldest first.</summary>
    public IReadOnlyList<ChronicleRecord> All => _records;

    /// <summary>Takes a reported core event; only kinds named in the data become entries.</summary>
    public void Observe(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (_kindByEvent.TryGetValue(worldEvent.Kind, out var kind))
        {
            _records.Add(new ChronicleRecord(LastSeq + 1, worldEvent.Tick, kind, worldEvent.InhabitantId, worldEvent.TargetId, worldEvent.Action));
        }
    }

    /// <summary>Restores entries (save games), in sequence order.</summary>
    public void Restore(IEnumerable<ChronicleRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records.OrderBy(r => r.Seq))
        {
            if (record.Seq <= LastSeq)
            {
                throw new InvalidDataException($"Chronicle entry {record.Seq} is out of order.");
            }

            _records.Add(record);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChronicleRecord> Since(long afterSeq) => [.. _records.Where(r => r.Seq > afterSeq)];

    /// <inheritdoc/>
    public IReadOnlyList<ChronicleRecord> Page(long beforeSeq, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        return [.. _records.Where(r => r.Seq < beforeSeq).Reverse().Take(take)];
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // Entries come from reported events; nothing happens per tick.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_records.Count);
        foreach (var r in _records)
        {
            hasher.Write(r.Seq).Write(r.Tick).Write(r.Kind).Write(r.InhabitantId).Write(r.TargetId ?? string.Empty).Write(r.Action ?? string.Empty);
        }
    }
}
