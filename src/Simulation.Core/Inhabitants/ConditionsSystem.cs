using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A complaint an inhabitant has, as saved (A1816, R23): which, since when, how much of its course is left (permille ticks).</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Condition">Condition id (vocabulary condition).</param>
/// <param name="SinceTick">When it began.</param>
/// <param name="LeftMilli">What is left of its course, in thousandths of a tick.</param>
public sealed record HeldCondition(string Id, string Condition, long SinceTick, long LeftMilli);

/// <summary>
/// A1816 step 2 (D1203 v3 layer 2 "Beschwerden"; B5 R23, A1381): complaints behind the switch body.stores.enabled. A complaint
/// begins by the data - after an event (eating something with a property), while a need stays low (cold and wet), as the
/// worsening of another, or from someone ill nearby - each with a chance of the data, rolled from the seed (no stream). It runs
/// its course of the data's hours, costs health per hour (the stores) and heals faster at rest or asleep and near a thing with
/// the data's healing property; the inhabitants find out what helps themselves (the truth is data). Never deadly by itself:
/// health is the stores' and a complaint ends when its course is run. The decider sees how it feels (FeltCondition), never
/// the cause. Deterministic: inhabitants and complaints in id order; hourly checks on the full hour.
/// </summary>
public sealed class ConditionsSystem : IWorldSystem, IOptionalWorldState
{
    private readonly IReadOnlyList<BodyCondition> _rules;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly StoresSystem _stores;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, SortedDictionary<string, (long Since, long LeftMilli)>> _held = new(StringComparer.Ordinal);
    private readonly List<WorldObject> _near = [];
    private readonly ulong _seed;

