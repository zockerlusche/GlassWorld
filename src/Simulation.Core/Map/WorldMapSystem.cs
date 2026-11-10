using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// One placed instance of a regrowing spawn rule (minimal regrowth before B5): the object now standing
/// there (or the one used up), its cell and since when it is gone.
/// </summary>
/// <param name="ObjectId">Object currently at this place, or the last one that was used up.</param>
/// <param name="TypeId">Catalogue type that grows back.</param>
/// <param name="X">Cell x.</param>
/// <param name="Z">Cell z.</param>
/// <param name="GoneSinceTick">Tick at which the object was first missed; null while it exists.</param>
public sealed record Occurrence(string ObjectId, string TypeId, int X, int Z, long? GoneSinceTick);

/// <summary>
/// The generated start island as a world system (B2 R1): the base grid never changes after generation,
/// so the system does nothing per tick and writes the island data into the state hash. It is rebuilt
/// from seed and preset when a save game is loaded (the generator is deterministic).
/// The tides (B2-5 R15) lie over it as a run-time layer: they change the walk costs of the shallow sea
/// with the tick, never the walkable areas (<see cref="Connected"/> reads the base grid).
/// </summary>
public sealed class WorldMapSystem : IWorldSystem, IRouteFinder
{
    private readonly List<Occurrence> _occurrences = [];
    private readonly IReadOnlyDictionary<string, long> _regrowTicks;
    private readonly WorldObjects? _objects;

