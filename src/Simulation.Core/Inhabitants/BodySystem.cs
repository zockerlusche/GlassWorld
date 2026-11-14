using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>How wet an inhabitant is, as saved: permille of fully wet.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Permille">Wetness, 1..1000.</param>
public sealed record WetInhabitant(string Id, int Permille);

/// <summary>
/// B5-11 (spec Bogen 30 R29/R30, D1084; data <c>content/body/settings.json</c>): what the weather does to an
/// inhabitant's body beyond the air. Rain wets an inhabitant out in the open (not near a thing with the shelter
/// property); it dries by a table over the air temperature, faster in wind; being wet feels colder. The sun warms
/// under a clear sky where no tree's shade falls (shade_height_cm and crown_radius_cm toward the direction of the
/// sun table, without angle functions; derived, not state). No fear of water and no behaviour in the code: what an
/// inhabitant does about rain or sun stays with the decider (B2584). Deterministic: inhabitants in id order,
/// whole permille, rates spread over the hour without remainders.
/// </summary>
public sealed class BodySystem : IWorldSystem, IOptionalWorldState
{
    private readonly BodySettings _data;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly ObjectCatalog _content;
    private readonly int _ticksPerHour;
    private readonly SortedDictionary<string, int> _wet = new(StringComparer.Ordinal);
    private readonly long _tallestCm;
    private readonly long _widestCm;

    /// <summary>Creates the system.</summary>
    public BodySystem(BodySettings data, InhabitantsSystem inhabitants, WorldObjects objects, ObjectCatalog content, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(content);
        _data = data;
        _inhabitants = inhabitants;
        _objects = objects;
        _content = content;
        _ticksPerHour = ticksPerHour;

        // The tallest shade caster and the widest crown bound how far a shade can fall.
        var tallest = 0L;
        var widest = 0L;
        foreach (var typeId in content.TypeIds)
        {
            if (content.Params(typeId) is { ShadeHeightCm: { } height, CrownRadiusCm: { } crown })
            {
                tallest = Math.Max(tallest, height);
                widest = Math.Max(widest, crown);
            }
        }

        _tallestCm = tallest;
        _widestCm = widest;
    }

    /// <summary>The air at a place (precipitation wets, the temperature dries); null = no weather, nobody gets wet.</summary>
    public Func<World, Position, Air?>? AirAt { get; init; }

    /// <summary>Cloud cover 0..1000 now (less sun with clouds); null = clear sky.</summary>
    public Func<World, int>? CloudPermille { get; init; }

    /// <summary>Wind 0..1 at a tick (dries faster); null = calm.</summary>
    public Func<long, double>? WindAt { get; init; }

    /// <inheritdoc/>
    public string Name => "body";

    /// <inheritdoc/>
    public bool HasState => _wet.Count > 0;

    /// <summary>How wet an inhabitant is, in permille of fully wet.</summary>
    public int WetPermille(string inhabitantId) => _wet.GetValueOrDefault(inhabitantId);

