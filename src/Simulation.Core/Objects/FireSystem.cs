using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>A burning thing as saved: it goes out at <paramref name="Until"/> unless rain puts it out first.</summary>
/// <param name="Id">Object id.</param>
/// <param name="Until">Tick at which it has burnt down.</param>
public sealed record BurningThing(string Id, long Until);

/// <summary>
/// B2687 K3 (spec Bogen 30 R7, A1379 part): fire as a thing of the world. Every thing whose type has
/// <c>params.burn_minutes</c> (fire, a spark) burns from the tick it is first seen in the world and is gone
/// when the time is up; precipitation where it lies puts it out at once. A type with
/// <c>params.heat_tenth_c</c>/<c>heat_radius_cm</c> warms the air around it, falling off linearly to nothing
/// at the radius (R4: what the inhabitants feel). All numbers come from the type data; the code knows no
/// fire by name. Deterministic: objects in id order, whole ticks, no randomness.
/// </summary>
public sealed class FireSystem : IWorldSystem, IOptionalWorldState
{
    private readonly ObjectCatalog _content;
    private readonly WorldObjects _objects;
    private readonly Func<string, string?> _rootOf;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectParams?> _params = new(StringComparer.Ordinal);

    /// <summary>Creates the system.</summary>
    /// <param name="content">The content catalog (type params).</param>
    /// <param name="objects">The world's things.</param>
    /// <param name="rootOf">The type a changed thing was made from (K2 variants), or null; its params count.</param>
    /// <param name="ticksPerHour">Ticks per game hour.</param>
    public FireSystem(ObjectCatalog content, WorldObjects objects, Func<string, string?> rootOf, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(rootOf);
        _content = content;
        _objects = objects;
        _rootOf = rootOf;
        _ticksPerHour = ticksPerHour;
    }

    /// <summary>Event: a fire burnt down (A1379; vocabulary event).</summary>
    public const string BurnedOut = "burned_out";

    /// <summary>Event: rain put a fire out (A1379; vocabulary event).</summary>
    public const string PutOut = "put_out";

    /// <summary>Told when a burning thing ends: the thing (already gone from the world) and how (burnt down / put out).</summary>
    public Action<World, WorldObject, string>? Ended { get; set; }

    /// <summary>The air at a place (precipitation puts fires out); null = no weather, fires only burn down.</summary>
    public Func<World, Position, Air?>? AirAt { get; init; }

    /// <inheritdoc/>
    public string Name => "fire";

    /// <inheritdoc/>
    public bool HasState => _until.Count > 0;

    /// <summary>The burning things in id order with the tick they burn down.</summary>
    public IEnumerable<KeyValuePair<string, long>> Burning => _until;

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // New burning things start now (made by combining, or there from the start).
        foreach (var ofType in _objects.ByType)
        {
            if (ParamsOf(ofType.TypeId)?.BurnMinutes is not { } minutes || ofType.Objects.Count == 0)
            {
                continue;
            }

            var ticks = Math.Max(1L, (long)minutes * _ticksPerHour / 60);
            foreach (var thing in ofType.Objects)
            {
                _until.TryAdd(thing.Id, world.Tick + ticks);
            }
        }

        if (_until.Count == 0)
        {
            return;
        }

        List<string>? done = null;
        foreach (var (id, until) in _until)
        {
            if (!_objects.TryGet(id, out var thing))
            {
                (done ??= []).Add(id);
                continue;
            }

            var burnt = world.Tick >= until;
            if (burnt || AirAt?.Invoke(world, thing.Position) is { Precipitation: true })
            {
                _objects.Remove(id);
                (done ??= []).Add(id);
                Ended?.Invoke(world, thing, burnt ? BurnedOut : PutOut);
            }
        }

        foreach (var id in done ?? [])
        {
            _until.Remove(id);
        }
    }

    /// <summary>
    /// The warmth the things around a place add to the air there, in tenths of a degree: each heat source
    /// gives its <c>heat_tenth_c</c> at its own place, falling off linearly to nothing at <c>heat_radius_cm</c>.
    /// </summary>
    public int HeatAt(Position at)
    {
        var total = 0.0;
        foreach (var ofType in _objects.ByType)
        {
            if (ofType.Objects.Count == 0 || ParamsOf(ofType.TypeId) is not { HeatTenthC: { } heat, HeatRadiusCm: { } radiusCm } || radiusCm <= 0)
            {
                continue;
            }

            var radius = radiusCm / 100.0;
            foreach (var thing in ofType.Objects)
            {
                var dx = thing.Position.X - at.X;
                var dz = thing.Position.Z - at.Z;
                var distance = Math.Sqrt((dx * dx) + (dz * dz));
                if (distance < radius)
                {
                    total += heat * (1.0 - (distance / radius));
                }
            }
        }

        return (int)Math.Round(total, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether the thing is burning now and its type takes fuel (A1379).</summary>
    public bool TakesFuel(WorldObject thing)
    {
        ArgumentNullException.ThrowIfNull(thing);
        return _until.ContainsKey(thing.Id) && ParamsOf(thing.TypeId)?.TakesFuel == true;
    }

    /// <summary>
    /// Fuel put into a burning thing (A1379): when the fire takes fuel and the partner is fuel, the partner is
    /// used up and the fire burns its fuel_minutes longer, at most burn_max_minutes from now. Returns whether
    /// it burns longer now; anything else changes nothing (offered by reach, not by truth).
    /// </summary>
    public bool Feed(World world, string fireId, string fuelId)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!_until.TryGetValue(fireId, out var until) || !_objects.TryGet(fireId, out var fire) || !_objects.TryGet(fuelId, out var fuel)
            || ParamsOf(fire.TypeId) is not { TakesFuel: true } fireParams || ParamsOf(fuel.TypeId)?.FuelMinutes is not { } minutes)
        {
            return false;
        }

        var longer = until + ((long)minutes * _ticksPerHour / 60);
        if (fireParams.BurnMaxMinutes is { } most)
        {
            longer = Math.Min(longer, world.Tick + ((long)most * _ticksPerHour / 60));
        }

        if (longer <= until)
        {
            // Already at its most (review GW1 #355): the fuel stays where it is.
            return false;
        }

        _objects.Remove(fuelId);
        _until[fireId] = longer;
        return true;
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_until.Count);
        foreach (var (id, until) in _until)
        {
            hasher.Write(id).Write(until);
        }
    }

    /// <summary>The burning things for a save game.</summary>
    public IReadOnlyList<BurningThing> Capture() => [.. _until.Select(b => new BurningThing(b.Key, b.Value))];

    /// <summary>Restores the burning things of a save game.</summary>
    public void Restore(IReadOnlyList<BurningThing>? burning)
    {
        foreach (var thing in burning ?? [])
        {
            _until[thing.Id] = thing.Until;
        }
    }

    /// <summary>The params of a type, through the root of a changed thing; null for types without any (inventions).</summary>
    private ObjectParams? ParamsOf(string typeId)
    {
        if (_params.TryGetValue(typeId, out var known))
        {
            return known;
        }

        var source = _content.Contains(typeId) ? typeId : _rootOf(typeId);
        var found = source is not null && _content.Contains(source) ? _content.Params(source) : null;
        _params[typeId] = found;
        return found;
    }
}
