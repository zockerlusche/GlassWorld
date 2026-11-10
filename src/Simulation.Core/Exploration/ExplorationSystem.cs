using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Exploration;

/// <summary>The seen cells as a save game keeps them (B2-6 R14): the player's view and one set per inhabitant.</summary>
/// <param name="Width">Width of the grid in cells.</param>
/// <param name="Depth">Depth of the grid in cells.</param>
/// <param name="Player">The player's view (<see cref="CellSet.ToBytes"/>).</param>
/// <param name="Seen">The cells each inhabitant has seen, by inhabitant id in ordinal order.</param>
public sealed record ExplorationState(int Width, int Depth, byte[] Player, IReadOnlyList<KeyValuePair<string, byte[]>> Seen);

/// <summary>
/// Cloud cover and exploring (B2-6 R14, name "exploration"): which cells of the island have been seen.
/// <list type="bullet">
/// <item>Every inhabitant has its own set of seen cells; a child born later starts with an empty one.</item>
/// <item>The player's view is the union of everything any inhabitant has ever seen. Cells are only added:
/// new land stays new land, whoever saw it.</item>
/// <item>Seeing is a radius over the grid (content/world/exploration.json), wider where the inhabitant
/// stands higher than its surroundings. Nothing blocks the view; no rays are cast. An inhabitant looks
/// around only when it has entered another cell, in whole cells (integer maths).</item>
/// <item>For the routine: <see cref="Frontier"/> names the nearest seen cell an inhabitant can walk to that
/// lies next to an unseen one. Whether and how much inhabitants explore is data (action destination
/// <c>frontier</c>, weights in content/behaviour/utility.json).</item>
/// </list>
/// State (hashed and saved): the player's view and the sets per inhabitant. Everything else here is a
/// cache that follows from the state, the immutable map and the data. The code names no action.
/// </summary>
public sealed class ExplorationSystem : IWorldSystem, IOptionalWorldState
{
    private static readonly (int Dx, int Dz)[] Sides = [(1, 0), (0, -1), (-1, 0), (0, 1)];

    private readonly WorldMapSystem _map;
    private readonly InhabitantsSystem _inhabitants;
    private readonly int _width;
    private readonly int _depth;
    private readonly int _seaLevel;
    private readonly int _heightUnitMm;
    private readonly int _rangeCells;
    private readonly int _surroundingsCells;
    private readonly int _rangePerHeight;
    private readonly int _maxBonusCells;
    private readonly SortedDictionary<string, CellSet> _seen = new(StringComparer.Ordinal);
    private CellSet _player;

