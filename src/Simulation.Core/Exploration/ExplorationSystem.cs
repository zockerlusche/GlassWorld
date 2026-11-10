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
public sealed record ExplorationState(int Width, int Depth, byte[] Player, IReadOnlyList<KeyValuePair<string, byte[]>> Seen)
{
    /// <summary>
    /// The last look of each inhabitant, by inhabitant id in ordinal order (B2594). Null or without an
    /// inhabitant in save games from before the sight range could change: whoever has no last look looks
    /// again when the world goes on.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, LastLook>>? Looks { get; init; }
}

/// <summary>Where an inhabitant last looked around from and how far it saw then (B2594).</summary>
/// <param name="Cell">The cell it stood in (index <c>z × Width + x</c>).</param>
/// <param name="RangeCells">Its sight range at that look, in cells.</param>
public readonly record struct LastLook(int Cell, int RangeCells);

/// <summary>
/// Cloud cover and exploring (B2-6 R14, name "exploration"): which cells of the island have been seen.
/// <list type="bullet">
/// <item>Every inhabitant has its own set of seen cells; a child born later starts with an empty one.</item>
/// <item>The player's view is the union of everything any inhabitant has ever seen. Cells are only added:
/// new land stays new land, whoever saw it.</item>
/// <item>Seeing is a radius over the grid (the preset's <c>exploration</c> section), wider where the inhabitant
/// stands higher than its surroundings and shorter in weather that lowers the sight (<see cref="Sight"/>,
/// B2594). Nothing blocks the view; no rays are cast. An inhabitant looks around when it has entered
/// another cell and when its sight range has changed (the weather changed), in whole cells (integer maths).
/// What was seen stays seen: fog reveals less, and when it clears everyone looks again from where it stands.</item>
/// <item>For the routine: <see cref="Frontier"/> names the nearest seen dry cell an inhabitant can walk to
/// that lies next to an unseen one. Whether and how much inhabitants explore is data (action destination
/// <c>frontier</c>, weights in content/behaviour/utility.json).</item>
/// </list>
/// State (hashed and saved): the player's view, the sets per inhabitant and each inhabitant's last look
/// (cell and range: whether it looks again depends on it, so a loaded world goes on exactly like the one
/// that was never saved). Everything else here is a cache that follows from the state, the immutable map
/// and the data. The code names no action.
/// </summary>
public sealed class ExplorationSystem : IWorldSystem, IOptionalWorldState
{
    private static readonly (int Dx, int Dz)[] Sides = [(1, 0), (0, -1), (-1, 0), (0, 1)];

    private readonly WorldMapSystem _map;
    private readonly InhabitantsSystem _inhabitants;
    private readonly int _width;
    private readonly int _depth;
    private readonly int _seaLevel;
    private readonly int _rangeCells;
    private readonly int _surroundingsCells;
    private readonly int _bonusPer100Height;
    private readonly int _maxBonusCells;

    // Inhabitants are never taken out of the world today, and neither are their sets: the cells of someone
    // who is gone would stay in state, hash and save (one bit per cell, about 4.6 kB each on the start
    // island). B9 (generations) must decide when to drop them; the player's view keeps everything anyway.
    private readonly SortedDictionary<string, CellSet> _seen = new(StringComparer.Ordinal);
    private CellSet _player;

    // State: the cell each inhabitant last looked from and its sight range then.
    private readonly SortedDictionary<string, LastLook> _lookedFrom = new(StringComparer.Ordinal);

    // Caches (not state): the sight range per cell in full sight (data and height, never the weather), the
    // rows of a circle per radius, the cells in the order they were revealed, where nothing is left to explore.
    private readonly Dictionary<string, (int Seen, int Area)> _nothingLeft = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int[]> _circles = [];
    private readonly List<int> _revealed = [];
    private readonly short[] _rangeAt;
    private int[]? _stamp;

    /// <summary>What lowers the sight range right now: the inhabitants' sight (the weather; B5 adds the light).</summary>
    private Sight Sight => _inhabitants.Sight;
    private int[]? _queue;
    private int _search;

