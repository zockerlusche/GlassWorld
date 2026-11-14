using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Nature;
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
/// fire by name. Deterministic: objects in id order, whole ticks.
/// B5-10 (R45-R47, D1084; data <c>content/fire/settings.json</c>): a thing stays wet for the drying hours of the
/// air temperature after the last rain on the island (a pure function of the nature takt, no state); wet things
/// neither catch fire nor feed one. A warm burning thing takes hold of dry things with the fuel property near it
/// by a chance that falls off with the distance and grows with the wind (stream <c>fire</c>), capped per check
/// and on the island; a fire that burnt down leaves its remains (ash). Nothing harms an inhabitant (B2472).
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

    /// <summary>Drying, spreading and remains (B5-10); null = fires only burn down or go out (as before).</summary>
    public FireSettings? Settings { get; set; }

    /// <summary>The nature takt: how long the island has been dry (R51); null = things are wet only while it rains.</summary>
    public NatureSystem? Nature { get; init; }

    /// <summary>Wind 0..1 at a tick (the weather); null = calm.</summary>
    public Func<long, double>? WindAt { get; init; }

    /// <summary>Told when a thing caught fire: the thing (already gone from the world) and the fire it became.</summary>
    public Action<World, WorldObject, WorldObject>? Caught { get; set; }

    /// <summary>Event: a thing caught fire from a fire near it (B5 R46; vocabulary event).</summary>
    public const string CaughtFire = "caught_fire";

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
                if (burnt && Settings?.Remains?.FirstOrDefault(r => string.Equals(r.Burnt, thing.TypeId, StringComparison.Ordinal)) is { } remains)
                {
                    _objects.Spawn(remains.Left, thing.Position);
                }

                Ended?.Invoke(world, thing, burnt ? BurnedOut : PutOut);
            }
        }

        foreach (var id in done ?? [])
        {
            _until.Remove(id);
        }

        if (Settings?.Spread is { } spread)
        {
            TakeHold(world, spread);
        }
    }

    /// <summary>
    /// Whether a thing at a place is wet (B5 R45): while it rains there, and for the drying hours of the air
    /// temperature after the last rain on the island. Without drying data or weather nothing is wet.
    /// </summary>
    public bool IsWet(World world, Position at)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (Settings is not { } data || AirAt?.Invoke(world, at) is not { } air)
        {
            return false;
        }

        if (air.Precipitation)
        {
            return true;
        }

        if (Nature?.InfluencesSince(world.Tick, world.Tick).DryTakts is not { } dryTakts)
        {
            return false;
        }

        var band = data.Drying.FirstOrDefault(b => b.BelowTenthC is not { } below || air.TemperatureTenthC < below);
        return band is not null && dryTakts * Nature.TaktTicks < (long)band.Hours * _ticksPerHour;
    }

    /// <summary>
    /// B5 R47: whether a sensation of the fire data is felt at a place - at least min_burning burning things
    /// within its radius (a campfire alone is not a big fire). Unknown sensations: false.
    /// </summary>
    public bool Feels(Position at, string sensation)
    {
        if (Settings?.Felt is not { Count: > 0 } felt || felt.FirstOrDefault(f => string.Equals(f.Sensation, sensation, StringComparison.Ordinal)) is not { } rule)
        {
            return false;
        }

        var radius = rule.RadiusCm / 100.0;
        var count = 0;
        foreach (var id in _until.Keys)
        {
            if (_objects.TryGet(id, out var burning))
            {
                var dx = burning.Position.X - at.X;
                var dz = burning.Position.Z - at.Z;
                if ((dx * dx) + (dz * dz) <= radius * radius && ++count >= rule.MinBurning)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a thing feeds a fire and can catch one: its type has the fuel property (TRUE properties).</summary>
    private bool IsFuel(string typeId) =>
        Settings is { } data && _objects.Catalog.Contains(typeId) && _objects.Catalog.TrueProperties(typeId).Contains(data.FuelProperty);

    /// <summary>
    /// B5 R46: each warm burning thing (heat radius) may set dry fuel near it alight; the chance falls off
    /// linearly to nothing at the radius and grows with the wind; at most max_new per check, none while
    /// max_burning things burn. Candidates in id order, one draw each from the stream <c>fire</c>.
    /// </summary>
    private void TakeHold(World world, FireSpread spread)
    {
        var every = Math.Max(1L, (long)spread.EveryMinutes * _ticksPerHour / 60);
        if (world.Tick % every != 0 || _until.Count == 0 || _until.Count >= spread.MaxBurning || spread.MaxNew <= 0)
        {
            return;
        }

        var sources = new List<WorldObject>();
        foreach (var id in _until.Keys)
        {
            if (_objects.TryGet(id, out var burning) && ParamsOf(burning.TypeId) is { HeatRadiusCm: > 0 })
            {
                sources.Add(burning);
            }
        }

        if (sources.Count == 0)
        {
            return;
        }

        var radius = spread.RadiusCm / 100.0;
        var near = new List<WorldObject>();
        var closest = new SortedDictionary<string, (WorldObject Thing, double Distance)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            near.Clear();
            _objects.Within(source.Position, radius, near);
            foreach (var thing in near)
            {
                if (_until.ContainsKey(thing.Id) || !IsFuel(thing.TypeId)
                    || (spread.Spares is { Count: > 0 } spares && _objects.Catalog.TrueProperties(thing.TypeId).Overlaps(spares)))
                {
                    // Built things wait for the damage by quality (review GW1 #396, A1382 S2).
                    continue;
                }

                var dx = thing.Position.X - source.Position.X;
                var dz = thing.Position.Z - source.Position.Z;
                var distance = Math.Sqrt((dx * dx) + (dz * dz));
                if (distance < radius && (!closest.TryGetValue(thing.Id, out var known) || distance < known.Distance))
                {
                    closest[thing.Id] = (thing, distance);
                }
            }
        }

        if (closest.Count == 0)
        {
            return;
        }

        var windPermille = (long)Math.Round(Math.Clamp(WindAt?.Invoke(world.Tick) ?? 0, 0, 1) * 1000);
        var rng = world.Random.Get("fire");
        var lit = 0;
        foreach (var (thing, distance) in closest.Values)
        {
            if (lit >= spread.MaxNew || _until.Count >= spread.MaxBurning)
            {
                break;
            }

            if (IsWet(world, thing.Position))
            {
                continue;
            }

            var restCm = Math.Max(0L, spread.RadiusCm - (long)Math.Round(distance * 100));
            var chance = (long)spread.ChancePermille * restCm / spread.RadiusCm * (1000 + (windPermille * spread.WindPermille / 1000)) / 1000;
            if (rng.NextInt(0, 1000) >= chance)
            {
                continue;
            }

            _objects.Remove(thing.Id);
            var fire = _objects.Spawn(spread.Becomes, thing.Position);
            var minutes = ParamsOf(fire.TypeId)?.BurnMinutes ?? 0;
            _until[fire.Id] = world.Tick + Math.Max(1L, (long)minutes * _ticksPerHour / 60);
            lit++;
            Caught?.Invoke(world, thing, fire);
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

    /// <summary>The heat level a burning thing gives for processing (B2700): position of its type's heat_level in <paramref name="levels"/>, or null.</summary>
    public int? HeatLevelOf(WorldObject thing, IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(levels);
        if (!_until.ContainsKey(thing.Id) || ParamsOf(thing.TypeId)?.HeatLevel is not { } level)
        {
            return null;
        }

        for (var i = 0; i < levels.Count; i++)
        {
            if (string.Equals(levels[i], level, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The highest heat level of burning things within <paramref name="reachM"/> of a place (B2700), or null.</summary>
    public int? HeatLevelNear(Position at, double reachM, IReadOnlyList<string> levels)
    {
        int? best = null;
        foreach (var id in _until.Keys)
        {
            if (_objects.TryGet(id, out var thing) && HeatLevelOf(thing, levels) is { } level)
            {
                var dx = thing.Position.X - at.X;
                var dz = thing.Position.Z - at.Z;
                if ((dx * dx) + (dz * dz) <= reachM * reachM)
                {
                    best = Math.Max(best ?? level, level);
                }
            }
        }

        return best;
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
            || ParamsOf(fire.TypeId) is not { TakesFuel: true } fireParams || ParamsOf(fuel.TypeId)?.FuelMinutes is not { } minutes
            || IsWet(world, fuel.Position))
        {
            // B5 R45: wet fuel does not burn.
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