    // Caches (not state): the cell each inhabitant last looked from, the sight range per cell, the rows of
    // a circle per radius, the cells in the order they were revealed, where nothing is left to explore.
    private readonly Dictionary<string, int> _lookedFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Seen, int Area)> _nothingLeft = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int[]> _circles = [];
    private readonly List<int> _revealed = [];
    private readonly short[] _rangeAt;
    private int[]? _stamp;
    private int[]? _queue;
    private int _search;

    /// <summary>Creates the system for an island; nothing is seen yet.</summary>
    /// <param name="data">Sight range and height bonus (content/world/exploration.json).</param>
    /// <param name="map">The island.</param>
    /// <param name="inhabitants">Who looks.</param>
    /// <param name="seaLevel">Sea level in height units (preset); water counts with its surface there.</param>
    /// <param name="heightUnitMm">Millimetres per height unit (preset).</param>
    public ExplorationSystem(ExplorationSettings data, WorldMapSystem map, InhabitantsSystem inhabitants, int seaLevel, int heightUnitMm)
    {
        ArgumentNullException.ThrowIfNull(data);
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        ArgumentOutOfRangeException.ThrowIfLessThan(heightUnitMm, 1);
        _width = map.Map.WidthCells;
        _depth = map.Map.DepthCells;
        _seaLevel = seaLevel;
        _heightUnitMm = heightUnitMm;
        _rangeCells = Cells(data.SightRangeM);
        _surroundingsCells = Cells(data.HeightBonus.SurroundingsRadiusM);
        _rangePerHeight = data.HeightBonus.RangeMPerHeightM;
        _maxBonusCells = data.HeightBonus.MaxBonusM * 100 / map.CellSizeCm;
        _player = new CellSet(_width, _depth);
        _rangeAt = new short[_width * _depth];
    }

    /// <inheritdoc/>
    public string Name => "exploration";

    /// <summary>Width of the grid in cells.</summary>
    public int Width => _width;

    /// <summary>Depth of the grid in cells.</summary>
    public int Depth => _depth;

    /// <inheritdoc/>
    public bool HasState => _player.Count > 0 || _seen.Count > 0;

    /// <summary>
    /// Version of the player's view: the number of cells revealed so far. It only grows; the presentation
    /// keeps the version it has drawn and asks <see cref="RevealedSince"/> for what came after.
    /// </summary>
    public int Version => _player.Count;

    /// <summary>Whether the player's view shows the cell (x, z).</summary>
    public bool IsRevealed(int x, int z) => _player.Contains(x, z);

    /// <summary>
    /// The cells (index <c>z × Width + x</c>) revealed after the player's view had <paramref name="version"/>
    /// cells, as a copy; version 0 gives the whole view. Like the snapshot it is asked on the simulation thread.
    /// </summary>
    public int[] RevealedSince(int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return version >= _revealed.Count ? [] : [.. _revealed.Skip(version)];
    }

    /// <summary>Number of cells an inhabitant has seen itself (0 for someone who has not looked yet).</summary>
    public int SeenCount(string inhabitantId) => _seen.TryGetValue(inhabitantId, out var seen) ? seen.Count : 0;

    /// <summary>Whether an inhabitant has seen the cell (x, z) itself.</summary>
    public bool HasSeen(string inhabitantId, int x, int z) => _seen.TryGetValue(inhabitantId, out var seen) && seen.Contains(x, z);

    /// <summary>
    /// How far one sees from the cell (x, z), in cells: the range of the data plus the height bonus there.
    /// The one place the range comes from (B5 will make it depend on the light).
    /// </summary>
    public int SightRangeCells(int x, int z)
    {
        var index = (z * _width) + x;
        if (_rangeAt[index] == 0)
        {
            _rangeAt[index] = (short)Math.Min(short.MaxValue, _rangeCells + HeightBonusCells(x, z));
        }

        return _rangeAt[index];
    }

    /// <inheritdoc/>
    public void Advance(World world) => Look();

    /// <summary>
    /// Everyone who stands in another cell than at its last look sees the cells around it. Called every tick
    /// after the inhabitants have moved, and once when a world starts or a save game from before is loaded.
    /// </summary>
    public void Look()
    {
        foreach (var inhabitant in _inhabitants.All)
        {
            if (_map.CellOf(inhabitant.Position) is not { } cell)
            {
                continue;
            }

            var index = (cell.Z * _width) + cell.X;
            if (_lookedFrom.TryGetValue(inhabitant.Id, out var last) && last == index)
            {
                continue;
            }

            _lookedFrom[inhabitant.Id] = index;
            if (!_seen.TryGetValue(inhabitant.Id, out var own))
            {
                own = new CellSet(_width, _depth);
                _seen.Add(inhabitant.Id, own);
            }

            var range = SightRangeCells(cell.X, cell.Z);
            var half = Circle(range);
            for (var dz = -range; dz <= range; dz++)
            {
                var z = cell.Z + dz;
                if (z < 0 || z >= _depth)
                {
                    continue;
                }

                var reach = half[Math.Abs(dz)];
                var from = Math.Max(0, cell.X - reach);
                var to = Math.Min(_width - 1, cell.X + reach);
                own.AddRow(z, from, to);
                _player.AddRow(z, from, to, _revealed);
            }
        }
    }

    /// <summary>
    /// Where the land an inhabitant has not seen yet begins: the centre of the nearest cell (in steps over
    /// cells it has seen and can walk on, in its own walkable area) that lies next to a cell it has not seen.
    /// Null when nothing is left to explore on foot or it has not looked around yet. It reads the
    /// inhabitant's own seen cells, never the player's view.
    /// </summary>
    public Position? Frontier(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (!_seen.TryGetValue(inhabitant.Id, out var seen) || _map.CellOf(inhabitant.Position) is not { } start)
        {
            return null;
        }

        var area = _map.AreaOf(start.X, start.Z);
        if (area == 0 || (_nothingLeft.TryGetValue(inhabitant.Id, out var known) && known == (seen.Count, area)))
        {
            return null;
        }

        _stamp ??= new int[_width * _depth];
        _queue ??= new int[_width * _depth];
        _search++;
        var head = 0;
        var tail = 0;
        var first = (start.Z * _width) + start.X;
        _stamp[first] = _search;
        _queue[tail++] = first;
        while (head < tail)
        {
            var index = _queue[head++];
            var x = index % _width;
            var z = index / _width;
            if (seen.Contains(index) && AtEdge(seen, x, z))
            {
                return _map.CentreOf(x, z);
            }

            foreach (var (dx, dz) in FlowDirections.All)
            {
                var nx = x + dx;
                var nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= _width || nz >= _depth)
                {
                    continue;
                }

                var next = (nz * _width) + nx;
                if (_stamp[next] != _search && seen.Contains(next) && _map.AreaOf(nx, nz) == area)
                {
                    _stamp[next] = _search;
                    _queue[tail++] = next;
                }
            }
        }

        // Nothing until the inhabitant sees more or stands in another area: no search again before that.
        _nothingLeft[inhabitant.Id] = (seen.Count, area);
        return null;
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        _player.WriteTo(hasher);
        hasher.Write(_seen.Count);
        foreach (var (id, seen) in _seen)
        {
            hasher.Write(id);
            seen.WriteTo(hasher);
        }
    }

    /// <summary>
    /// The state to save. Always written, also while nothing has been seen: a save game without it is one
    /// from before the cloud cover.
    /// </summary>
    public ExplorationState Capture() =>
        new(_width, _depth, _player.ToBytes(), [.. _seen.Select(s => new KeyValuePair<string, byte[]>(s.Key, s.Value.ToBytes()))]);

    /// <summary>Restores saved state; it must fit the grid of this island.</summary>
    public void Restore(ExplorationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Width != _width || state.Depth != _depth)
        {
            throw new InvalidDataException($"Save game seen cells are for a grid of {state.Width} x {state.Depth} cells, the island has {_width} x {_depth}.");
        }

        _player = CellSet.FromBytes(_width, _depth, state.Player);
        _seen.Clear();
        foreach (var (id, bytes) in state.Seen)
        {
            _seen.Add(id, CellSet.FromBytes(_width, _depth, bytes));
        }

        // The caches follow the state: the reveal order is not saved, a loaded view counts in grid order.
        _lookedFrom.Clear();
        _nothingLeft.Clear();
        _revealed.Clear();
        _player.CopyTo(_revealed);
    }

    private int Cells(int metres) => Math.Max(1, metres * 100 / _map.CellSizeCm);

    /// <summary>
    /// Extra sight in cells where the ground at (x, z) is higher than the mean of its surroundings (water
    /// counts with its surface at sea level). Whole numbers only.
    /// </summary>
    private int HeightBonusCells(int x, int z)
    {
        if (_rangePerHeight == 0 || _maxBonusCells == 0)
        {
            return 0;
        }

        var half = Circle(_surroundingsCells);
        long sum = 0;
        var count = 0;
        for (var dz = -_surroundingsCells; dz <= _surroundingsCells; dz++)
        {
            var row = z + dz;
            if (row < 0 || row >= _depth)
            {
                continue;
            }

            var reach = half[Math.Abs(dz)];
            for (var column = Math.Max(0, x - reach); column <= Math.Min(_width - 1, x + reach); column++)
            {
                sum += Surface(column, row);
                count++;
            }
        }

        var above = Surface(x, z) - (sum / count);
        if (above <= 0)
        {
            return 0;
        }

        // Height units → millimetres → centimetres of extra sight → cells.
        var bonusCm = above * _heightUnitMm * _rangePerHeight / 10;
        return (int)Math.Min(_maxBonusCells, bonusCm / _map.CellSizeCm);
    }

    private int Surface(int x, int z) => Math.Max(_map.Map.Cell(x, z).Height, _seaLevel);

    /// <summary>Half widths of the rows of a circle of this radius in cells (row 0 = through the centre).</summary>
    private int[] Circle(int radius)
    {
        if (_circles.TryGetValue(radius, out var half))
        {
            return half;
        }

        half = new int[radius + 1];
        var reach = radius;
        for (var dz = 0; dz <= radius; dz++)
        {
            while ((reach * reach) + (dz * dz) > radius * radius)
            {
                reach--;
            }

            half[dz] = reach;
        }

        _circles.Add(radius, half);
        return half;
    }

    /// <summary>Whether a cell lies next to (east, north, west, south) a cell of the grid that is not in the set.</summary>
    private bool AtEdge(CellSet seen, int x, int z)
    {
        foreach (var (dx, dz) in Sides)
        {
            var nx = x + dx;
            var nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < _width && nz < _depth && !seen.Contains((nz * _width) + nx))
            {
                return true;
            }
        }

        return false;
    }
}