    /// <summary>
    /// What the body adds to the air an inhabitant feels, in tenths of a degree: the sun's warmth where no shade
    /// falls (less with clouds), minus the cooling of being wet.
    /// </summary>
    public int FeltTenthC(World world, Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitant);
        var cool = (int)((long)_data.Wetness.CoolTenthC * WetPermille(inhabitant.Id) / 1000);
        return SunTenthC(world, inhabitant.Position) - cool;
    }

    /// <summary>The sun's warmth at a place now, in tenths of a degree: none at night, in shade or under full cloud.</summary>
    public int SunTenthC(World world, Position at)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (SunNow(world) is not { } sun || InShade(at, sun))
        {
            return 0;
        }

        var cloud = Math.Clamp(CloudPermille?.Invoke(world) ?? 0, 0, 1000);
        return (int)((long)_data.Sun.WarmTenthC * (1000 - cloud) / 1000);
    }

    /// <summary>Whether a tree's shade falls on a place now (R29); at night nothing is in the sun anyway.</summary>
    public bool InShade(World world, Position at)
    {
        ArgumentNullException.ThrowIfNull(world);
        return SunNow(world) is not { } sun || InShade(at, sun);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (AirAt is null)
        {
            return;
        }

        foreach (var inhabitant in _inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            if (AirAt(world, inhabitant.Position) is not { } air)
            {
                continue;
            }

            var wet = _wet.GetValueOrDefault(inhabitant.Id);
            if (air.Precipitation && !Sheltered(inhabitant.Position))
            {
                wet = Math.Min(1000, wet + Step(world.Tick, _data.Wetness.RainPerHour));
            }
            else if (wet > 0)
            {
                var band = _data.Wetness.Drying.FirstOrDefault(b => b.BelowTenthC is not { } below || air.TemperatureTenthC < below);
                var perHour = (long)(band?.PerHour ?? 0);
                var wind = (long)Math.Round(Math.Clamp(WindAt?.Invoke(world.Tick) ?? 0, 0, 1) * 1000);
                perHour += perHour * wind * _data.Wetness.WindDryingPermille / 1_000_000;
                wet = Math.Max(0, wet - Step(world.Tick, perHour));
            }

            if (wet > 0)
            {
                _wet[inhabitant.Id] = wet;
            }
            else
            {
                _wet.Remove(inhabitant.Id);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_wet.Count);
        foreach (var (id, permille) in _wet)
        {
            hasher.Write(id).Write(permille);
        }
    }

    /// <summary>The wet inhabitants for a save game.</summary>
    public IReadOnlyList<WetInhabitant> Capture() => [.. _wet.Select(w => new WetInhabitant(w.Key, w.Value))];

    /// <summary>Restores the wet inhabitants of a save game.</summary>
    public void Restore(IReadOnlyList<WetInhabitant>? wet)
    {
        _wet.Clear();
        foreach (var inhabitant in wet ?? [])
        {
            if (inhabitant.Permille > 0)
            {
                _wet[inhabitant.Id] = Math.Min(1000, inhabitant.Permille);
            }
        }
    }

    /// <summary>The part of a rate per hour that falls on this tick, so a whole hour adds up to it exactly.</summary>
    private int Step(long tick, long perHour)
    {
        var inHour = tick % _ticksPerHour;
        return (int)((perHour * (inHour + 1) / _ticksPerHour) - (perHour * inHour / _ticksPerHour));
    }

    /// <summary>Whether a thing with the shelter property is near (it keeps the rain off).</summary>
    private bool Sheltered(Position at)
    {
        var near = new List<WorldObject>();
        _objects.Within(at, _data.Wetness.ShelterRadiusCm / 100.0, near);
        return near.Any(o => _objects.Catalog.Contains(o.TypeId) && _objects.Catalog.TrueProperties(o.TypeId).Contains(_data.Wetness.ShelterProperty));
    }

    /// <summary>The sun position of the season at the hour (the latest entry at or before it), or null while the sun is down.</summary>
    private SunPosition? SunNow(World world)
    {
        var now = world.Now;
        SunPosition? found = null;
        foreach (var position in _data.Sun.Positions)
        {
            if (string.Equals(position.Season, now.SeasonId, StringComparison.Ordinal) && position.Hour <= now.Hour
                && (found is null || position.Hour >= found.Hour))
            {
                found = position;
            }
        }

        return found is { TowardXPermille: not null, TowardZPermille: not null } ? found : null;
    }

    /// <summary>
    /// Whether a place lies in a shade: under the crown of a shade caster or along its shadow, which reaches
    /// its height times the shadow length toward the shadow direction, as wide as its crown.
    /// </summary>
    private bool InShade(Position at, SunPosition sun)
    {
        // The farthest a shade falls now: the tallest caster at this sun's shadow length, plus the widest crown.
        var reachM = ((_tallestCm * sun.ShadowPermille / 1000) + _widestCm) / 100.0;
        if (reachM <= 0)
        {
            return false;
        }

        var near = new List<WorldObject>();
        _objects.Within(at, reachM, near);
        foreach (var thing in near)
        {
            if (!_content.Contains(thing.TypeId) || _content.Params(thing.TypeId) is not { ShadeHeightCm: { } height, CrownRadiusCm: { } crown })
            {
                continue;
            }

            // The shadow runs from the thing to its far end; a place within the crown of that line is in shade.
            var lengthM = (long)height * sun.ShadowPermille / 1000 / 100.0;
            var endX = thing.Position.X + (lengthM * sun.TowardXPermille!.Value / 1000.0);
            var endZ = thing.Position.Z + (lengthM * sun.TowardZPermille!.Value / 1000.0);
            if (DistanceToSegment(at, thing.Position, new Position(endX, endZ)) <= crown / 100.0)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(Position p, Position a, Position b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var length = (dx * dx) + (dz * dz);
        var t = length <= 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Z - a.Z) * dz)) / length, 0, 1);
        var x = a.X + (t * dx) - p.X;
        var z = a.Z + (t * dz) - p.Z;
        return Math.Sqrt((x * x) + (z * z));
    }
}
