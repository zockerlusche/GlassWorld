using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Player;

/// <summary>An open call for help: an inhabitant tried in vain and the needed thing is really missing.</summary>
/// <param name="InhabitantId">Who.</param>
/// <param name="Need">Which need (vocabulary id).</param>
/// <param name="Tick">When the call opened (it expires after the data's suggestion time).</param>
public sealed record HelpCall(string InhabitantId, string Need, long Tick);

/// <summary>The player part of the world state, as saved.</summary>
/// <param name="Favor">Current favour.</param>
/// <param name="Attempts">Futile attempts per "inhabitant|need".</param>
/// <param name="Open">Open calls for help.</param>
public sealed record PlayerState(double Favor, IReadOnlyList<KeyValuePair<string, int>> Attempts, IReadOnlyList<HelpCall> Open);

/// <summary>
/// The player's part of the world (B6 R8/R9, F5238, a world system):
/// <list type="bullet">
/// <item>Favour grows back slowly up to its maximum; interventions cost favour (all values from
/// content/player/favor.json).</item>
/// <item>Futile effort: when a search action (data) ends while a need is pressing and nothing in
/// sight seems to help (by the inhabitant's own beliefs), that is a futile attempt. After enough attempts the core
/// checks against the truth whether a helping thing is really missing nearby; only then a call for
/// help opens (event <see cref="HelpNeeded"/>). Otherwise the inhabitant is to search on.</item>
/// <item>Gifts: the player places an object (command <see cref="PlaceGift"/>); it belongs to nobody,
/// so any inhabitant may perceive and take it.</item>
/// </list>
/// Rules and thresholds come from content/player/gift_rules.json; the code names no need or object.
/// </summary>
public sealed class PlayerSystem : IWorldSystem
{
    /// <summary>Event: a call for help opened (InhabitantId = who, Detail = need).</summary>
    public const string HelpNeeded = "help_needed";

    /// <summary>Event: the player placed a gift (TargetId = the new object, Detail = its type).</summary>
    public const string GiftPlaced = "gift_placed";

    /// <summary>Command kind: place a gift (TypeId, X, Z).</summary>
    public const string PlaceGift = "place_gift";

    /// <summary>Reason: not enough favour.</summary>
    public const string ReasonNoFavor = "msg.command.no_favor";

    /// <summary>Reason: the type is not in the object catalog.</summary>
    public const string ReasonUnknownType = "msg.command.unknown_type";

    /// <summary>Reason: too close to an inhabitant.</summary>
    public const string ReasonTooClose = "msg.command.too_close";

    /// <summary>Reason: no position given.</summary>
    public const string ReasonNoPosition = "msg.command.no_position";

    private const string GiftIntervention = "gift";

    private readonly GiftRules _rules;
    private readonly FavorSettings _favor;
    private readonly UtilityWeights _weights;
    private readonly UtilityBrain _brain;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly List<HelpCall> _open = [];

