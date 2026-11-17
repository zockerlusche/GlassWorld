using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>An inhabitant's body stores as saved (A1816): stamina and health, permille of full.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Stamina">Stamina, 0..1000.</param>
/// <param name="Health">Health, 0..1000.</param>
public sealed record StoredBody(string Id, int Stamina, int Health);

/// <summary>
/// A1816 (D1203 v3 layer 2, E10, E13; A1799 D1236): the body's stores behind the switch <c>body.stores.enabled</c>. Stamina
/// falls while walking by the gait of the walk and by the load in the hand (weight over the carrying strength of the life
/// phase, squared) and comes back at rest and faster asleep - but only up to a cap that sinks with the sleep need and with
/// health (D1202: a tired or ill inhabitant is spent sooner). Health moves by the events of the data (a fall) and comes back
/// slowly. The carrying strength is a body measure of the life phase. All numbers are data; this first step only keeps the
/// stores - what they change (pace, a gate for hard work, lifting) comes with the decider (A1816 E1/E2). Deterministic:
/// inhabitants in id order, whole permille, rates spread over the hour without remainders. Not in the world without the
/// switch, so worlds without it keep their hashes.
/// </summary>
public sealed class StoresSystem : IWorldSystem, IOptionalWorldState
{
    private const int Full = 1000;
    private readonly BodyStores _data;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ObjectCatalog _catalog;
    private readonly GaitTable _gaits;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, (int Stamina, int Health)> _stores = new(StringComparer.Ordinal);

