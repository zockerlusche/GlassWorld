using GlassWorld.Contracts;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A need an invention brought (B4b R11, A1372).</summary>
/// <param name="Id">Need id (new, not a vocabulary need).</param>
/// <param name="NameDe">German name.</param>
/// <param name="NameEn">English name.</param>
/// <param name="DecayPerDay">How much it falls per game day.</param>
/// <param name="SourceTypeId">The invention type whose things fill it (TL B2658: bound to the type, not to a property).</param>
public sealed record NeedInfo(string Id, string NameDe, string NameEn, double DecayPerDay, string SourceTypeId)
{
    /// <summary>The actions that fill it on a thing of <see cref="SourceTypeId"/> (sorted).</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>New needs of the world (save games).</summary>
/// <param name="Needs">New needs in the order they came.</param>
public sealed record NeedRegisterState(IReadOnlyList<NeedInfo> Needs);

/// <summary>
/// The register of needs inventions brought (B4b R11, A1372; TL B2658). A new need is bound to its invention's
/// type: an inhabitant gains it the first time it uses a thing of that type with one of the need's actions
/// (InhabitantsSystem), using it again fills it by the data's fill_per_use, and it falls by its own
/// decay_per_day. Refused when its id is a need already, when it names no action, when its decay is outside
/// the data's range, or when the world has as many new needs as the data allow. A new need is never neglected,
/// so it can do no harm. Saved and hashed; without any the register stays out of the world hash.
/// </summary>
public sealed class NeedRegister : IWorldSystem, IOptionalWorldState
{
    /// <summary>Event: a new need of an invention was refused (InhabitantId = inventor, TargetId = invention type, Detail = reason).</summary>
    public const string NeedRefused = "need_refused";

    /// <summary>Event: an inhabitant gained a new need (Action = what it did, TargetId = invention type, Detail = need id).</summary>
    public const string NeedGained = "need_gained";

    /// <summary>Refusal: the id is already a need.</summary>
    public const string ReasonTaken = "need_id_taken";

    /// <summary>Refusal: the need names no action that fills it.</summary>
    public const string ReasonNoActions = "need_no_actions";

    /// <summary>Refusal: the decay per day is outside the data's range.</summary>
    public const string ReasonDecay = "need_decay";

    /// <summary>Refusal: the world has the data's maximum of new needs, or the data allow none.</summary>
    public const string ReasonLimit = "need_limit";

    private readonly HashSet<string> _start;
    private readonly NeedRules? _rules;
    private readonly List<NeedInfo> _added = [];

    /// <summary>Creates the register from the data.</summary>
    /// <param name="needs">The model's needs (their ids are taken).</param>
    /// <param name="rules">The data's rules; null = inventions bring no needs.</param>
    /// <param name="ticksPerDay">Ticks per game day.</param>
    public NeedRegister(IEnumerable<NeedModel> needs, NeedRules? rules, long ticksPerDay)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerDay, 1);
        _start = needs.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        _rules = rules;
        TicksPerDay = ticksPerDay;
    }

    /// <inheritdoc/>
    public string Name => "needs";

    /// <inheritdoc/>
    public bool HasState => _added.Count > 0;

    /// <summary>New needs of inventions, in the order they came.</summary>
    public IReadOnlyList<NeedInfo> Added => _added;

    /// <summary>How much one use fills a new need (data).</summary>
    public double FillPerUse => _rules?.FillPerUse ?? 0;

    /// <summary>Ticks per game day (decay per tick = decay per day / this).</summary>
    public long TicksPerDay { get; }

    /// <summary>Checks a new need without adding it; null if it may be added, otherwise the reason.</summary>
    public string? Check(NewNeed need)
    {
        ArgumentNullException.ThrowIfNull(need);
        if (_start.Contains(need.Id) || _added.Any(n => n.Id == need.Id))
        {
            return ReasonTaken;
        }

        if (need.Actions.Count == 0)
        {
            return ReasonNoActions;
        }

        if (_rules is null || _added.Count >= _rules.MaxRuntimeNeeds)
        {
            return ReasonLimit;
        }

        return need.DecayPerDay < _rules.RuntimeDecayPerDayMin || need.DecayPerDay > _rules.RuntimeDecayPerDayMax ? ReasonDecay : null;
    }

    /// <summary>Adds a new need an approved invention brings; returns null on success, else the reason.</summary>
    public string? Add(NewNeed need, string sourceTypeId)
    {
        ArgumentNullException.ThrowIfNull(need);
        ArgumentException.ThrowIfNullOrEmpty(sourceTypeId);
        var reason = Check(need);
        if (reason is null)
        {
            _added.Add(new NeedInfo(need.Id, need.NameDe, need.NameEn, need.DecayPerDay, sourceTypeId) { Actions = [.. need.Actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] });
        }

        return reason;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // The register changes only through approved inventions; the inhabitants carry the values.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_added.Count);
        foreach (var n in _added)
        {
            hasher.Write(n.Id).Write(n.NameDe).Write(n.NameEn).Write(n.DecayPerDay).Write(n.SourceTypeId).Write(n.Actions.Count);
            foreach (var action in n.Actions)
            {
                hasher.Write(action);
            }
        }
    }

    /// <summary>The state to save.</summary>
    public NeedRegisterState Capture() => new([.. _added]);

    /// <summary>
    /// Restores new needs (null = a save game from before the register: none). Each must pass the same checks
    /// as when it was added, otherwise the load fails cleanly (as for skills, review #96).
    /// </summary>
    public void Restore(NeedRegisterState? state)
    {
        _added.Clear();
        foreach (var need in state?.Needs ?? [])
        {
            var reason = string.IsNullOrEmpty(need.SourceTypeId)
                ? "no source invention"
                : Check(new NewNeed { Id = need.Id, NameDe = need.NameDe, NameEn = need.NameEn, DecayPerDay = need.DecayPerDay, Actions = [.. need.Actions] });
            if (reason is not null)
            {
                _added.Clear();
                throw new InvalidDataException($"Save game need '{need.Id}': {reason}.");
            }

            _added.Add(need);
        }
    }
}