    /// <summary>Creates the system; favour starts at the middle start option until the world sets it.</summary>
    public PlayerSystem(GiftRules rules, FavorSettings favor, UtilityWeights weights, UtilityBrain brain, InhabitantsSystem inhabitants, WorldObjects objects, int ticksPerHour)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _favor = favor ?? throw new ArgumentNullException(nameof(favor));
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _ticksPerHour = ticksPerHour;
        var options = favor.StartOptions.ToList();
        Favor = options.Count == 0 ? favor.Maximum : options[options.Count / 2];
    }

    /// <inheritdoc/>
    public string Name => "player";

    /// <summary>Current favour (0..maximum).</summary>
    public double Favor { get; private set; }

    /// <summary>Open calls for help, oldest first.</summary>
    public IReadOnlyList<HelpCall> Open => _open;

    /// <summary>Favour cost of an intervention (0 if the data has none).</summary>
    public double CostOf(string intervention) => _favor.Costs.FirstOrDefault(c => c.Intervention == intervention)?.Cost1 ?? 0;

    /// <summary>Sets the start favour of a new world (one of the start options of the data).</summary>
    public void SetStartFavor(int favor)
    {
        if (!_favor.StartOptions.Contains(favor))
        {
            throw new ArgumentOutOfRangeException(nameof(favor), favor, "not a start option of favor.json");
        }

        Favor = favor;
    }

    /// <summary>
    /// Checks a gift command without changing anything (the UI shows the reason). Walkable ground is not
    /// checked yet: the island terrain query comes with B2 (GW1 review #67 S3).
    /// </summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.X is not { } x || command.Z is not { } z)
        {
            return new CommandResult(false, ReasonNoPosition);
        }

        if (command.TypeId is null || !_objects.Catalog.Contains(command.TypeId))
        {
            return new CommandResult(false, ReasonUnknownType);
        }

        var min = _rules.MinDistanceToInhabitantM;
        if (_inhabitants.All.Any(i => DistanceSquared(i.Position, new Position(x, z)) < min * min))
        {
            return new CommandResult(false, ReasonTooClose);
        }

        return Favor + 1e-9 < CostOf(GiftIntervention) ? new CommandResult(false, ReasonNoFavor) : new CommandResult(true, null);
    }

    /// <summary>Applies a checked gift command (called by the decisions system at the tick it takes effect).</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        if (!Check(command).Accepted)
        {
            // Conditions can change between submitting and the tick (favour spent, inhabitant moved).
            return;
        }

        Favor -= CostOf(GiftIntervention);
        var gift = _objects.Spawn(command.TypeId!, new Position(command.X!.Value, command.Z!.Value));
        _inhabitants.Report(new WorldEvent(GiftPlaced, world.Tick, string.Empty, null, gift.Id) { Detail = gift.TypeId });
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Favor = Math.Min(_favor.Maximum, Favor + (_favor.RegrowPerHour / _ticksPerHour));
        var expiry = (long)(_rules.SuggestionExpiryHours * _ticksPerHour);
        _open.RemoveAll(c => world.Tick - c.Tick >= expiry);

        // Only searching counts as effort (search_actions of the data), not sleeping or resting.
        var finished = _inhabitants.Events
            .Where(e => e.Tick == world.Tick && e.Kind == InhabitantsSystem.ActionFinished && e.Action is not null && _rules.SearchActions.Contains(e.Action))
            .Select(e => e.InhabitantId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var inhabitant in _inhabitants.All)
        {
            foreach (var need in PressingCandidates())
            {
                var key = inhabitant.Id + "|" + need;
                var value = inhabitant.Needs.GetValueOrDefault(need, 1.0);
                if (value >= _rules.UrgentBelow)
                {
                    // Satisfied again: effort and the call are over.
                    _attempts.Remove(key);
                    _open.RemoveAll(c => c.InhabitantId == inhabitant.Id && c.Need == need);
                    continue;
                }

                if (!finished.Contains(inhabitant.Id) || SeemsHelpful(inhabitant, need))
                {
                    continue;
                }

                var attempts = _attempts.GetValueOrDefault(key) + 1;
                _attempts[key] = attempts;
                if (attempts >= _rules.FutileAttemptsThreshold
                    && _open.Count < _rules.MaxOpenSuggestions
                    && !_open.Any(c => c.InhabitantId == inhabitant.Id && c.Need == need)
                    && ReallyMissing(inhabitant, need))
                {
                    _open.Add(new HelpCall(inhabitant.Id, need, world.Tick));
                    _inhabitants.Report(new WorldEvent(HelpNeeded, world.Tick, inhabitant.Id, null, null) { Detail = need });
                }
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Favor).Write(_attempts.Count);
        foreach (var (key, count) in _attempts)
        {
            hasher.Write(key).Write(count);
        }

        hasher.Write(_open.Count);
        foreach (var call in _open)
        {
            hasher.Write(call.InhabitantId).Write(call.Need).Write(call.Tick);
        }
    }

    /// <summary>The state to save.</summary>
    public PlayerState Capture() => new(Favor, [.. _attempts], [.. _open]);

    /// <summary>Restores a saved state (null = a save game from before the player system: defaults).</summary>
    public void Restore(PlayerState? state)
    {
        if (state is null)
        {
            return;
        }

        Favor = state.Favor;
        _attempts.Clear();
        foreach (var (key, count) in state.Attempts)
        {
            _attempts[key] = count;
        }

        _open.Clear();
        _open.AddRange(state.Open);
    }

    /// <summary>Needs that something in the world can satisfy (considerations with a target property).</summary>
    private IEnumerable<string> PressingCandidates() =>
        _weights.Considerations.Where(c => c.Need is not null && c.TargetProperty is not null).Select(c => c.Need!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>By its own beliefs: does the inhabitant see anything that might help with the need?</summary>
    private bool SeemsHelpful(Inhabitant inhabitant, string need)
    {
        var radius = _weights.PerceptionRadiusM;
        var seen = _objects.All.Where(o => DistanceSquared(inhabitant.Position, o.Position) <= radius * radius).ToList();
        return _weights.Considerations
            .Where(c => c.Need == need && c.TargetProperty is not null)
            .Any(c => seen.Any(o => _brain.BeliefFactor(inhabitant, o.TypeId, c.TargetProperty!) > 0));
    }

    /// <summary>The truth: is no object that really helps (target property, none to avoid) within the search distance?</summary>
    private bool ReallyMissing(Inhabitant inhabitant, string need)
    {
        var radius = _rules.MissingRadiusM;
        return !_weights.Considerations
            .Where(c => c.Need == need && c.TargetProperty is not null)
            .Any(c => _objects.All.Any(o =>
                DistanceSquared(inhabitant.Position, o.Position) <= radius * radius
                && _objects.Catalog.HasProperty(o.TypeId, c.TargetProperty!)
                && !c.AvoidProperties.Any(p => _objects.Catalog.HasProperty(o.TypeId, p))));
    }

    private static double DistanceSquared(Position a, Position b) => ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));
}