    /// <summary>Creates the system; it listens to the inhabitants' events for the health changes of the data.</summary>
    public StoresSystem(BodyStores data, InhabitantsSystem inhabitants, WorldObjects objects, ObjectCatalog catalog, GaitTable gaits, int ticksPerHour)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gaits = gaits ?? throw new ArgumentNullException(nameof(gaits));
        _ticksPerHour = Math.Max(1, ticksPerHour);
        _inhabitants.Reported += Observe;
    }

    /// <summary>
    /// The content type a runtime type comes from (a process variant: its root; set by the host from the processes). A type
    /// outside the catalog without a root (an invention) has no weight here (review GW4 #589).
    /// </summary>
    public Func<string, string?>? RootOf { get; init; }

    /// <inheritdoc/>
    public string Name => "stores";

    /// <inheritdoc/>
    public bool HasState => _stores.Count > 0;

    /// <summary>Stamina of an inhabitant, 0..1 (full while it has no entry yet).</summary>
    public double Stamina(string inhabitantId) => (_stores.TryGetValue(inhabitantId, out var s) ? s.Stamina : Full) / (double)Full;

    /// <summary>Health of an inhabitant, 0..1 (full while it has no entry yet).</summary>
    public double Health(string inhabitantId) => (_stores.TryGetValue(inhabitantId, out var s) ? s.Health : Full) / (double)Full;

    /// <summary>The carrying strength of an inhabitant in kg: the data's value for its life phase, else the default.</summary>
    public double CarryKg(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.LifeStage is { } stage && _data.CarryKg.TryGetValue(stage, out var kg) ? kg : _data.DefaultCarryKg;
    }

    /// <summary>
    /// How heavy what the inhabitant holds is for it: the thing's weight over its carrying strength (0 with empty hands or a
    /// thing without a weight). Above 1 it could not lift it (E10) - what follows from that is the decider's.
    /// </summary>
    public double LoadShare(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (inhabitant.Held is not { } held || !_objects.TryGet(held, out var thing) || WeightOf(thing.TypeId) is not { } kg)
        {
            return 0;
        }

        var carry = CarryKg(inhabitant);
        return carry > 0 ? kg / carry : 0;
    }

    /// <summary>The weight of a type in kg: its catalog value, for a process variant the value of its root; null = none known.</summary>
    private double? WeightOf(string typeId)
    {
        var source = _catalog.Contains(typeId) ? typeId : RootOf?.Invoke(typeId);
        return source is not null && _catalog.Contains(source) ? _catalog.Params(source).WeightKg : null;
    }

    /// <summary>The highest stamina the inhabitant can recover to now, permille (D1202: sinks with the sleep need and health).</summary>
    public int Cap(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var rule = _data.Stamina;
        var sleep = (int)Math.Round(Math.Clamp(inhabitant.Needs.GetValueOrDefault(rule.SleepNeed, 1.0), 0, 1) * Full);
        var health = _stores.TryGetValue(inhabitant.Id, out var s) ? s.Health : Full;
        return Math.Clamp(Full - (rule.CapSleepPermille * (Full - sleep) / Full) - (rule.CapHealthPermille * (Full - health) / Full), 0, Full);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var rule = _data.Stamina;
        foreach (var inhabitant in _inhabitants.Ordered)
        {
            var (stamina, health) = _stores.TryGetValue(inhabitant.Id, out var s) ? s : (Full, Full);

            // Walking: on its way and getting on (A1751: standing stalled on a walk is no walking, review GW4 #589).
            if (inhabitant.Activity is { RemainingTicks: null, StallSinceTick: null } walk)
            {
                // Walking: the gait of the walk, and what is carried (load share squared, at most the full rate).
                var gait = _gaits.GaitFor(inhabitant, walk.Action);
                var load = Math.Min(1.0, LoadShare(inhabitant));
                var perHour = (rule.GaitPerHour.TryGetValue(gait, out var gaitRate) ? gaitRate : 0) + (long)Math.Round(rule.LoadPerHour * load * load);
                stamina = Math.Max(0, stamina - Step(world.Tick, perHour));
            }
            else
            {
                // D1202: it recovers only up to the cap; above it (it grew sleepy while fresh) it keeps what it has.
                var perHour = _inhabitants.IsAsleep(inhabitant) ? rule.RecoverSleepPerHour : rule.RecoverRestPerHour;
                var cap = Cap(inhabitant);
                if (stamina < cap)
                {
                    stamina = Math.Min(cap, stamina + Step(world.Tick, perHour));
                }
            }

            if (health < Full && _data.Health.RecoverPerHour > 0)
            {
                health = Math.Min(Full, health + Step(world.Tick, _data.Health.RecoverPerHour));
            }

            if (stamina == Full && health == Full)
            {
                _stores.Remove(inhabitant.Id);
            }
            else
            {
                _stores[inhabitant.Id] = (stamina, health);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_stores.Count);
        foreach (var (id, (stamina, health)) in _stores)
        {
            hasher.Write(id).Write(stamina).Write(health);
        }
    }

    /// <summary>The stores that are not full, for a save game.</summary>
    public IReadOnlyList<StoredBody> Capture() => [.. _stores.Select(s => new StoredBody(s.Key, s.Value.Stamina, s.Value.Health))];

    /// <summary>Restores the stores of a save game (absent = all full).</summary>
    public void Restore(IReadOnlyList<StoredBody>? stores)
    {
        _stores.Clear();
        foreach (var s in stores ?? [])
        {
            var stamina = Math.Clamp(s.Stamina, 0, Full);
            var health = Math.Clamp(s.Health, 0, Full);
            if (stamina < Full || health < Full)
            {
                _stores[s.Id] = (stamina, health);
            }
        }
    }

    private void Observe(WorldEvent e)
    {
        if (_data.Health.Events is { } events && events.TryGetValue(e.Kind, out var change) && change != 0 && e.InhabitantId.Length > 0)
        {
            // At once, in the order the events come (deterministic): nothing waits in a list a save would lose.
            var (stamina, health) = _stores.TryGetValue(e.InhabitantId, out var s) ? s : (Full, Full);
            _stores[e.InhabitantId] = (stamina, Math.Clamp(health + change, 0, Full));
        }
    }

    /// <summary>The part of a rate per hour that falls on this tick, so a whole hour adds up to it exactly.</summary>
    private int Step(long tick, long perHour)
    {
        var inHour = tick % _ticksPerHour;
        return (int)((perHour * (inHour + 1) / _ticksPerHour) - (perHour * inHour / _ticksPerHour));
    }
}
