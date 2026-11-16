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

/// <summary>A time a field cell was bare from overuse (A1380 N2): from Start to End, or still (End null).</summary>
/// <param name="Start">The tick it went bare.</param>
/// <param name="End">The tick it was green again, or null while bare.</param>
public sealed record BareSpan(long Start, long? End);

/// <summary>The overuse of one field cell (A1380 N2) as saved: its harvests within the window and its bare times.</summary>
/// <param name="X">Field cell, east.</param>
/// <param name="Z">Field cell, south.</param>
/// <param name="Harvests">Ticks of the harvests still within the window.</param>
/// <param name="Bare">Its bare times still needed (open, or overlapping a place still regrowing).</param>
public sealed record OveruseCell(int X, int Z, IReadOnlyList<long> Harvests, IReadOnlyList<BareSpan> Bare);

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
    private readonly HashSet<string> _growing = new(StringComparer.Ordinal);
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
        if (objects is not null)
        {
            objects.Spawned += ObstacleTouched;
            objects.Removed += ObstacleTouched;

            // Review GW1 #515: a change between two obstacle types with other radii keeps the count.
            objects.TypeChanged += (before, after) =>
            {
                ObstacleTouched(before);
                ObstacleTouched(after);
            };
        }
    }

    /// <summary>
    /// Whether a used-up place has grown back (type, gone since, now, regrow ticks): the nature takt with seasons
    /// and drought (A1380); null = by the clock, as before.
    /// </summary>
    public Func<string, long, long, long, bool>? Grown { get; set; }

    /// <summary>Places that grow back, in the order they were placed.</summary>
    public IReadOnlyList<Occurrence> Occurrences => _occurrences;

    /// <summary>A1380 N2: the overuse data (nature settings); null = no overuse.</summary>
    public Contracts.NatureOveruse? Overuse { get; set; }

    /// <summary>A1380 N2: the growth of a type between two ticks in permille-ticks (the nature takt); null = by the clock.</summary>
    public Func<string, long, long, long>? GrowthBetween { get; set; }

    private readonly SortedDictionary<(int X, int Z), List<long>> _harvests = [];
    private readonly SortedDictionary<(int X, int Z), List<BareSpan>> _bare = [];

    /// <summary>A1380 N2: the field cells bare now (for the look, A1385), in order.</summary>
    public IEnumerable<(int X, int Z)> BareCells => _bare.Where(b => b.Value.Count > 0 && b.Value[^1].End is null).Select(b => b.Key);

    /// <summary>A1380 N2: whether the field cell of a position is bare now.</summary>
    public bool BareAt(Position at) =>
        Overuse is { } o && _bare.TryGetValue(FieldOf(at, o), out var spans) && spans.Count > 0 && spans[^1].End is null;

    /// <summary>A1380 N2: the overuse for a save game, in cell order (only cells with anything).</summary>
    public IReadOnlyList<OveruseCell> CaptureOveruse()
    {
        var cells = new SortedSet<(int X, int Z)>(_harvests.Keys.Concat(_bare.Keys));
        return [.. cells.Select(c => new OveruseCell(c.X, c.Z, _harvests.TryGetValue(c, out var h) ? [.. h] : [], _bare.TryGetValue(c, out var b) ? [.. b] : []))];
    }

    /// <summary>A1380 N2: restores the overuse of a save game.</summary>
    public void RestoreOveruse(IReadOnlyList<OveruseCell>? cells)
    {
        _harvests.Clear();
        _bare.Clear();
        foreach (var cell in cells ?? [])
        {
            if (cell.Harvests.Count > 0)
            {
                _harvests[(cell.X, cell.Z)] = [.. cell.Harvests];
            }

            if (cell.Bare.Count > 0)
            {
                _bare[(cell.X, cell.Z)] = [.. cell.Bare];
            }
        }
    }

    private static (int X, int Z) FieldOf(Position at, Contracts.NatureOveruse o) =>
        ((int)Math.Floor(at.X / o.CellM), (int)Math.Floor(at.Z / o.CellM));

    private (int X, int Z) FieldOf(Occurrence place, Contracts.NatureOveruse o) => FieldOf(CentreOf(place.X, place.Z), o);

    /// <summary>A harvest in a field cell: counted within the window; too many make it bare (A1380 N2).</summary>
    private void Harvested(Occurrence place, long tick, int ticksPerHour)
    {
        if (Overuse is not { } o)
        {
            return;
        }

        var cell = FieldOf(place, o);
        if (!_harvests.TryGetValue(cell, out var ticks))
        {
            _harvests[cell] = ticks = [];
        }

        ticks.Add(tick);
        ticks.RemoveAll(t => t <= tick - ((long)o.WindowHours * ticksPerHour));
        var bare = _bare.TryGetValue(cell, out var spans) && spans.Count > 0 && spans[^1].End is null;
        if (!bare && ticks.Count >= o.BareAfter)
        {
            if (spans is null)
            {
                _bare[cell] = spans = [];
            }

            spans.Add(new BareSpan(tick, null));
            ticks.Clear();
        }

        if (ticks.Count == 0)
        {
            _harvests.Remove(cell);
        }
    }

    /// <summary>
    /// Once an hour (A1380 N2): a bare cell all of whose places have grown back is green again (at the earliest after the
    /// data's hours); bare times no place still regrowing reaches back to are dropped, and old harvests leave the window.
    /// </summary>
    private void Recover(long tick, int ticksPerHour)
    {
        if (Overuse is not { } o || (_bare.Count == 0 && _harvests.Count == 0))
        {
            return;
        }

        var oldestGone = new Dictionary<(int X, int Z), long>();
        foreach (var place in _occurrences)
        {
            if (place.GoneSinceTick is { } gone)
            {
                var cell = FieldOf(place, o);
                oldestGone[cell] = oldestGone.TryGetValue(cell, out var was) ? Math.Min(was, gone) : gone;
            }
        }

        foreach (var cell in _bare.Keys.ToList())
        {
            var spans = _bare[cell];
            if (spans[^1] is { End: null } open && !oldestGone.ContainsKey(cell) && tick - open.Start >= (long)o.BareMinHours * ticksPerHour)
            {
                spans[^1] = open with { End = tick };
            }

            spans.RemoveAll(s => s.End is { } end && (!oldestGone.TryGetValue(cell, out var since) || end <= since));
            if (spans.Count == 0)
            {
                _bare.Remove(cell);
            }
        }

        foreach (var cell in _harvests.Keys.ToList())
        {
            _harvests[cell].RemoveAll(t => t <= tick - ((long)o.WindowHours * ticksPerHour));
            if (_harvests[cell].Count == 0)
            {
                _harvests.Remove(cell);
            }
        }
    }

    /// <summary>
    /// Whether a place gone since <paramref name="since"/> has grown back (A1380 N2, review GW1 #27201): the growth of its
    /// group since then, less what it lacked while its cell was bare (bare_growth_permille, only within the bare times).
    /// </summary>
    private bool GrownWithBare(Occurrence place, long since, long now, long regrowTicks, List<BareSpan> spans, Contracts.NatureOveruse o)
    {
        long Growth(long from, long to) => GrowthBetween?.Invoke(place.TypeId, from, to) ?? (Math.Max(0, to - from) * 1000);
        var full = Growth(since, now) * 1000;
        var lacking = 0L;
        foreach (var span in spans)
        {
            var from = Math.Max(span.Start, since);
            var to = Math.Min(span.End ?? now, now);
            if (to > from)
            {
                lacking += Growth(from, to) * (1000 - o.BareGrowthPermille);
            }
        }

        return full - lacking >= regrowTicks * 1_000_000;
    }

    /// <summary>Remembers a placed object if its type grows back.</summary>
    public void Track(WorldObject obj, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (_regrowTicks.ContainsKey(obj.TypeId))
        {
            _occurrences.Add(new Occurrence(obj.Id, obj.TypeId, x, z, null));
            _growing.Add(obj.Id);
        }
    }

    /// <summary>Whether an object stands at a regrowth place of the island now (a berry on its bush: it does not spoil there).</summary>
    public bool GrowsInPlace(string objectId) => _growing.Contains(objectId);

    /// <summary>
    /// D1105 H2: what hangs in a plant's crown, by type - height between min and max permille of the plant's height
    /// (centimetres), and the chance per day that it falls. Set by the host from the spawn data; empty = nothing hangs.
    /// </summary>
    public IReadOnlyDictionary<string, (int MinPermille, int MaxPermille, int FallsPerDayPermille, int PlantHeightCm)> Hangs { get; init; } =
        new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal);

    /// <summary>D1105 H3b: the plant a hanging type grows on and how far from it (cells, each axis), from the spawn data.</summary>
    public IReadOnlyDictionary<string, (string Plant, int WithinCells)> GrowsOn { get; init; } =
        new Dictionary<string, (string, int)>(StringComparer.Ordinal);

    private readonly List<WorldObject> _plants = [];

    /// <summary>D1105 H3b: the nearest plant of the kind a hanging thing grows on, within its spawn distance; null = none.</summary>
    public WorldObject? PlantOf(WorldObject thing)
    {
        ArgumentNullException.ThrowIfNull(thing);
        if (_objects is null || !GrowsOn.TryGetValue(thing.TypeId, out var grows))
        {
            return null;
        }

        _objects.Within(thing.Position, (grows.WithinCells + 1) * CellSizeCm / 100.0 * 1.5, _plants);
        WorldObject? best = null;
        var bestDistance = double.MaxValue;
        foreach (var plant in _plants)
        {
            if (!string.Equals(plant.TypeId, grows.Plant, StringComparison.Ordinal))
            {
                continue;
            }

            var d = ((plant.Position.X - thing.Position.X) * (plant.Position.X - thing.Position.X)) + ((plant.Position.Z - thing.Position.Z) * (plant.Position.Z - thing.Position.Z));
            if (d < bestDistance || (d == bestDistance && string.CompareOrdinal(plant.Id, best!.Id) < 0))
            {
                best = plant;
                bestDistance = d;
            }
        }

        _plants.Clear();
        return best;
    }

    /// <summary>
    /// D1105 H2: how high a thing hangs above the ground, in centimetres - only while it grows in a plant's crown;
    /// each at its own height, a pure function of its id (no state). 0 = on the ground.
    /// </summary>
    public int AboveCm(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (!_growing.Contains(objectId) || _objects is null || !_objects.TryGet(objectId, out var thing) || !Hangs.TryGetValue(thing.TypeId, out var hang))
        {
            return 0;
        }

        var span = Math.Max(0, hang.MaxPermille - hang.MinPermille);
        var share = hang.MinPermille + (span == 0 ? 0 : (int)(StableHash(objectId) % (uint)(span + 1)));
        return hang.PlantHeightCm * share / 1000;
    }

    /// <summary>A hash of a text that is the same on every machine and run (FNV-1a).</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    /// <summary>A thing that grew in its place was taken from it (picked, AK1a): its id and the tick. It ages from now on.</summary>
    public event Action<string, long>? LeftPlace;

    /// <summary>Restores the places that grow back (save games).</summary>
    public void Restore(IEnumerable<Occurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        _occurrences.Clear();
        _occurrences.AddRange(occurrences);
        _growing.Clear();
        // A place that is empty (eaten or picked, AK1a) has no thing growing there: a picked fruit ages after a load too.
        _growing.UnionWith(_occurrences.Where(o => o.GoneSinceTick is null).Select(o => o.ObjectId));
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
    /// A1790: the radius (centimetres) of the solid part on the ground of each type nobody walks through - a trunk,
    /// a rock - from the catalogue (obstacle_radius_cm). The path search goes around every cell such a circle
    /// covers; empty = nothing blocks, routes as before.
    /// </summary>
    public IReadOnlyDictionary<string, int> ObstacleRadiusCm { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// A1790: how far (metres) a walker's middle keeps from the edge of an obstacle where a walk to it ends - half
    /// the smallest distance between two inhabitants (gaits spacing), set by the host.
    /// </summary>
    public double BodyRadiusM { get; init; }

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

    /// <summary>
    /// Walk costs with the tide at the current tick, or null where the base grid counts. The array is the
    /// layer's shared one (<see cref="TideWalkLayer.CostsAt"/>): valid on the simulation thread only and
    /// until the tide level changes, so it is read right away and never kept (one-thread assumption).
    /// </summary>
    private int[]? TideCosts() => TideWalk is { } layer && Clock is { } clock ? layer.CostsAt(clock()) : null;

    /// <summary>Most searches kept at a time (<see cref="_paths"/>); beyond it all are forgotten.</summary>
    internal const int MostKeptPaths = 4096;

    private int? _minWalkCost;
    private PathFinder.Scratch? _scratch;

    // The searches of the tide level that holds now, by start and goal cell. A search is a pure function of
    // the map, the two cells, the budget and the walk costs; the costs change only when the tide level
    // changes by a whole unit (TideWalkLayer), so until then the same two cells give the same path - found,
    // unreachable or over budget - and it is taken from here instead of being searched again. Derived only:
    // never saved, never hashed, and a world that forgets it searches the same paths anew.
    private readonly Dictionary<(int From, int Goal, int Slack), PathResult> _paths = [];
    private int _pathsLevel;

    /// <summary>Path searches really run so far (not taken from the kept ones); for tests and measurements.</summary>
    internal long Searches { get; private set; }

    /// <summary>Forgets the kept searches (tests: a world that keeps none walks the same ways).</summary>
    internal void ForgetPaths() => _paths.Clear();

    // A1790: the cells the obstacles cover and the circles by the cell of their centre - derived from the objects
    // alone (never saved, never hashed), built again when an obstacle comes or goes; then the kept searches go too.
    private bool[]? _obstacles;
    private readonly Dictionary<int, List<(Position Centre, double RadiusM)>> _circles = [];
    private bool _obstaclesDirty = true;
    private int _obstacleCount = -1;
    private long _obstacleSignature;
    private double _widestObstacleM;

    // A destination this close to an obstacle's centre is the obstacle itself (positions are the same doubles).
    private const double CentreToleranceM = 0.01;

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private void ObstacleTouched(WorldObject thing)
    {
        if (ObstacleRadiusCm.ContainsKey(thing.TypeId))
        {
            _obstaclesDirty = true;
        }
    }

    /// <summary>A1790: the cells nobody walks through because an obstacle stands in them (index z × width + x); null = none.</summary>
    internal bool[]? Obstacles()
    {
        if (_objects is null || ObstacleRadiusCm.Count == 0)
        {
            return null;
        }

        // A signature over every obstacle as well - where it stands and whether someone holds it: a move fires no
        // event (a young tree picked up goes with its holder), so the cells must never lag behind the objects, or a
        // loaded world (which builds them fresh) would walk other ways than the one that ran on.
        var count = 0;
        var signature = 17L;
        foreach (var ofType in _objects.ByType)
        {
            if (!ObstacleRadiusCm.TryGetValue(ofType.TypeId, out var radius) || radius <= 0)
            {
                continue;
            }

            foreach (var thing in ofType.Objects)
            {
                count++;
                var held = _objects.HeldBy(thing.Id) is null ? 0L : 1L;
                signature = unchecked((signature * 1_000_003) + (BitConverter.DoubleToInt64Bits(thing.Position.X) * 31)
                    + BitConverter.DoubleToInt64Bits(thing.Position.Z) + held);
            }
        }

        if (!_obstaclesDirty && count == _obstacleCount && signature == _obstacleSignature)
        {
            return _obstacles;
        }

        _obstaclesDirty = false;
        _obstacleCount = count;
        _obstacleSignature = signature;
        _circles.Clear();
        _widestObstacleM = 0;
        _paths.Clear();
        var w = Map.WidthCells;
        var cells = new bool[w * Map.DepthCells];
        var cellM = CellSizeCm / 100.0;
        var any = false;
        foreach (var ofType in _objects.ByType)
        {
            if (!ObstacleRadiusCm.TryGetValue(ofType.TypeId, out var radiusCm) || radiusCm <= 0)
            {
                continue;
            }

            var r = radiusCm / 100.0;
            foreach (var thing in ofType.Objects)
            {
                // What someone holds blocks nothing (it goes where its holder goes).
                if (_objects.HeldBy(thing.Id) is not null || CellOf(thing.Position) is not { } at)
                {
                    continue;
                }

                any = true;
                var key = (at.Z * w) + at.X;
                if (!_circles.TryGetValue(key, out var here))
                {
                    _circles[key] = here = [];
                }

                here.Add((thing.Position, r));
                _widestObstacleM = Math.Max(_widestObstacleM, r);
                var reach = (int)Math.Ceiling(r / cellM);
                for (var z = at.Z - reach; z <= at.Z + reach; z++)
                {
                    for (var x = at.X - reach; x <= at.X + reach; x++)
                    {
                        // A cell is taken when the circle reaches into it: the point of the cell nearest the centre lies inside.
                        var nearX = Math.Clamp(thing.Position.X, x * cellM, (x + 1) * cellM) - thing.Position.X;
                        var nearZ = Math.Clamp(thing.Position.Z, z * cellM, (z + 1) * cellM) - thing.Position.Z;
                        if (Map.Contains(x, z) && (nearX * nearX) + (nearZ * nearZ) < r * r)
                        {
                            cells[(z * w) + x] = true;
                        }
                    }
                }
            }
        }

        _obstacles = any ? cells : null;
        return _obstacles;
    }

    /// <summary>
    /// A1790: the obstacle a position lies within, counting the walker's body (its centre and that clearance), or null.
    /// Of several, the one whose centre lies nearest (then the first placed).
    /// </summary>
    private (Position Centre, double Clear)? ObstacleAt(Position destination)
    {
        if (_circles.Count == 0 || CellOf(destination) is not { } cell)
        {
            return null;
        }

        (Position Centre, double Clear)? best = null;
        var bestDistance = double.MaxValue;
        var w = Map.WidthCells;
        var cellM = CellSizeCm / 100.0;
        var reach = 1 + (int)Math.Ceiling((_widestObstacleM + BodyRadiusM) / cellM);
        for (var z = cell.Z - reach; z <= cell.Z + reach; z++)
        {
            for (var x = cell.X - reach; x <= cell.X + reach; x++)
            {
                if (!Map.Contains(x, z) || !_circles.TryGetValue((z * w) + x, out var here))
                {
                    continue;
                }

                foreach (var (centre, radius) in here)
                {
                    var clear = radius + BodyRadiusM;
                    var d = Distance(destination, centre);
                    if (d < clear && (d < bestDistance || (d == bestDistance && best is { } kept && (centre.Z, centre.X).CompareTo((kept.Centre.Z, kept.Centre.X)) < 0)))
                    {
                        best = (centre, clear);
                        bestDistance = d;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>A1790: the point in front of an obstacle towards <paramref name="from"/> (on the centre: towards the destination, else east).</summary>
    private static Position InFront((Position Centre, double Clear) obstacle, Position from, Position destination)
    {
        var (dx, dz) = (from.X - obstacle.Centre.X, from.Z - obstacle.Centre.Z);
        var length = Math.Sqrt((dx * dx) + (dz * dz));
        if (length < 1e-6)
        {
            (dx, dz) = (destination.X - obstacle.Centre.X, destination.Z - obstacle.Centre.Z);
            length = Math.Sqrt((dx * dx) + (dz * dz));
            if (length < 1e-6)
            {
                (dx, dz, length) = (1, 0, 1);
            }
        }

        return new Position(obstacle.Centre.X + (dx / length * obstacle.Clear), obstacle.Centre.Z + (dz / length * obstacle.Clear));
    }

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
        var obstacles = Obstacles();

        // A1790: going to a trunk or a rock or to something right by it, its own cells are open near the goal (it may
        // cover the cells around).
        var target = ObstacleAt(destination);
        var slack = target is { } around ? (int)Math.Ceiling(around.Clear * 100 / CellSizeCm) : 0;

        // With tides the shallow sea costs what the water level makes of it (never blocked, see TideWalkLayer).
        int[]? costs = null;
        var level = 0;
        if (TideWalk is { } layer && Clock is { } clock)
        {
            var tick = clock();
            costs = layer.CostsAt(tick);
            level = layer.Tides.LevelUnitsAt(tick);
        }

        if (level != _pathsLevel || _paths.Count >= MostKeptPaths)
        {
            _paths.Clear();
            _pathsLevel = level;
        }

        var w = Map.WidthCells;
        var key = ((from.Z * w) + from.X, (goal.Z * w) + goal.X, slack);
        if (!_paths.TryGetValue(key, out var path))
        {
            Searches++;
            path = PathFinder.Find(Map, from, goal, NodeBudget, costs is null ? _minWalkCost.Value : Math.Min(_minWalkCost.Value, TideWalk!.MinCost), _scratch, costs, obstacles, slack);
            _paths.Add(key, path);
        }

        if (path.Status != PathStatus.Found)
        {
            return new Route(path.Status, []);
        }

        // Cell centres, the last one the exact destination. The kept cells are only read.
        var cells = path.Cells;
        var points = new List<Position>(Math.Max(1, cells.Count));
        for (var i = 0; i < cells.Count - 1; i++)
        {
            points.Add(CentreOf(cells[i].X, cells[i].Z));
        }

        // A1790: a walk to the trunk or the rock itself (its centre) ends in front of it, not inside: the cell centres
        // within its clearance go, and the walk ends that far from its centre on the side it comes from (a straight
        // line towards it). A walk to something lying by it goes to that thing, so it stays within the action's reach.
        if (target is { } obstacle && Distance(destination, obstacle.Centre) < CentreToleranceM)
        {
            while (points.Count > 0 && Distance(points[^1], obstacle.Centre) < obstacle.Clear)
            {
                points.RemoveAt(points.Count - 1);
            }

            points.Add(InFront(obstacle, points.Count > 0 ? points[^1] : start, destination));
            return new Route(PathStatus.Found, points);
        }

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

    /// <summary>
    /// The walkable area the cell (x, z) belongs to; 0 = it cannot be walked on or lies outside the map.
    /// Cells of one area can be reached from each other on foot (cheap, no search).
    /// </summary>
    public int AreaOf(int x, int z)
    {
        _areas ??= PathFinder.WalkableAreas(Map);
        return Map.Contains(x, z) ? _areas[(z * Map.WidthCells) + x] : 0;
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

    /// <summary>
    /// A1498 (B2828): the nearest dry ground to a position within a distance - the position itself when its cell is dry
    /// land, else the centre of the nearest dry cell that can be walked on in the same walkable area (ties by cell). Shallow
    /// water belongs to the area of its banks, so the dry cell is one the walker can reach - never across deep water (review
    /// GW3 #507). Null when there is none within the distance, the position lies outside the map, or it stands on ground
    /// nobody can walk (area 0): then it sleeps where it stands, and no walk can fail and be tried again tick after tick.
    /// </summary>
    public Position? DryGroundNear(Position at, double withinM)
    {
        if (CellOf(at) is not { } here)
        {
            return null;
        }

        if (Map.Cell(here.X, here.Z).Water == WaterKind.None)
        {
            return at;
        }

        var area = AreaOf(here.X, here.Z);
        if (area == 0)
        {
            return null;
        }

        var cells = (int)Math.Ceiling(withinM * 100 / CellSizeCm);
        Position? best = null;
        var bestDistance = double.MaxValue;
        for (var z = here.Z - cells; z <= here.Z + cells; z++)
        {
            for (var x = here.X - cells; x <= here.X + cells; x++)
            {
                if (!Map.Contains(x, z) || Map.Cell(x, z).Water != WaterKind.None || Map.Cell(x, z).WalkCost == MapCell.Blocked
                    || AreaOf(x, z) != area)
                {
                    continue;
                }

                var centre = CentreOf(x, z);
                var distance = Math.Sqrt(((centre.X - at.X) * (centre.X - at.X)) + ((centre.Z - at.Z) * (centre.Z - at.Z)));
                if (distance <= withinM && distance < bestDistance)
                {
                    best = centre;
                    bestDistance = distance;
                }
            }
        }

        return best;
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

        if (world.Tick % world.Calendar.Settings.TicksPerHour == 0)
        {
            Recover(world.Tick, world.Calendar.Settings.TicksPerHour);
        }

        for (var i = 0; i < _occurrences.Count; i++)
        {
            var place = _occurrences[i];
            if (place.GoneSinceTick is null)
            {
                // Eaten, or picked (held, AK1a): the place is empty and grows again; a picked fruit ages like any thing.
                if (!_objects.TryGet(place.ObjectId, out _) || _objects.HeldBy(place.ObjectId) is not null)
                {
                    _occurrences[i] = place with { GoneSinceTick = world.Tick };
                    Harvested(place, world.Tick, world.Calendar.Settings.TicksPerHour);
                    if (_growing.Remove(place.ObjectId) && _objects.TryGet(place.ObjectId, out _))
                    {
                        LeftPlace?.Invoke(place.ObjectId, world.Tick);
                    }
                }

                // D1105 H2: what hangs in a crown falls with the data's chance per day (checked each hour, stream
                // nature/fall); it lies on the ground then, ages, and the plant grows a new one.
                if (Hangs.TryGetValue(place.TypeId, out var hang) && hang.FallsPerDayPermille > 0 && _growing.Contains(place.ObjectId)
                    && world.Tick % world.Calendar.Settings.TicksPerHour == 0
                    && world.Random.Get("nature/fall").NextInt(0, 24_000) < hang.FallsPerDayPermille)
                {
                    _occurrences[i] = place with { GoneSinceTick = world.Tick };
                    _growing.Remove(place.ObjectId);
                    LeftPlace?.Invoke(place.ObjectId, world.Tick);
                }

                continue;
            }

            if (Overuse is { } overuse && _bare.TryGetValue(FieldOf(place, overuse), out var bareSpans)
                ? GrownWithBare(place, place.GoneSinceTick.Value, world.Tick, _regrowTicks[place.TypeId], bareSpans, overuse)
                : Grown?.Invoke(place.TypeId, place.GoneSinceTick.Value, world.Tick, _regrowTicks[place.TypeId])
                    ?? world.Tick - place.GoneSinceTick.Value >= _regrowTicks[place.TypeId])
            {
                var grown = _objects.Spawn(place.TypeId, CentreOf(place.X, place.Z));
                _occurrences[i] = place with { ObjectId = grown.Id, GoneSinceTick = null };
                _growing.Remove(place.ObjectId);
                _growing.Add(grown.Id);
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

        // A1380 N2: overuse only while there is any (worlds without it keep their hashes).
        if (_harvests.Count > 0 || _bare.Count > 0)
        {
            hasher.Write("overuse");
            foreach (var cell in CaptureOveruse())
            {
                hasher.Write(cell.X).Write(cell.Z).Write(cell.Harvests.Count);
                foreach (var t in cell.Harvests)
                {
                    hasher.Write(t);
                }

                hasher.Write(cell.Bare.Count);
                foreach (var span in cell.Bare)
                {
                    hasher.Write(span.Start).Write(span.End ?? -1);
                }
            }
        }
    }
}
