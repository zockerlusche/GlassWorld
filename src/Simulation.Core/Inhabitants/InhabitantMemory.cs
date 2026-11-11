using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>One remembered event of an inhabitant (B4a R5), language-neutral: a kind and what it was about.</summary>
/// <param name="Seq">Running number per inhabitant, starting at 1, never reused.</param>
/// <param name="Tick">Tick it happened; for a counted event (<paramref name="Times"/> above 1) the last time.</param>
/// <param name="Kind">Kind of the remembered event (memory data: an action, an event kind or a word of its own).</param>
/// <param name="AboutKind">What it was about: <c>type</c> or <c>inhabitant</c>; null for nothing.</param>
/// <param name="AboutId">The object type or the inhabitant; null for nothing.</param>
/// <param name="Formative">True if the event shapes the inhabitant (memory data): it must stay in the summary.</param>
/// <param name="Condensed">True once the summary holds it: a formative event that a condensing kept.</param>
/// <param name="Times">How often it happened: repetitions of an everyday event are counted here instead of added (memory data: repeat counted).</param>
/// <param name="Since">Tick of the first time, for an event counted more than once; null = <paramref name="Tick"/>.</param>
/// <param name="Outcome">What came of a remembered action (<see cref="ActionOutcomes"/>: done, found_out, nothing), so an attempt is not remembered as a deed; null for other events.</param>
public sealed record MemoryEntry(long Seq, long Tick, string Kind, string? AboutKind, string? AboutId, bool Formative, bool Condensed = false, int Times = 1, long? Since = null, string? Outcome = null);

/// <summary>
/// The memory of one inhabitant (B4a R5): a short summary, written by the AI service in the compute night
/// and checked by the core, and the remembered events, oldest first. The core only keeps and counts; which
/// events are remembered and which are formative is data (knowledge settings: memory).
/// </summary>
public sealed class InhabitantMemory
{
    private readonly List<MemoryEntry> _events = [];
    private readonly SortedSet<string> _firsts = new(StringComparer.Ordinal);

    /// <summary>The summary so far (internal English text for the AI, never shown to the player); empty until the first condensing.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>The remembered events, oldest first.</summary>
    public IReadOnlyList<MemoryEntry> Events => _events;

    /// <summary>Running number of the newest event ever remembered (0 = none yet).</summary>
    public long LastSeq { get; private set; }

    /// <summary>
    /// Events up to this running number have gone out to a condensing: nothing is counted on them any more
    /// (what was sent must stay what it was), a repetition starts a new entry.
    /// </summary>
    public long SealedSeq { get; private set; }

    /// <summary>What the inhabitant has remembered for the first time already (kind and subject), in ordinal order.</summary>
    public IReadOnlyCollection<string> Firsts => _firsts;

    /// <summary>True once the memory holds anything.</summary>
    public bool HasState => Summary.Length > 0 || _events.Count > 0 || _firsts.Count > 0 || LastSeq > 0;

    /// <summary>The events the summary does not hold yet, oldest first.</summary>
    public IEnumerable<MemoryEntry> Uncondensed => _events.Where(e => !e.Condensed);

    /// <summary>
    /// Whether this is the first time the inhabitant remembers this kind about this subject; from then on it
    /// is not (the answer is kept, so it survives the forgetting of the event itself).
    /// </summary>
    /// <remarks>With an <paramref name="outcome"/> every outcome has its own first time: the first attempt that came to nothing and the first time it worked.</remarks>
    public bool First(string kind, string? aboutKind, string? aboutId, string? outcome = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        return _firsts.Add(kind + "|" + (aboutKind ?? string.Empty) + "|" + (aboutId ?? string.Empty) + (outcome is null ? string.Empty : "|" + outcome));
    }

    /// <summary>
    /// Remembers an event. Beyond <paramref name="max"/> events the oldest one that is not formative is
    /// forgotten first, then the oldest formative one the summary already holds, then the oldest.
    /// With <paramref name="count"/> a repetition - an event of the same kind and outcome about the same
    /// subject whose entry is still open (not sent to a condensing yet) - is counted on that entry instead of
    /// added: one more time, now the newest event, at this tick; a formative first time stays formative. A
    /// deed and an attempt (different <paramref name="outcome"/>) are never counted together.
    /// </summary>
    public void Remember(long tick, string kind, string? aboutKind, string? aboutId, bool formative, int max, bool count = false, string? outcome = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        if (max <= 0)
        {
            return;
        }

        var open = count
            ? _events.FindLastIndex(e => !e.Condensed && e.Seq > SealedSeq && e.Kind == kind && e.AboutKind == aboutKind && e.AboutId == aboutId && e.Outcome == outcome)
            : -1;
        if (open >= 0)
        {
            var before = _events[open];
            _events.RemoveAt(open);
            LastSeq++;
            _events.Add(before with { Seq = LastSeq, Tick = tick, Times = before.Times + 1, Since = before.Since ?? before.Tick, Formative = before.Formative || formative });
            return;
        }

        LastSeq++;
        _events.Add(new MemoryEntry(LastSeq, tick, kind, aboutKind, aboutId, formative, Outcome: outcome));
        while (_events.Count > max)
        {
            var index = _events.FindIndex(e => !e.Formative);
            index = index >= 0 ? index : _events.FindIndex(e => e.Condensed);
            _events.RemoveAt(Math.Max(0, index));
        }
    }

    /// <summary>
    /// Takes a checked condensing: the new summary replaces the old one; of the events that were sent, those
    /// whose tick is in <paramref name="keptTicks"/> stay (now held by the summary), the others are forgotten.
    /// Events that were not sent stay as they are.
    /// </summary>
    public void Condense(string summary, IReadOnlySet<long> sentSeqs, IReadOnlySet<long> keptTicks)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(sentSeqs);
        ArgumentNullException.ThrowIfNull(keptTicks);
        Summary = summary;
        _events.RemoveAll(e => sentSeqs.Contains(e.Seq) && !keptTicks.Contains(e.Tick));
        for (var i = 0; i < _events.Count; i++)
        {
            if (sentSeqs.Contains(_events[i].Seq))
            {
                _events[i] = _events[i] with { Condensed = true };
            }
        }
    }

    /// <summary>Marks the events up to <paramref name="seq"/> as sent to a condensing: nothing is counted on them any more.</summary>
    public void Seal(long seq) => SealedSeq = Math.Max(SealedSeq, seq);

    /// <summary>Replaces the memory with a saved one (save games).</summary>
    public void Restore(string summary, long lastSeq, IEnumerable<MemoryEntry> events, IEnumerable<string> firsts, long sealedSeq = 0)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(firsts);
        Summary = summary;
        _events.Clear();
        _events.AddRange(events.OrderBy(e => e.Seq));
        LastSeq = Math.Max(lastSeq, _events.Count > 0 ? _events[^1].Seq : 0);
        SealedSeq = sealedSeq;
        _firsts.Clear();
        _firsts.UnionWith(firsts);
    }

    /// <summary>Writes the memory in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Summary).Write(LastSeq).Write(SealedSeq).Write(_events.Count);
        foreach (var e in _events)
        {
            hasher.Write(e.Seq).Write(e.Tick).Write(e.Kind).Write(e.AboutKind ?? string.Empty).Write(e.AboutId ?? string.Empty).Write(e.Formative).Write(e.Condensed)
                .Write(e.Times).Write(e.Since ?? e.Tick).Write(e.Outcome ?? string.Empty);
        }

        hasher.Write(_firsts.Count);
        foreach (var first in _firsts)
        {
            hasher.Write(first);
        }
    }
}