    /// <summary>Creates the system around a generated island (no regrowth).</summary>
    public WorldMapSystem(GeneratedIsland island, int cellSizeCm)
        : this(island, cellSizeCm, null, new Dictionary<string, long>(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Creates the system around a generated island; objects of the types in
    /// <paramref name="regrowTicks"/> grow back at their place that many ticks after they were used up.
    /// </summary>
    public WorldMapSystem(GeneratedIsland island, int cellSizeCm, WorldObjects? objects, IReadOnlyDictionary<string, long> regrowTicks)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(regrowTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSizeCm, 1);
        Island = island;
        CellSizeCm = cellSizeCm;
        IslandHash = island.Hash();
        _objects = objects;
        _regrowTicks = regrowTicks;
    }

    /// <summary>Places that grow back, in the order they were placed.</summary>
    public IReadOnlyList<Occurrence> Occurrences => _occurrences;

    /// <summary>Remembers a placed object if its type grows back.</summary>
    public void Track(WorldObject obj, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (_regrowTicks.ContainsKey(obj.TypeId))
        {
            _occurrences.Add(new Occurrence(obj.Id, obj.TypeId, x, z, null));
        }
    }

    /// <summary>Restores the places that grow back (save games).</summary>
    public void Restore(IEnumerable<Occurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        _occurrences.Clear();
        _occurrences.AddRange(occurrences);
    }

    /// <summary>Hash of the generated data, computed once (the grid never changes).</summary>
    public string IslandHash { get; }

    /// <inheritdoc/>
    public string Name => "worldmap";

    /// <summary>The generated island (map, objects, starts, check).</summary>
    public GeneratedIsland Island { get; }

    /// <summary>The base grid.</summary>
    public WorldMap Map => Island.Map;

    /// <summary>Edge length of a cell in centimetres (from the preset).</summary>
    public int CellSizeCm { get; }

    /// <summary>Most nodes one path search may expand (preset pathfinding, B2 R10).</summary>
    public int NodeBudget { get; init; } = 20000;

    /// <summary>
    /// The tides of the island (B2-5 R15), or null on islands built without them (tests). The level follows
    /// from the tick alone, so nothing of it is saved or hashed.
    /// </summary>
    public Tides? Tides { get; init; }

    /// <summary>
    /// Walk costs that follow the tide; null without tides (amplitude 0): then routes and speed read the base
    /// grid exactly as before.
    /// </summary>
    public TideWalkLayer? TideWalk { get; init; }

    /// <summary>The world tick the routes are asked at (set by the host); without it the tide layer is not used.</summary>
    public Func<long>? Clock { get; init; }

    /// <summary>The tide at a tick, or null without tides data.</summary>
    public TideReading? TideAt(long tick) => Tides?.Read(tick);

    /// <summary>Walk costs with the tide at the current tick, or null where the base grid counts.</summary>
    private int[]? TideCosts() => TideWalk is { } layer && Clock is { } clock ? layer.CostsAt(clock()) : null;

    private int? _minWalkCost;
    private PathFinder.Scratch? _scratch;

    /// <summary>The cell that contains a world position, or null outside the map.</summary>
    public (int X, int Z)? CellOf(Position position)
    {
        var x = (int)Math.Floor(position.X * 100 / CellSizeCm);
        var z = (int)Math.Floor(position.Z * 100 / CellSizeCm);
        return Map.Contains(x, z) ? (x, z) : null;
    }

    /// <inheritdoc/>
    public Route Find(Position start, Position destination)
    {
        if (CellOf(start) is not { } from || CellOf(destination) is not { } goal)
        {
            return new Route(PathStatus.Unreachable, []);
        }

        _minWalkCost ??= Enumerable.Range(0, Map.WidthCells * Map.DepthCells)
            .Select(i => Map.Cell(i % Map.WidthCells, i / Map.WidthCells).WalkCost)
            .Where(c => c != MapCell.Blocked)
            .DefaultIfEmpty(1000)
            .Min();
        _scratch ??= new PathFinder.Scratch(Map.WidthCells * Map.DepthCells);

        // With tides the shallow sea costs what the water level makes of it (never blocked, see TideWalkLayer).
        var costs = TideCosts();
        var path = PathFinder.Find(Map, from, goal, NodeBudget, costs is null ? _minWalkCost.Value : Math.Min(_minWalkCost.Value, TideWalk!.MinCost), _scratch, costs);
        if (path.Status != PathStatus.Found)
        {
            return new Route(path.Status, []);
        }

        var points = path.Cells.Take(Math.Max(0, path.Cells.Count - 1)).Select(c => CentreOf(c.X, c.Z)).ToList();
        points.Add(destination);
        return new Route(PathStatus.Found, points);
    }

    private int[]? _areas;

    /// <inheritdoc/>
    public bool Connected(Position a, Position b)
    {
        if (CellOf(a) is not { } from || CellOf(b) is not { } to)
        {
            return false;
        }

        _areas ??= PathFinder.WalkableAreas(Map);
        var w = Map.WidthCells;
        var area = _areas[(from.Z * w) + from.X];
        return area != 0 && area == _areas[(to.Z * w) + to.X];
    }

    /// <inheritdoc/>
    public double SpeedFactor(Position at)
    {
        if (CellOf(at) is not { } cell)
        {
            return 1.0;
        }

        var cost = TideCosts() is { } costs ? costs[(cell.Z * Map.WidthCells) + cell.X] : Map.Cell(cell.X, cell.Z).WalkCost;
        return cost == MapCell.Blocked ? 0.25 : Math.Min(1.0, 1000.0 / Math.Max(1, cost));
    }

    /// <summary>World position (metres, origin at the map corner) of the centre of cell (x, z).</summary>
    public Position CentreOf(int x, int z) => new((x + 0.5) * CellSizeCm / 100.0, (z + 0.5) * CellSizeCm / 100.0);

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_objects is null)
        {
            return;
        }

        for (var i = 0; i < _occurrences.Count; i++)
        {
            var place = _occurrences[i];
            if (place.GoneSinceTick is null)
            {
                if (!_objects.TryGet(place.ObjectId, out _))
                {
                    _occurrences[i] = place with { GoneSinceTick = world.Tick };
                }

                continue;
            }

            if (world.Tick - place.GoneSinceTick.Value >= _regrowTicks[place.TypeId])
            {
                var grown = _objects.Spawn(place.TypeId, CentreOf(place.X, place.Z));
                _occurrences[i] = place with { ObjectId = grown.Id, GoneSinceTick = null };
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Island.PresetId).Write(IslandHash).Write(_occurrences.Count);
        foreach (var place in _occurrences)
        {
            hasher.Write(place.ObjectId).Write(place.TypeId).Write(place.X).Write(place.Z).Write(place.GoneSinceTick ?? -1);
        }
    }
}