    /// <summary>Creates the system for an island; nothing is seen yet.</summary>
    /// <param name="data">Sight range and height bonus in cells: the <c>exploration</c> section of the world preset, the same values the start check uses.</param>
    /// <param name="map">The island.</param>
    /// <param name="inhabitants">Who looks.</param>
    /// <param name="seaLevel">Sea level in height units (preset); water counts with its surface there.</param>
    public ExplorationSystem(WorldPresetExploration data, WorldMapSystem map, InhabitantsSystem inhabitants, int seaLevel)
    {
        ArgumentNullException.ThrowIfNull(data);
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _width = map.Map.WidthCells;
        _depth = map.Map.DepthCells;
        _seaLevel = seaLevel;
        _rangeCells = Math.Max(1, data.ViewRangeCells);
        _surroundingsCells = Math.Max(1, data.SurroundingsRadiusCells);
        _bonusPer100Height = data.HeightBonusPer100Height;
        _maxBonusCells = data.MaxHeightBonusCells;
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
    /// How far one sees from the cell (x, z) right now, in cells: the range of the data plus the height
    /// bonus there, as far as the weather lets one see (<see cref="Sight"/>; B5 adds the light there), at
    /// least one cell. The one place the range comes from.
    /// </summary>
    public int SightRangeCells(int x, int z) => Sight.Cells(FullRangeCells(x, z));

    /// <summary>The range from the cell (x, z) in full sight: the data plus the height bonus there (kept per cell).</summary>
    private int FullRangeCells(int x, int z)
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
    /// Everyone who stands in another cell than at its last look, or sees another distance than then (the
    /// weather changed), sees the cells around it. Called every tick after the weather and after the
    /// inhabitants have moved, and once when a world starts or a save game from before is loaded.
    /// </summary>
    public void Look()
    {
        foreach (var inhabitant in _inhabitants.All)
        {
            if (_map.CellOf(inhabitant.Position) is not { } cell)
            {
                continue;
            }

            var look = new LastLook((cell.Z * _width) + cell.X, SightRangeCells(cell.X, cell.Z));
            var looked = _lookedFrom.TryGetValue(inhabitant.Id, out var last);
            if (looked && last == look)
            {
                continue;
            }

            _lookedFrom[inhabitant.Id] = look;
            if (!_seen.TryGetValue(inhabitant.Id, out var own))
            {
                own = new CellSet(_width, _depth);
                _seen.Add(inhabitant.Id, own);
            }

            var range = look.RangeCells;
            if (looked && last.Cell == look.Cell && range < last.RangeCells)
            {
                // The sight got shorter where it stands: nothing new to see, the look is only noted.
                continue;
            }

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
    /// Where the land an inhabitant has not seen yet begins: the centre of the nearest dry cell (in steps
    /// over cells it has seen and can walk on, in its own walkable area) that lies next to a cell it has
    /// not seen. Null when nothing is left to explore on foot or it has not looked around yet. It reads
    /// the inhabitant's own seen cells, never the player's view.
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
            // The cell to walk to is dry land: the way may lead through a ford, but exploring does not
            // send anyone into the shallow sea to uncover more sea.
            if (seen.Contains(index) && _map.Map.Cell(x, z).Water == WaterKind.None && AtEdge(seen, x, z))
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

        hasher.Write(_lookedFrom.Count);
        foreach (var (id, look) in _lookedFrom)
        {
            hasher.Write(id).Write(look.Cell).Write(look.RangeCells);
        }
    }

    /// <summary>
    /// The state to save. Always written, also while nothing has been seen: a save game without it is one
    /// from before the cloud cover.
    /// </summary>
    public ExplorationState Capture() =>
        new(_width, _depth, _player.ToBytes(), [.. _seen.Select(s => new KeyValuePair<string, byte[]>(s.Key, s.Value.ToBytes()))])
        {
            Looks = [.. _lookedFrom],
        };

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

        // The last looks as saved, so nobody looks again who would not have in the world that was never
        // saved. A save game from before has none: there everyone looks again at the next tick.
        _lookedFrom.Clear();
        foreach (var (id, look) in state.Looks ?? [])
        {
            if (look.Cell < 0 || look.Cell >= _width * _depth || look.RangeCells < 1)
            {
                throw new InvalidDataException($"Save game last look of '{id}' (cell {look.Cell}, range {look.RangeCells}) does not fit the island.");
            }

            _lookedFrom.Add(id, look);
        }

        // The caches follow the state: the reveal order is not saved, a loaded view counts in grid order.
        _nothingLeft.Clear();
        _revealed.Clear();
        _player.CopyTo(_revealed);
    }

    /// <summary>
    /// Extra sight in cells where the ground at (x, z) is higher than the mean of its surroundings (water
    /// counts with its surface at sea level). Whole numbers only.
    /// </summary>
    private int HeightBonusCells(int x, int z)
    {
        if (_bonusPer100Height == 0 || _maxBonusCells == 0)
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

        return (int)Math.Min(_maxBonusCells, above * _bonusPer100Height / 100);
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
