using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inventions;

/// <summary>A reached milestone (B2598): when, by whom and with which thing type.</summary>
/// <param name="Id">Milestone id of the data.</param>
/// <param name="Tick">Tick it was reached.</param>
/// <param name="InhabitantId">Who made the thing or used the rule.</param>
/// <param name="TypeId">Type of the made thing, or null.</param>
public sealed record ReachedMilestone(string Id, long Tick, string InhabitantId, string? TypeId);

/// <summary>
/// B2598 / D827 R12, R35 (A1583, A1625): the core recognises the world's milestones itself and derives the
/// reached tech level from them. A milestone of the data is reached the first time the inhabitants make a
/// thing whose type has all its properties (events <c>built</c> and <c>combined</c>, by the world's truth,
/// runtime types included) or the first time its rule applies (<c>combined</c> with that rule id). Each is
/// reached once per world and reported as <c>milestone_reached</c> (detail the milestone id, target type the
/// made thing's type). The reached tech level is the highest of the start level and those the reached
/// milestones raise to, in the order of the vocabulary. Nothing here names a milestone, thing or level;
/// the order follows from what the world allows. Deterministic: events in report order, state sorted by id,
/// out of the hash while nothing is reached.
/// </summary>
public sealed class MilestoneSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Event: the world reached a milestone (detail = milestone id).</summary>
    public const string MilestoneReached = "milestone_reached";

    private readonly IReadOnlyList<Milestone> _milestones;
    private readonly IReadOnlyList<string> _techLevels;
    private readonly int _start;
    private readonly ITypeTruth _truth;
    private readonly WorldObjects _objects;
    private readonly InhabitantsSystem _inhabitants;
    private readonly SortedDictionary<string, ReachedMilestone> _reached = new(StringComparer.Ordinal);
    private readonly List<ReachedMilestone> _pending = [];

    /// <summary>Creates the system; it listens to the inhabitants' events.</summary>
    /// <param name="catalog">The milestones of the data (checked at load), or null for none.</param>
    /// <param name="techLevels">Tech levels in their order (vocabulary order).</param>
    /// <param name="startTechLevel">The tech level a world starts with (invention settings).</param>
    /// <param name="truth">The world's type truth (runtime types included).</param>
    /// <param name="objects">The world's things.</param>
    /// <param name="inhabitants">Where the events come from and go to.</param>
    public MilestoneSystem(MilestoneCatalog? catalog, IReadOnlyList<string> techLevels, string startTechLevel, ITypeTruth truth, WorldObjects objects, InhabitantsSystem inhabitants)
    {
        ArgumentNullException.ThrowIfNull(techLevels);
        _milestones = [.. catalog?.Milestones ?? []];
        _techLevels = techLevels;
        _start = Math.Max(0, IndexOf(startTechLevel));
        _truth = truth ?? throw new ArgumentNullException(nameof(truth));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        TechLevelIndex = _start;
        _inhabitants.Reported += Observe;
    }

    /// <inheritdoc/>
    public string Name => "milestones";

    /// <inheritdoc/>
    public bool HasState => _reached.Count > 0;

    /// <summary>The reached milestones in id order.</summary>
    public IReadOnlyCollection<ReachedMilestone> Reached => _reached.Values;

    /// <summary>The reached tech level as a number: its position in the vocabulary <c>tech_level</c>.</summary>
    public int TechLevelIndex { get; private set; }

    /// <summary>The reached tech level id.</summary>
    public string TechLevel => _techLevels.Count == 0 ? string.Empty : _techLevels[TechLevelIndex];

    /// <summary>Raised when the reached tech level rises (new position in the vocabulary).</summary>
    public event Action<int>? TechLevelRaised;

    /// <summary>Whether a milestone has been reached.</summary>
    public bool IsReached(string id) => _reached.ContainsKey(id);

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_pending.Count == 0)
        {
            return;
        }

        // Reported in the step after the making, so everyone has heard of the thing itself first.
        var reached = _pending.ToList();
        _pending.Clear();
        foreach (var milestone in reached)
        {
            _inhabitants.Report(new WorldEvent(MilestoneReached, world.Tick, milestone.InhabitantId, null, null) { TargetType = milestone.TypeId, Detail = milestone.Id });
        }

        Raise();
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_reached.Count);
        foreach (var milestone in _reached.Values)
        {
            hasher.Write(milestone.Id).Write(milestone.Tick).Write(milestone.InhabitantId).Write(milestone.TypeId ?? string.Empty);
        }

        hasher.Write(_pending.Count);
    }

    /// <summary>The state to save.</summary>
    public IReadOnlyList<ReachedMilestone> Capture() => [.. _reached.Values];

    /// <summary>Restores the reached milestones of a saved world; the tech level follows from them.</summary>
    public void Restore(IReadOnlyList<ReachedMilestone>? reached)
    {
        _reached.Clear();
        _pending.Clear();
        foreach (var milestone in reached ?? [])
        {
            _reached[milestone.Id] = milestone;
        }

        TechLevelIndex = _start;
        Raise();
    }

    private void Observe(WorldEvent e)
    {
        if (_milestones.Count == 0 || e.Kind is not (InventionSystem.Built or InhabitantsSystem.Combined))
        {
            return;
        }

        var type = e.TargetType ?? (e.TargetId is { } id && _objects.TryGet(id, out var thing) ? thing.TypeId : null);
        var rule = e.Kind == InhabitantsSystem.Combined ? e.Detail : null;
        foreach (var milestone in _milestones)
        {
            if (_reached.ContainsKey(milestone.Id) || !Fits(milestone, type, rule))
            {
                continue;
            }

            var reached = new ReachedMilestone(milestone.Id, e.Tick, e.InhabitantId, type);
            _reached[milestone.Id] = reached;
            _pending.Add(reached);
        }
    }

    private bool Fits(Milestone milestone, string? type, string? rule)
    {
        if (milestone.Rule is { } wanted)
        {
            return rule is not null && string.Equals(wanted, rule, StringComparison.Ordinal);
        }

        return type is not null && milestone.MadeWith is { Count: > 0 } properties && properties.All(p => _truth.HasProperty(type, p));
    }

    private void Raise()
    {
        var level = _start;
        foreach (var reached in _reached.Values)
        {
            if (_milestones.FirstOrDefault(m => m.Id == reached.Id)?.RaisesTechLevel is { } raises && IndexOf(raises) is var index && index > level)
            {
                level = index;
            }
        }

        if (level != TechLevelIndex)
        {
            var rose = level > TechLevelIndex;
            TechLevelIndex = level;
            if (rose)
            {
                TechLevelRaised?.Invoke(level);
            }
        }
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < _techLevels.Count; i++)
        {
            if (string.Equals(_techLevels[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
