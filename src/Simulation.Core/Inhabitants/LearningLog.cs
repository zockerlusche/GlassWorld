using System.Collections.Frozen;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>One line of the learning log (A1620), language-neutral: what happened, to whom, about what.</summary>
/// <param name="Tick">When.</param>
/// <param name="InhabitantId">Who.</param>
/// <param name="Kind">Core event kind (content/learning/log.json).</param>
/// <param name="Action">The event's action (an action, a need, a process), if any.</param>
/// <param name="TypeId">The thing's type it is about, if any.</param>
/// <param name="OtherId">Another inhabitant it is about (names, meetings), if any.</param>
/// <param name="Property">For knowledge: the property.</param>
/// <param name="Source">For knowledge: seen, experienced, heard or guessed.</param>
/// <param name="Value">For knowledge: whether the thing has the property, as the inhabitant now believes.</param>
/// <param name="From">For knowledge heard: who told it.</param>
public sealed record LearningEntry(long Tick, string InhabitantId, string Kind, string? Action, string? TypeId, string? OtherId, string? Property, string? Source, bool? Value, string? From);

/// <summary>
/// The learning log (B2685 part B, A1620): it reads along the core's event stream and keeps, per event kind named
/// in the data, a language-neutral line until a writer takes the finished days. It is no world state: not hashed,
/// not saved (a loaded world starts its log empty), and bounded.
/// </summary>
public sealed class LearningLog
{
    /// <summary>Most lines held before the oldest are dropped (a technical bound: a writer takes them every day).</summary>
    public const int MaxEntries = 20_000;

    private const string KnowledgeGained = "knowledge_gained";
    private readonly FrozenSet<string> _kinds;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly List<LearningEntry> _entries = [];

    /// <summary>Creates the log for the given event kinds.</summary>
    public LearningLog(IEnumerable<string> kinds, InhabitantsSystem inhabitants, WorldObjects objects)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _kinds = kinds.ToFrozenSet(StringComparer.Ordinal);
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    }

    /// <summary>The lines not yet taken, oldest first.</summary>
    public IReadOnlyList<LearningEntry> Entries => _entries;

    /// <summary>Notes an event of a kind the data names (called for every reported event).</summary>
    public void Observe(WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_kinds.Contains(e.Kind))
        {
            return;
        }

        LearningEntry entry;
        if (e.Kind == KnowledgeGained && e.TargetId is { } typeId && e.Detail is { } detail && detail.IndexOf('/', StringComparison.Ordinal) is var cut and > 0)
        {
            // Detail is 'property/source'; the belief is already set when the event is reported (GW1 #23236), so its
            // value and who told it are read here.
            var property = detail[..cut];
            var source = detail[(cut + 1)..];
            bool? value = null;
            string? from = null;
            if (_inhabitants.TryGet(e.InhabitantId, out var who) && who.Beliefs.TryGet(typeId, property, out var belief))
            {
                value = belief.Value;
                from = belief.ToldBy;
            }

            entry = new LearningEntry(e.Tick, e.InhabitantId, e.Kind, e.Action, typeId, null, property, source, value, from);
        }
        else if (e.TargetId is { } target && _inhabitants.TryGet(target, out _))
        {
            entry = new LearningEntry(e.Tick, e.InhabitantId, e.Kind, e.Action, null, target, null, null, null, null);
        }
        else
        {
            var type = e.TargetType ?? (e.TargetId is { } id && _objects.TryGet(id, out var thing) ? thing.TypeId : e.TargetId);
            entry = new LearningEntry(e.Tick, e.InhabitantId, e.Kind, e.Action, type, null, null, null, null, null);
        }

        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>Takes (and forgets) every line before <paramref name="tick"/>, oldest first.</summary>
    public IReadOnlyList<LearningEntry> TakeBefore(long tick)
    {
        var taken = _entries.TakeWhile(e => e.Tick < tick).ToList();
        _entries.RemoveRange(0, taken.Count);
        return taken;
    }
}