    /// <summary>Creates the system; it listens to the inhabitants' events for the data's event triggers.</summary>
    public ConditionsSystem(IReadOnlyList<BodyCondition> rules, InhabitantsSystem inhabitants, WorldObjects objects, StoresSystem stores, ulong seed, int ticksPerHour)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        _seed = seed;
        _ticksPerHour = Math.Max(1, ticksPerHour);
        _inhabitants.Reported += Observe;
    }

    /// <summary>Whether a type has a property (world truth; set by the host). Null = no thing has any.</summary>
    public Func<string, string, bool>? HasProperty { get; init; }

    /// <summary>Event kind: a complaint began (target = the condition).</summary>
    public const string ConditionBegan = "condition_began";

    /// <summary>Event kind: a complaint is over (target = the condition).</summary>
    public const string ConditionEnded = "condition_ended";

    /// <inheritdoc/>
    public string Name => "conditions";

    /// <inheritdoc/>
    public bool HasState => _held.Count > 0;

    /// <summary>The complaints of an inhabitant now, in id order.</summary>
    public IReadOnlyList<string> Of(string inhabitantId) => _held.TryGetValue(inhabitantId, out var held) ? [.. held.Keys] : [];

    /// <summary>How the inhabitant feels (contract FeltCondition): each complaint with the data's severity, never its cause.</summary>
    public IReadOnlyList<FeltCondition> Felt(string inhabitantId)
    {
        if (!_held.TryGetValue(inhabitantId, out var held))
        {
            return [];
        }

        return [.. held.Select(h => new FeltCondition { Condition = h.Key, Severity = Rule(h.Key)?.Severity ?? 0.5, SinceTick = h.Value.Since, BodyPart = null })];
    }

    /// <summary>Gives an inhabitant a complaint of the data now (also for tests); false if unknown or already had.</summary>
    public bool Begin(string inhabitantId, string condition, long tick)
    {
        if (Rule(condition) is not { } rule || (_held.TryGetValue(inhabitantId, out var held) && held.ContainsKey(condition)))
        {
            return false;
        }

        if (held is null)
        {
            held = new SortedDictionary<string, (long, long)>(StringComparer.Ordinal);
            _held[inhabitantId] = held;
        }

        held[condition] = (tick, (long)rule.Hours * _ticksPerHour * 1000);
        _inhabitants.Report(new WorldEvent(ConditionBegan, tick, inhabitantId, null, condition));
        return true;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var hour = world.Tick % _ticksPerHour == 0;
        foreach (var inhabitant in _inhabitants.Ordered)
        {
            if (hour)
            {
                Hourly(world, inhabitant);
            }

            if (!_held.TryGetValue(inhabitant.Id, out var held))
            {
                continue;
            }

            var resting = inhabitant.Activity is not { RemainingTicks: null } || _inhabitants.IsAsleep(inhabitant);
            foreach (var condition in held.Keys.ToList())
            {
                var rule = Rule(condition)!;
                var (since, left) = held[condition];
                var pace = 1000L;
                if (rule.Heal is { } heal)
                {
                    pace += resting ? heal.RestPermille : 0;
                    pace += heal.NearProperty is { } property && Near(inhabitant.Position, property, heal.NearRadiusCm ?? 0) ? heal.NearPermille ?? 0 : 0;
                }

                left -= pace;
                if (left <= 0)
                {
                    held.Remove(condition);
                    _inhabitants.Report(new WorldEvent(ConditionEnded, world.Tick, inhabitant.Id, null, condition));
                    continue;
                }

                held[condition] = (since, left);
                if (rule.HealthPerHour != 0)
                {
                    _stores.ChangeHealth(inhabitant.Id, Step(world.Tick, rule.HealthPerHour));
                }
            }

            if (held.Count == 0)
            {
                _held.Remove(inhabitant.Id);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_held.Count);
        foreach (var (id, held) in _held)
        {
            hasher.Write(id).Write(held.Count);
            foreach (var (condition, (since, left)) in held)
            {
                hasher.Write(condition).Write(since).Write(left);
            }
        }
    }

    /// <summary>The complaints for a save game.</summary>
    public IReadOnlyList<HeldCondition> Capture() =>
        [.. _held.SelectMany(h => h.Value.Select(c => new HeldCondition(h.Key, c.Key, c.Value.Since, c.Value.LeftMilli)))];

    /// <summary>Restores the complaints of a save game (absent = nobody has one; unknown conditions are dropped).</summary>
    public void Restore(IReadOnlyList<HeldCondition>? held)
    {
        _held.Clear();
        foreach (var c in held ?? [])
        {
            if (Rule(c.Condition) is null || c.LeftMilli <= 0)
            {
                continue;
            }

            if (!_held.TryGetValue(c.Id, out var of))
            {
                of = new SortedDictionary<string, (long, long)>(StringComparer.Ordinal);
                _held[c.Id] = of;
            }

            of[c.Condition] = (c.SinceTick, c.LeftMilli);
        }
    }

    private void Hourly(World world, Inhabitant inhabitant)
    {
        var hourNo = world.Tick / _ticksPerHour;
        foreach (var rule in _rules)
        {
            if (Has(inhabitant.Id, rule.Id))
            {
                continue;
            }

            // A need that stays low (cold and wet; per hour).
            foreach (var need in rule.OnNeed ?? [])
            {
                if (inhabitant.Needs.GetValueOrDefault(need.Need, 1.0) < need.Below && Roll(inhabitant.Id, rule.Id, "need/" + need.Need, hourNo) < need.ChancePerHourPermille / 1000.0)
                {
                    Begin(inhabitant.Id, rule.Id, world.Tick);
                    break;
                }
            }

            // The worsening of a complaint it has had for the data's hours.
            if (!Has(inhabitant.Id, rule.Id) && rule.GrowsFrom is { } from && _held.TryGetValue(inhabitant.Id, out var held)
                && held.TryGetValue(from.Condition, out var had) && world.Tick - had.Since >= (long)from.AfterHours * _ticksPerHour
                && Roll(inhabitant.Id, rule.Id, "from/" + from.Condition, hourNo) < from.ChancePerHourPermille / 1000.0)
            {
                Begin(inhabitant.Id, rule.Id, world.Tick);
            }

            // Someone near who has it (contagion; per hour and person).
            if (!Has(inhabitant.Id, rule.Id) && rule.Contagion is { } contagion)
            {
                var reach = contagion.RadiusCm / 100.0;
                foreach (var other in _inhabitants.Ordered)
                {
                    if (other.Id != inhabitant.Id && Has(other.Id, rule.Id) && Distance(other.Position, inhabitant.Position) <= reach
                        && Roll(inhabitant.Id, rule.Id, "from_person/" + other.Id, hourNo) < contagion.ChancePerHourPermille / 1000.0)
                    {
                        Begin(inhabitant.Id, rule.Id, world.Tick);
                        break;
                    }
                }
            }
        }
    }

    private void Observe(WorldEvent e)
    {
        if (e.Kind is ConditionBegan or ConditionEnded || e.InhabitantId.Length == 0)
        {
            return;
        }

        foreach (var rule in _rules)
        {
            foreach (var trigger in rule.OnEvents ?? [])
            {
                if (e.Kind != trigger.Event || (trigger.Action is { } action && e.Action != action))
                {
                    continue;
                }

                if (trigger.TargetProperty is { } property && (e.TargetType is not { } type || HasProperty?.Invoke(type, property) != true))
                {
                    continue;
                }

                if (!Has(e.InhabitantId, rule.Id) && Roll(e.InhabitantId, rule.Id, "event/" + e.Kind + "/" + e.TargetId, e.Tick) < trigger.ChancePermille / 1000.0)
                {
                    Begin(e.InhabitantId, rule.Id, e.Tick);
                }
            }
        }
    }

    private bool Has(string inhabitantId, string condition) => _held.TryGetValue(inhabitantId, out var held) && held.ContainsKey(condition);

    private BodyCondition? Rule(string condition) => _rules.FirstOrDefault(r => r.Id == condition);

    private bool Near(Position at, string property, int radiusCm)
    {
        _near.Clear();
        _objects.Within(at, radiusCm / 100.0, _near);
        return _near.Any(o => HasProperty?.Invoke(o.TypeId, property) == true);
    }

    /// <summary>A number in 0..1 from the world seed and what is rolled for (FNV-1a; no random stream used).</summary>
    private double Roll(string inhabitantId, string condition, string why, long when)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{_seed}|condition|{inhabitantId}|{condition}|{why}|{when}");
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return (hash >> 11) * (1.0 / (1UL << 53));
    }

    private static double Distance(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>The part of a rate per hour that falls on this tick, so a whole hour adds up to it exactly.</summary>
    private int Step(long tick, long perHour)
    {
        var inHour = tick % _ticksPerHour;
        return (int)((perHour * (inHour + 1) / _ticksPerHour) - (perHour * inHour / _ticksPerHour));
    }
}
